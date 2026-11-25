"""mg400.py - small standalone driver for the Dobot MG400 (our own code, standard library only).

Protocol used (the "cr" profile; this is the one that answers on our robot, 192.168.1.6):
    dashboard port 29999  plain ASCII commands, no checksum:  ClearError() EnableRobot() User(n) Tool(n) SpeedFactor(p)
                          GetPose() GetAngle() RobotMode() GetErrorID() DOExecute(i,v) EmergencyStop()
    motion port    30003  MovJ(x,y,z,r) MovL(x,y,z,r) Sync()
Every reply looks like   <code>,{<payload>},<Command>(<args>);   code 0 = OK, anything else = error id.
Sync() on the motion port blocks until the queued motion is finished; after it we still read GetPose() and check
that the robot really is within tolerance of the target.

The socket layer is injectable (link_factory) so the whole stack can be tested against a fake robot.
"""

from __future__ import annotations

import logging
import math
import os
import re
import socket
import threading
import time
from datetime import datetime

HARD_SPEED_CAP = 30          # percent; the driver itself refuses more

MODE_NAMES = {               # RobotMode() values as documented for the Dobot V3 TCP/IP protocol
    1: "initialising", 2: "brake released", 3: "power off / not enabled", 4: "disabled",
    5: "enabled, idle", 6: "drag / backdrive", 7: "running", 8: "recording", 9: "ERROR / alarm",
    10: "paused", 11: "jogging",
}
MODE_IDLE = 5
MODE_RUNNING = 7
MODE_ERROR = 9
MODE_PAUSED = 10
START_TIMEOUT_S = 2.0         # a queued move must make the arm move within this time
MOVE_DETECT_MM = 0.3          # pose change that counts as "moving"
STILL_MM = 0.05               # pose change between two polls that counts as "standing still"

log = logging.getLogger("mg400")


# ---------------------------------------------------------------------------------------------------------------
# errors
# ---------------------------------------------------------------------------------------------------------------
class MG400Error(Exception):
    """Base class for every error raised by this driver."""


class RobotError(MG400Error):
    """The robot answered with a non-zero code (the error id)."""

    def __init__(self, command: str, reply: str, code: int | None):
        self.command, self.reply, self.code = command, reply, code
        super().__init__(f"{command} -> error id {code}: {reply!r}")


class LinkError(MG400Error):
    """Socket problem (not connected, closed, timeout)."""


class ArrivalError(MG400Error):
    """The robot did not reach the target pose (within tolerance, within the timeout)."""


class Aborted(MG400Error):
    """The run was stopped from another thread (E-STOP / Ctrl+C on the server)."""


class MotionNotStarted(ArrivalError):
    """The robot accepted a move (reply 0) but the arm never started moving."""


def _r2(values) -> tuple:
    return tuple(round(v, 2) for v in values)


# ---------------------------------------------------------------------------------------------------------------
# parsing
# ---------------------------------------------------------------------------------------------------------------
_NUM = re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")


def parse_reply(text: str) -> tuple[int, str]:
    """'0,{1,2,3,4},GetPose();' -> (0, '1,2,3,4'). Raises ValueError when there is no leading integer code."""
    m = re.match(r"\s*(-?\d+)", text or "")
    if not m:
        raise ValueError(f"no status code in reply {text!r}")
    left, right = text.find("{"), text.rfind("}")
    payload = text[left + 1:right] if 0 <= left < right else ""
    return int(m.group(1)), payload


def numbers(payload: str) -> list[float]:
    return [float(v) for v in _NUM.findall(payload)]


def angle_diff(a: float, b: float) -> float:
    """Smallest absolute difference between two angles in degrees."""
    return abs((a - b + 180.0) % 360.0 - 180.0)


def setup_logging(log_dir: str, verbose: bool = False) -> str:
    """Log every request/reply to <log_dir>/robot_<time>.log; with verbose also to the console. Returns the path."""
    os.makedirs(log_dir, exist_ok=True)
    path = os.path.join(log_dir, datetime.now().strftime("robot_%Y%m%d_%H%M%S.log"))
    log.setLevel(logging.DEBUG)
    for handler in list(log.handlers):
        log.removeHandler(handler)
    file_handler = logging.FileHandler(path, encoding="utf-8")
    file_handler.setFormatter(logging.Formatter("%(asctime)s.%(msecs)03d %(levelname)-7s %(message)s", "%H:%M:%S"))
    log.addHandler(file_handler)
    if verbose:
        console = logging.StreamHandler()
        console.setFormatter(logging.Formatter("    %(message)s"))
        log.addHandler(console)
    log.propagate = False
    return path


def close_logging() -> None:
    for handler in list(log.handlers):
        handler.close()
        log.removeHandler(handler)


# ---------------------------------------------------------------------------------------------------------------
# TCP link
# ---------------------------------------------------------------------------------------------------------------
class TcpLink:
    """One ASCII request/reply socket. A reply ends with ';'."""

    SLICE = 0.25             # recv timeout slice, so that Ctrl+C is noticed quickly on Windows

    def __init__(self, name: str, ip: str, port: int, connect_timeout: float = 4.0):
        self.name, self.ip, self.port, self.connect_timeout = name, ip, port, connect_timeout
        self.sock: socket.socket | None = None
        self.lock = threading.RLock()       # one request/reply at a time, also when a stop comes from another thread

    def connect(self) -> None:
        try:
            self.sock = socket.create_connection((self.ip, self.port), timeout=self.connect_timeout)
        except OSError as exc:
            raise LinkError(f"cannot connect to {self.ip}:{self.port} ({self.name}): {exc}") from exc

    def close(self) -> None:
        if self.sock is not None:
            try:
                self.sock.close()
            finally:
                self.sock = None

    def _drain(self) -> None:
        """Throw away anything the robot sent unasked, so it cannot be mistaken for the next reply."""
        sock = self.sock
        sock.setblocking(False)
        try:
            while True:
                data = sock.recv(4096)
                if not data:
                    raise LinkError(f"{self.name} socket closed by the robot")
                log.debug("RX %s (unsolicited): %r", self.name, data)
        except (BlockingIOError, InterruptedError):
            pass
        finally:
            sock.setblocking(True)

    def request(self, command: str, timeout: float = 5.0) -> str:
        with self.lock:
            return self._request(command, timeout)

    def _request(self, command: str, timeout: float = 5.0) -> str:
        if self.sock is None:
            raise LinkError(f"{self.name} is not connected")
        try:
            self._drain()
            self.sock.sendall(command.strip().encode("ascii"))
            self.sock.settimeout(self.SLICE)
            deadline = time.monotonic() + timeout
            buf = b""
            while not buf.rstrip().endswith(b";"):
                try:
                    chunk = self.sock.recv(4096)
                except socket.timeout:
                    if time.monotonic() > deadline:
                        raise LinkError(f"no reply to {command} within {timeout:.0f} s ({self.name})") from None
                    continue
                if not chunk:
                    raise LinkError(f"{self.name} socket closed while waiting for the reply to {command}")
                buf += chunk
            return buf.decode("ascii", errors="replace").strip()
        except OSError as exc:
            raise LinkError(f"{self.name} socket error on {command}: {exc}") from exc


# ---------------------------------------------------------------------------------------------------------------
# driver
# ---------------------------------------------------------------------------------------------------------------
class MG400:
    def __init__(self, ip: str = "192.168.1.6", dash_port: int = 29999, motion_port: int = 30003,
                 link_factory=None, inline_frame_args: bool = False):
        self.ip, self.dash_port, self.motion_port = ip, dash_port, motion_port
        self.link_factory = link_factory or (lambda name, host, port: TcpLink(name, host, port))
        self.inline_frame_args = inline_frame_args
        self.dash = None
        self.motion = None
        self.user = None
        self.tool = None
        self.do_state: dict[int, int] = {}      # outputs this driver switched successfully {index: 0|1}
        self.motion_sent = False                # True once any Mov*() was sent
        self._start_pose = None
        self.poll_s = 0.1                       # GetPose/RobotMode polling period while a move runs
        self.abort = threading.Event()          # set by another thread to stop the current run at the next poll
        self.gem_possibly_held = False          # set by the grip logic: vacuum pulsed and release not completed

    # -- connection -------------------------------------------------------------------------------------------
    def connect(self) -> None:
        self.dash = self.link_factory("dashboard", self.ip, self.dash_port)
        self.motion = self.link_factory("motion", self.ip, self.motion_port)
        self.dash.connect()
        try:
            self.motion.connect()
        except MG400Error:
            self.dash.close()
            raise
        log.info("connected to %s (dashboard %d, motion %d)", self.ip, self.dash_port, self.motion_port)

    def close(self) -> None:
        for link in (self.dash, self.motion):
            if link is not None:
                try:
                    link.close()
                except Exception:   # noqa: BLE001 - closing must never raise
                    pass

    # -- low level --------------------------------------------------------------------------------------------
    def _send(self, link, command: str, timeout: float = 5.0) -> str:
        """Send one command, log request + reply, raise RobotError on a non-zero code. Returns the payload."""
        log.info("TX %-9s %s", link.name, command)
        try:
            reply = link.request(command, timeout)
        except MG400Error as exc:
            log.error("RX %-9s %s -> %s", link.name, command, exc)
            raise
        log.info("RX %-9s %s", link.name, reply)
        try:
            code, payload = parse_reply(reply)
        except ValueError:
            raise RobotError(command, reply, None) from None
        if code != 0:
            raise RobotError(command, reply, code)
        return payload

    def dash_cmd(self, command: str, timeout: float = 5.0) -> str:
        return self._send(self.dash, command, timeout)

    def motion_cmd(self, command: str, timeout: float = 5.0) -> str:
        return self._send(self.motion, command, timeout)

    # -- state ------------------------------------------------------------------------------------------------
    def get_pose(self) -> tuple[float, float, float, float]:
        """(x, y, z, r) in the currently selected User/Tool frame. Raises if the pose cannot be read."""
        values = numbers(self.dash_cmd("GetPose()"))
        if len(values) < 4:
            raise MG400Error(f"GetPose() returned no usable pose: {values}")
        pose = tuple(values[:4])
        if all(v == 0.0 for v in pose):
            raise MG400Error("GetPose() returned (0, 0, 0, 0): the robot is not reporting a real pose")
        return pose

    def get_joints(self) -> tuple[float, float, float, float]:
        values = numbers(self.dash_cmd("GetAngle()"))
        if len(values) < 4:
            raise MG400Error(f"GetAngle() returned no usable joints: {values}")
        return tuple(values[:4])

    def robot_mode(self) -> int:
        values = numbers(self.dash_cmd("RobotMode()"))
        if not values:
            raise MG400Error("RobotMode() returned no value")
        return int(values[0])

    def error_ids(self) -> list[int]:
        """Active alarm ids (empty list = no alarm)."""
        return [int(v) for v in numbers(self.dash_cmd("GetErrorID()")) if int(v) != 0]

    # -- setup ------------------------------------------------------------------------------------------------
    def clear_error(self) -> None:
        self.dash_cmd("ClearError()")

    def enable(self) -> None:
        self.dash_cmd("EnableRobot()", timeout=15.0)

    def disable(self) -> None:
        self.dash_cmd("DisableRobot()", timeout=15.0)

    def wait_enabled(self, timeout: float = 20.0) -> None:
        """Wait until RobotMode() == 5 (enabled, idle) after EnableRobot()."""
        deadline = time.monotonic() + timeout
        mode = None
        while time.monotonic() < deadline:
            mode = self.robot_mode()
            if mode == MODE_IDLE:
                return
            if mode == MODE_ERROR:
                raise MG400Error(f"robot went into ERROR (mode 9), error ids {self.error_ids()}")
            time.sleep(0.25)
        raise MG400Error(f"robot not enabled after {timeout:.0f} s (RobotMode={mode}: {MODE_NAMES.get(mode, '?')})")

    def select_frames(self, user: int, tool: int) -> None:
        self.dash_cmd(f"User({int(user)})")
        self.dash_cmd(f"Tool({int(tool)})")
        self.user, self.tool = int(user), int(tool)

    def set_speed(self, percent: int) -> None:
        if not 1 <= percent <= HARD_SPEED_CAP:
            raise MG400Error(f"speed {percent} % refused (allowed 1..{HARD_SPEED_CAP} %)")
        self.dash_cmd(f"SpeedFactor({int(percent)})")

    def set_acc(self, percent: int) -> bool:
        """AccJ/AccL are optional on some firmware: a refusal is logged and reported, not fatal."""
        ok = True
        for command in (f"AccJ({int(percent)})", f"AccL({int(percent)})"):
            try:
                self.dash_cmd(command)
            except RobotError as exc:
                log.warning("acceleration command refused (continuing with the robot's default): %s", exc)
                ok = False
        return ok

    # -- motion -----------------------------------------------------------------------------------------------
    def _frame_args(self) -> str:
        if self.inline_frame_args and self.user is not None:
            return f",User={self.user},Tool={self.tool}"
        return ""

    def _before_move(self) -> None:
        """Refuse to queue a move unless RobotMode() is 5 (enabled, idle); remember the pose the move starts from."""
        if self.abort.is_set():
            raise Aborted("stopped (E-STOP): no further move is sent")
        mode = self.robot_mode()
        if mode != MODE_IDLE:
            raise MG400Error(f"robot not ready for motion: RobotMode={mode} ({MODE_NAMES.get(mode, '?')}), "
                             f"expected {MODE_IDLE} (enabled, idle)")
        self._start_pose = self.get_pose()

    def mov_j(self, x: float, y: float, z: float, r: float) -> None:
        self._before_move()
        self.motion_sent = True
        self.motion_cmd(f"MovJ({x:.2f},{y:.2f},{z:.2f},{r:.2f}{self._frame_args()})")

    def mov_l(self, x: float, y: float, z: float, r: float) -> None:
        self._before_move()
        self.motion_sent = True
        self.motion_cmd(f"MovL({x:.2f},{y:.2f},{z:.2f},{r:.2f}{self._frame_args()})")

    def wait_until_arrived(self, target, tol_mm: float = 1.0, tol_deg: float = 1.0, timeout: float = 60.0,
                           start_timeout: float = START_TIMEOUT_S, poll: float | None = None):
        """Watch the motion by polling GetPose()/RobotMode() (no blocking Sync(): it can wait forever when the queued
        move never starts). 1) the arm must START moving within start_timeout, else MotionNotStarted;
        2) it must reach the target within tol_mm / tol_deg and stand still (two polls), else ArrivalError at timeout.
        Returns the final pose."""
        poll = self.poll_s if poll is None else poll
        start = self._start_pose or self.get_pose()
        t0 = time.monotonic()
        needs_motion = (math.dist(start[:3], target[:3]) > tol_mm or angle_diff(start[3], target[3]) > tol_deg)
        started = not needs_motion
        stable, last = 0, start
        while True:
            if self.abort.is_set():
                raise Aborted("stopped (E-STOP) while waiting for the move to finish")
            pose = self.get_pose()
            mode = self.robot_mode()
            elapsed = time.monotonic() - t0
            d_xyz, d_r = math.dist(pose[:3], target[:3]), angle_diff(pose[3], target[3])
            if mode == MODE_ERROR:
                raise MG400Error(f"robot went into ERROR (RobotMode 9) during the move, error ids {self.error_ids()}, "
                                 f"pose={_r2(pose)}")
            if mode == MODE_PAUSED:
                raise MG400Error(f"robot is PAUSED (RobotMode 10) during the move, pose={_r2(pose)}")
            if not started:
                moved = math.dist(pose[:3], start[:3]) > MOVE_DETECT_MM or angle_diff(pose[3], start[3]) > MOVE_DETECT_MM
                if moved or mode == MODE_RUNNING:
                    started = True
                    log.info("motion started after %.2f s (RobotMode=%s)", elapsed, mode)
                elif elapsed > start_timeout:
                    ids = []
                    try:
                        ids = self.error_ids()
                    except MG400Error:
                        pass
                    raise MotionNotStarted(
                        f"the move was accepted by the robot (reply 0) but the arm did NOT start moving within "
                        f"{start_timeout:g} s. RobotMode={mode} ({MODE_NAMES.get(mode, '?')}), error ids={ids or 'none'}, "
                        f"pose={_r2(pose)} (unchanged), target={_r2(target)}. Nothing moved: the queued command is "
                        f"being cancelled. Check the robot state in DobotStudio / run 'status'.")
            else:
                at_target = d_xyz <= tol_mm and d_r <= tol_deg
                still = math.dist(pose[:3], last[:3]) < STILL_MM
                stable = stable + 1 if (at_target and still and mode != MODE_RUNNING) else 0
                if stable >= 2:
                    log.info("arrived in %.1f s: pose=%s off by %.2f mm / %.2f deg", elapsed, pose, d_xyz, d_r)
                    return pose
                if elapsed > timeout:
                    raise ArrivalError(
                        f"not at target after {timeout:g} s: pose={_r2(pose)} target={_r2(target)} (off {d_xyz:.2f} mm / "
                        f"{d_r:.2f} deg, tolerance {tol_mm} mm / {tol_deg} deg, RobotMode={mode})")
            last = pose
            time.sleep(poll)

    # -- outputs ----------------------------------------------------------------------------------------------
    def set_do(self, index: int, on: bool) -> None:
        self.dash_cmd(f"DOExecute({int(index)},{1 if on else 0})")
        self.do_state[int(index)] = 1 if on else 0

    # -- stopping ---------------------------------------------------------------------------------------------
    def _confirm_stopped(self, seconds: float) -> bool:
        """True when RobotMode is not 'running'/'error' and the pose stands still for two polls."""
        deadline = time.monotonic() + seconds
        last, still = None, 0
        while time.monotonic() < deadline:
            try:
                pose, mode = self.get_pose(), self.robot_mode()
            except MG400Error:
                return False
            if mode in (MODE_RUNNING, MODE_ERROR):
                still = 0
            elif last is not None and math.dist(pose[:3], last[:3]) < STILL_MM:
                still += 1
                if still >= 2:
                    return True
            last = pose
            time.sleep(0.15)
        return False

    def stop_motion(self, confirm_s: float = 2.0) -> tuple[str, bool]:
        """Normal stop first: ResetRobot() (stop + clear the queued commands; fallback Pause()). It is confirmed by
        polling. Only if no normal stop is accepted and confirmed does it escalate to EmergencyStop(), which latches
        an alarm. Never raises. Returns (text, latched_alarm_possible)."""
        notes = []
        for command in ("ResetRobot()", "Pause()"):
            try:
                self._send(self.dash, command, timeout=3.0)
            except MG400Error as exc:
                notes.append(f"{command} refused: {exc}")
                continue
            if self._confirm_stopped(confirm_s):
                return f"stopped with {command} (normal stop, no E-STOP alarm)", False
            notes.append(f"{command} accepted but the arm was not confirmed standing still")
            break
        return "; ".join(notes + [self.emergency_stop()]), True

    def emergency_stop(self) -> str:
        """Safest stop the firmware offers over TCP: EmergencyStop(). Never raises; returns a status text.
        NOTE: it latches the robot into an alarm; ClearError() + EnableRobot() are needed afterwards."""
        try:
            self._send(self.dash, "EmergencyStop()", timeout=3.0)
            return "EmergencyStop() accepted"
        except MG400Error as exc:
            return f"EmergencyStop() FAILED: {exc} - use the robot's physical E-STOP"

    def outputs_off(self, indexes) -> list[str]:
        """Switch the given outputs OFF, one by one, never raising. Returns the list of failures."""
        failures = []
        for index in indexes:
            try:
                self.set_do(index, False)
            except MG400Error as exc:
                failures.append(f"DO{index} OFF failed: {exc}")
        return failures
