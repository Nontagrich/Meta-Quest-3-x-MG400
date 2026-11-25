"""Bounded controller tracking in the physical MG400's Cartesian frame."""

import math
import time

from motion_workspace import (WorkspaceMoves, allows_segment, contains_xy, entry_route,
                              limit_segment, read_robot_values)

class PlanarTeleop:
    R_MIN = -90.0
    R_MAX = -55.0
    R_ENTRY_MARGIN = 0.5
    R_PREP_STEP = 3.0
    R_ARRIVAL_TOLERANCE = 0.2
    PREPARING = "Preparing controller start pose"
    ROBOT_SPEED_FACTOR = 20
    MAX_STEP = 2.0
    PREP_XY_STEP = 10.0
    # Use the global factor for positioning, without an additional SpeedL cap.
    PREP_XY_SPEED = 100
    PREP_XY_ACCELERATION = 10
    DEADBAND = 0.2
    Z_READBACK_TOLERANCE = 0.005  # mm; the Z sent to the robot stays fixed
    TIMEOUT = 5.0

    def __init__(self, backend, workspace_moves=None):
        self.backend = backend
        self.workspace_moves = workspace_moves if workspace_moves is not None else WorkspaceMoves(backend)
        self.owner = None
        self.origin = None
        self.last_request = 0.0
        self.pending = None
        self.prep_route = []
        self.preparing_r = False
        self.entered_workspace = False
        self.entry_r = None

    def end(self, owner):
        if self.owner is owner:
            self.owner = None
            self.origin = None
            self.pending = None
            self.prep_route = []
            self.preparing_r = False
            self.entered_workspace = False
            self.entry_r = None

    def active(self):
        if self.owner is not None and time.monotonic() - self.last_request >= self.TIMEOUT:
            self.end(self.owner)
        return self.owner is not None

    @classmethod
    def _r_allowed(cls, angle):
        return cls.R_MIN <= angle <= cls.R_MAX

    def _finish_preparation(self, owner, pose):
        if not contains_xy(pose[:2]) or not self._r_allowed(pose[3]):
            self.end(owner)
            return False, "Prepared pose is outside controller XY/R limits"
        # Re-anchor XY only. Keep the original Z and the selected R.
        self.origin[:2] = pose[:2]
        return True, f"Start pose reached. Z {self.origin[2]:g} mm, R {self.origin[3]:g} deg locked"

    def _read_values(self, command, count):
        return read_robot_values(self.backend, command, count)

    def begin(self, owner):
        if self.active():
            return False, "Controller tracking is already active"
        if owner is None or self.backend.sim or self.backend.profile != "cr":
            return False, "Planar tracking requires the real MG400 TCP/IP profile (cr)"
        mode = self._read_values("RobotMode()", 1)
        if mode == [7.0]:
            return False, "Robot is still moving. Wait for it to stop, then press T again."
        if mode != [5.0]:
            return False, "Robot must be enabled and idle"
        pose = self._read_values("GetPose()", 4)
        if pose is None:
            return False, "Start pose unavailable"
        if not self.workspace_moves.settled(pose):
            return False, "Previous motion is unconfirmed. Check the robot and reopen Bridge before retrying."
        if float(f"{pose[2]:.6f}") != pose[2]:
            return False, "Reported Z cannot be held at the command precision"
        route = []
        inside = contains_xy(pose[:2])
        if not inside:
            route = entry_route(pose[:2])
            if not route:
                return False, "No same-Z path to the controller start inside the configured workspace"
        ok, reply = self.backend.command(f"SpeedFactor({self.ROBOT_SPEED_FACTOR})")
        if not ok:
            return False, f"Could not set controller speed: {reply}"
        self.prep_route = route
        self.entered_workspace = inside
        self.entry_r = round(pose[3], 6)
        self.preparing_r = not self._r_allowed(pose[3])
        locked_r = pose[3]
        if self.preparing_r:
            locked_r = max(self.R_MIN + self.R_ENTRY_MARGIN,
                           min(self.R_MAX - self.R_ENTRY_MARGIN, pose[3]))
        self.owner = owner
        self.origin = [*pose[:3], round(locked_r, 6)]
        self.last_request = time.monotonic()
        if self.prep_route or self.preparing_r:
            return True, self.PREPARING
        return True, f"Z {pose[2]:g} mm, R {locked_r:g} deg locked"

    def step(self, owner, msg):
        return self._advance(owner, msg=msg)

    def prepare(self, owner):
        return self._advance(owner, preparing=True)

    def _advance(self, owner, msg=None, preparing=False):
        if not self.active() or self.owner is not owner:
            return False, "Press T to start a new tracking session"

        def reject(reason):
            self.end(owner)
            return False, reason

        if preparing != (bool(self.prep_route) or self.preparing_r):
            return reject("Unexpected controller tracking phase. Press T again.")
        if not preparing:
            dx, dy = msg.get("x"), msg.get("y")
            if any(type(v) not in (int, float) or not -1000 <= v <= 1000 for v in (dx, dy)):
                return reject("Invalid controller offset")

        waiting = self.PREPARING if preparing else "Waiting for previous move"
        self.last_request = time.monotonic()
        mode = self._read_values("RobotMode()", 1)
        if mode == [7.0] and self.pending is not None:
            if time.monotonic() - self.pending[3] < self.TIMEOUT:
                return True, waiting
            return reject("Previous controller move did not finish")
        if mode != [5.0]:
            return reject("Robot is no longer enabled and idle")

        pose = self._read_values("GetPose()", 4)
        if pose is None:
            return reject("Pose unavailable")
        locked_z = self.origin[2]
        locked_r = self.origin[3]
        if abs(pose[2] - locked_z) > self.Z_READBACK_TOLERANCE:
            return reject(f"Z readback drift: {pose[2]:.6f} mm (locked {locked_z:.6f} mm). Tracking stopped.")
        inside = contains_xy(pose[:2])
        if not inside and (self.entered_workspace or not self.prep_route):
            return reject("Robot left the four-point XY workspace")
        self.entered_workspace = self.entered_workspace or inside
        if not self.preparing_r and not self._r_allowed(pose[3]):
            return reject(f"R readback {pose[3]:g} deg is outside controller limits [{self.R_MIN:g}, {self.R_MAX:g}]")
        if self.pending is not None:
            px, py, pr, sent_at = self.pending
            if (math.hypot(px - pose[0], py - pose[1]) >= self.DEADBAND
                    or abs(pr - pose[3]) > self.R_ARRIVAL_TOLERANCE):
                if time.monotonic() - sent_at >= self.TIMEOUT:
                    return reject("Previous controller move did not reach its target")
                return True, waiting
            self.pending = None

        # The entry exception moves XY only; set R after the arm is inside.
        rotating = self.preparing_r and not self.prep_route
        r = self.entry_r if self.prep_route else locked_r
        if rotating:
            delta_r = locked_r - pose[3]
            if abs(delta_r) <= self.R_ARRIVAL_TOLERANCE and self._r_allowed(pose[3]):
                self.preparing_r = False
                return self._finish_preparation(owner, pose)
            x, y = self.origin[:2]
            r = pose[3] + max(-self.R_PREP_STEP, min(self.R_PREP_STEP, delta_r))
        else:
            if preparing:
                x, y = self.prep_route[0]
                if not allows_segment(pose[:2], (x, y), entering=not self.entered_workspace):
                    return reject("Start route is outside the configured workspace")
            else:
                x, y = self.origin[0] + dx, self.origin[1] + dy
            distance = math.hypot(x - pose[0], y - pose[1])
            if distance <= self.DEADBAND:
                if not preparing:
                    return True, "Inside deadband"
                self.prep_route.pop(0)
                if self.prep_route:
                    return True, self.PREPARING
                if self.preparing_r:
                    if not inside:
                        return reject("Entry did not reach the inside of the XY workspace")
                    self.origin[:2] = pose[:2]
                    return True, self.PREPARING
                return self._finish_preparation(owner, pose)
            fraction = min(1.0, (self.PREP_XY_STEP if preparing else self.MAX_STEP) / distance)
            x = pose[0] + (x - pose[0]) * fraction
            y = pose[1] + (y - pose[1]) * fraction
            if not preparing:
                x, y = limit_segment(pose[:2], (x, y))
                if math.hypot(x - pose[0], y - pose[1]) < 0.02:
                    return True, "Workspace boundary reached"

        x, y, r = round(x, 6), round(y, 6), round(r, 6)
        if time.monotonic() - self.last_request >= 3.0:
            return reject("Robot status took too long. Press T again.")

        speed, acceleration = 5, 5
        if self.prep_route:
            speed, acceleration = self.PREP_XY_SPEED, self.PREP_XY_ACCELERATION
        ok, reply = self.workspace_moves.send(
            pose, (x, y, locked_z, r),
            entering=bool(self.prep_route) and not self.entered_workspace,
            options=f"SpeedL={speed},AccL={acceleration},CP=0",
        )
        if not ok:
            return reject(reply)
        self.pending = (x, y, r, time.monotonic())
        return True, self.PREPARING if preparing else reply
