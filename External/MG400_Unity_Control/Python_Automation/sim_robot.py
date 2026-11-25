"""
sim_robot.py -- Virtual Dobot MG400 used when the physical arm is not
available (--sim mode of bridge_server.py).

It accepts the same ASCII dashboard commands as the real robot, runs a
speed-limited joint interpolation, computes forward kinematics with the
same geometry constants as the Unity digital twin, and emits realtime
JSON lines identical in format to port 29901.

Usage is normally indirect: bridge_server.py --sim  (creates + drives it).
"""

from __future__ import annotations

import math
import re
import threading
import time

from mg400_driver import GEOMETRY, fk, ik

# joint speed limits (deg/s) at SpeedFactor(100)
JOINT_SPEEDS = (150.0, 150.0, 150.0, 300.0)
# rough joint limits (deg) for safety clamp (twin convention, see mg400_driver)
JOINT_LIMITS = ((-140, 140), (-30, 210), (-100, 240), (-185, 185))


def real_to_twin(x, y, z):
    """Real base frame (X forward, Y left, Z up) -> twin frame (x side, y up, z forward)."""
    return y, z, x


def twin_to_real(tx, ty, tz):
    return tz, tx, ty


class SimMG400:
    """Thread-safe virtual MG400. Cartesian commands/poses use the REAL base frame (Z up)."""

    def __init__(self):
        self.move_scale = 1.0                      # per-move SpeedJ/SpeedL cap (fraction)
        self.lock = threading.Lock()
        self.joints = [0.0, 90.0, 90.0, 0.0]      # "home-ish" upright pose
        self.targets = list(self.joints)
        self.speed_factor = 100
        self.enabled = False
        self.homed = True
        self.vacuum = False
        self.do_outputs = [0] * 16
        self.error = ""
        self._last_step = time.time()
        self._arrived_event = threading.Event()
        self._arrived_event.set()                  # already at target at start
        self._cond = threading.Condition(self.lock)

    # ------------------------------------------------------------------ #
    # motion engine
    # ------------------------------------------------------------------ #
    def _step(self) -> None:
        now = time.time()
        dt = min(0.2, now - self._last_step)
        self._last_step = now
        scale = self.speed_factor / 100.0 * self.move_scale
        arrived = True
        for i, (cur, tgt) in enumerate(zip(self.joints, self.targets)):
            lo, hi = JOINT_LIMITS[i]
            tgt = max(lo, min(hi, tgt))
            max_delta = JOINT_SPEEDS[i] * scale * dt
            delta = tgt - cur
            if abs(delta) > 1e-4:
                arrived = False
                step = max(-max_delta, min(max_delta, delta))
                self.joints[i] = cur + step
        if arrived and not self._arrived_event.is_set():
            self._arrived_event.set()
            self._cond.notify_all()

    def wait_arrive(self, timeout: float = 30.0) -> bool:
        """Block until all joints reach their targets."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            with self.lock:
                if self._arrived():
                    return True
                self._cond.wait(timeout=min(0.5, deadline - time.time()))
        return False

    def _arrived(self) -> bool:
        return all(abs(c - t) < 0.05 for c, t in zip(self.joints, self.targets))

    def poll(self) -> None:
        """Advance the simulation; call periodically (bridge does 100 Hz)."""
        with self.lock:
            self._step()

    # ------------------------------------------------------------------ #
    # state snapshot (port-29901 style)
    # ------------------------------------------------------------------ #
    def snapshot(self) -> dict:
        with self.lock:
            j = list(self.joints)
        tx, ty, tz, r = fk(j)
        x, y, z = twin_to_real(tx, ty, tz)
        return {"Joint": j, "Pose": {"x": x, "y": y, "z": z, "r": r}}

    def place(self, x, y, z, r=0.0) -> None:
        """Teleport to a real-frame pose (used to start at the park pose)."""
        j1, j2, j3 = ik(*real_to_twin(x, y, z))
        with self.lock:
            self.joints = [j1, j2, j3, r]
            self.targets = list(self.joints)

    # ------------------------------------------------------------------ #
    # ASCII dashboard command handling (same grammar as real robot)
    # ------------------------------------------------------------------ #
    def execute(self, command: str) -> str:
        """Execute a dashboard ASCII command, return a response line."""
        cmd = command.strip()
        m = re.match(r"([A-Za-z_]+)\s*\((.*)\)", cmd)
        if not m:
            return "2,Parse error"
        name, argstr = m.group(1), m.group(2)
        args: list[str] = [a.strip() for a in argstr.split(",")] if argstr else []
        nums = [float(a) for a in args if re.match(r"^-?\d+\.?\d*(e-?\d+)?$", a)]

        with self.lock:
            if name == "EnableRobot":
                self.enabled = True
                self.error = ""
                return "0,"
            if name == "DisableRobot":
                self.enabled = False
                return "0,"
            if name == "ClearError":
                self.error = ""
                return "0,"
            if name == "SpeedFactor":
                if nums:
                    self.speed_factor = max(1, min(100, int(nums[0])))
                return "0,"
            if name == "EmergencyStop":
                self.targets = list(self.joints)
                self._arrived_event.set()
                return "0,"
            if name == "RobotMode":
                return "0,{5},RobotMode();" if self._arrived() else "0,{7},RobotMode();"
            if name == "GetPose":
                tx, ty, tz, r = fk(self.joints)
                x, y, z = twin_to_real(tx, ty, tz)
                return f"0,{x:.6f},{y:.6f},{z:.6f},{r:.6f}"
            if name == "GetAngle":
                return "0," + ",".join(f"{j:.2f}" for j in self.joints)
            if name == "DO" or name == "DOExecute":
                if len(nums) >= 2:
                    idx = int(nums[0]) - 1
                    val = 1 if nums[1] >= 0.5 else 0
                    if 0 <= idx < len(self.do_outputs):
                        self.do_outputs[idx] = val
                        if idx == 0:                 # DO1 = vacuum cup
                            self.vacuum = bool(val)
                return "0,"
            if name == "Home":
                self.move_scale = 1.0
                self.targets = [0.0, 90.0, 90.0, 0.0]
                self._arrived_event.clear()
                return "0,"
            if name == "Sync":
                # handled by caller blocking on wait_arrive(); report ok
                return "0,"

            # ---- motion -------------------------------------------------
            if name == "MovJ" or name == "MovL":
                if len(nums) >= 4:
                    x, y, z, r = nums[0], nums[1], nums[2], nums[3]
                    try:
                        j1, j2, j3 = ik(*real_to_twin(x, y, z))
                    except ValueError:
                        return "1,Unreachable"
                    speeds = [float(v) for v in re.findall(r"Speed[JL]=(\d+(?:\.\d+)?)", argstr)]
                    self.move_scale = max(0.01, min(1.0, min(speeds) / 100.0)) if speeds else 1.0
                    self.targets = [j1, j2, j3, r]
                    self._arrived_event.clear()
                return "0,"
            if name == "JointMovJ":
                self.move_scale = 1.0
                if len(nums) >= 4:
                    self.targets = list(nums[:4])
                    self._arrived_event.clear()
                return "0,"
            if name == "RelMovJ" or name == "RelMovL":
                if len(nums) >= 4:
                    tx, ty, tz, r = fk(self.joints)
                    x, y, z = twin_to_real(tx, ty, tz)
                    nx, ny, nz, nr = x + nums[0], y + nums[1], z + nums[2], r + nums[3]
                    try:
                        j1, j2, j3 = ik(*real_to_twin(nx, ny, nz))
                    except ValueError:
                        return "1,Unreachable"
                    self.targets = [j1, j2, j3, nr]
                    self._arrived_event.clear()
                return "0,"
            if name == "Jog":
                return "0,"  # not needed for the demo cell
            if name == "Alarm":
                return "0,"
            return f"1,Unknown command {name}"

    @staticmethod
    def _in_workspace(x: float, y: float, z: float) -> bool:
        r = math.hypot(x, z)
        return (GEOMETRY["R_MIN"] <= r <= GEOMETRY["R_MAX"]
                and GEOMETRY["Z_MIN"] <= y <= GEOMETRY["Z_MAX"])
