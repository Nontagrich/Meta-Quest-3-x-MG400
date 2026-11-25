"""
mg400_driver.py -- Dobot TCP control driver (no dependencies).

Protocol profiles (Dobot firmware families differ!):

  profile "mg400"  -- MG400 TCP-IP Protocol (MG400 User Guide):
        Dashboard port 29900, Realtime port 29901 (JSON lines).
        Every ASCII command is sent as  "<Command>(<args>) <CRC16>".
        Response: "0,..." (first field 0 = OK).

  profile "cr"     -- CR-series/Magician-style protocol (some MG400
        firmware builds / DobotStudio Pro versions answer this way):
        Dashboard port 29999 (plain ASCII, no CRC), motion may go to
        30003, feedback via periodic GetPose()/GetAngle() polling
        (30001 binary packets are intentionally not used - polling is
        plenty for a 30 Hz digital twin).

  auto-detection: connect(), handshake with "GetPose()" on the dashboard
  port of each profile, and use the first profile that answers "0,...".

CRC variants: profile "mg400" uses CRC-16/CCITT-FALSE (poly 0x1021,
init 0xFFFF) by default; "modbus" is available as a fallback flag.
Both self-test against "123456789" (CCITT-FALSE -> 0x29B1, Modbus -> 0x4B37).

Test with real hardware:
    python mg400_driver.py --ip 192.168.1.6 --enable --home
    python mg400_driver.py --ip 192.168.1.6 --profile cr --enable --home
"""

from __future__ import annotations

import argparse
import json
import math
import re
import socket
import threading
import time

# ---------------------------------------------------------------------------
# Geometry constants shared with the Unity digital twin (MG400Geometry.cs).
# Units: metres for lengths, degrees for angles.  KEEP IN SYNC!
# MG400 real kinematics: L2 = L3 = 175 mm, usable radial reach 220-440 mm.
# ---------------------------------------------------------------------------
GEOMETRY = {
    "L1": 0.130,        # base top -> J2 pivot height (m)
    "O2": 0.030,        # J2 pivot horizontal offset along J1 forward (+Z) (m)
    "L2": 0.175,        # rear arm length  (J2 -> J3) (m)
    "L3": 0.175,        # forearm length   (J3 -> J4) (m)
    "L4": 0.050,        # J4 -> TCP (suction cup tip) (m)
    "E2": 90.0,         # joint reading when rear arm is horizontal  (j2 = phi2 + E2)
    "E3": 90.0,         # joint reading when forearm collinear (j3 = E3 - (phi3 - phi2))
    # workspace clamp (mm, base frame) -- MG400 real usable reach
    "R_MIN": 220.0,
    "R_MAX": 440.0,
    "Z_MIN": -20.0,
    "Z_MAX": 450.0,
}

# ---------------------------------------------------------------------------
# Protocol profiles
# ---------------------------------------------------------------------------
PROFILES = {
    "mg400": {"dash": 29900, "rt": 29901, "crc": "ccitt-false"},
    "cr": {"dash": 29999, "rt": None, "crc": None},   # rt via dashboard polling
}

CRC_VARIANT = "ccitt-false"          # "ccitt-false" or "modbus" (mg400 profile)


def parse_response_numbers(response: str, required_count: int = 1) -> list[float] | None:
    """Parse numeric payload values from a successful Dashboard response.

    Dobot replies include a status field before the payload and may append the
    command text after it, for example ``0,{j1,j2,j3,j4},GetAngle();``.
    """
    if "," not in response or MG400Dashboard.status_of(response) != 0:
        return None
    payload = response.partition(",")[2].strip()
    payload = re.sub(r",\s*[A-Za-z_]\w*\s*\(.*$", "", payload)
    if not payload or "timeout" in payload.lower():
        return None
    values = [float(value) for value in re.findall(r"-?\d+\.?\d*", payload)]
    return values[:required_count] if len(values) >= required_count else None


def crc16_ccitt_false(data: bytes) -> int:
    """CRC-16/CCITT-FALSE: poly 0x1021, init 0xFFFF, MSB first."""
    crc = 0xFFFF
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) if (crc & 0x8000) else (crc << 1)
            crc &= 0xFFFF
    return crc


def crc16_modbus(data: bytes) -> int:
    """CRC-16/MODBUS: poly 0xA001 (reflected 0x8005), init 0xFFFF, LSB first."""
    crc = 0xFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if (crc & 1) else (crc >> 1)
    return crc


def crc16(data: bytes, variant: str | None = None) -> int:
    v = variant or CRC_VARIANT
    return crc16_ccitt_false(data) if v == "ccitt-false" else crc16_modbus(data)


def self_test() -> None:
    """Assert CRC implementations against published check values."""
    ref = b"123456789"
    assert crc16_ccitt_false(ref) == 0x29B1, "CRC CCITT-FALSE self-test failed"
    assert crc16_modbus(ref) == 0x4B37, "CRC Modbus self-test failed"


def assemble(command: str, profile: str = "mg400") -> bytes:
    """Build a wire-ready dashboard command for the given profile.

    mg400 : 'Command() CRC16'   (CRC-16/CCITT-FALSE over the command text)
    cr    : 'Command()'         (plain ASCII, no checksum)
    """
    command = command.strip()
    spec = PROFILES[profile]
    if spec["crc"]:
        return f"{command} {crc16(command.encode('ascii'), spec['crc'])}".encode("ascii")
    return command.encode("ascii")


# ---------------------------------------------------------------------------
# Forward / inverse kinematics (mirrors Unity MG400Geometry.cs)
#   Base frame: +Y up, +Z "forward" when j1 = 0, mm at the API boundary.
#   Planar 2R chain after the J1 yaw, with the tool treated as a collinear
#   extension of the forearm (L3' = L3 + L4):
#       shoulder = (O2, L1)            in the (r, y) plane
#       rear arm direction elevation phi2, forearm elevation phi3 (deg)
#       j2 = phi2 + E2                 (E2: joint reading when rear arm horizontal)
#       j3 = E3 - (phi3 - phi2)        (E3: joint reading when forearm collinear)
#       j4 = tool roll (does not move the TCP).
#   NOTE: joint-zero offsets (E2/E3) are our twin convention; calibrate them
#   against the real arm with DobotStudio before hardware day (see README).
# ---------------------------------------------------------------------------
def fk(joints_deg, geo: dict | None = None) -> tuple[float, float, float, float]:
    """Joint angles (deg) -> (x, y, z, r) in mm.  r = tool roll (j4)."""
    g = geo or GEOMETRY
    j1, j2, j3, j4 = joints_deg
    phi2 = j2 - g["E2"]                    # rear arm elevation
    phi3 = phi2 - (j3 - g["E3"])           # forearm absolute elevation
    t2, t3 = math.radians(phi2), math.radians(phi3)
    l3p = (g["L3"] + g["L4"]) * 1000.0     # forearm + tool, collinear

    r = g["O2"] * 1000.0 + g["L2"] * 1000.0 * math.cos(t2) + l3p * math.cos(t3)
    y = g["L1"] * 1000.0 + g["L2"] * 1000.0 * math.sin(t2) + l3p * math.sin(t3)

    j1r = math.radians(j1)
    x = r * math.sin(j1r)
    z = r * math.cos(j1r)
    return x, y, z, j4


def ik(x_mm: float, y_mm: float, z_mm: float, geo: dict | None = None) -> tuple[float, float, float]:
    """TCP (x,y,z) mm -> (j1,j2,j3).  Raises ValueError when unreachable."""
    g = geo or GEOMETRY
    r = math.hypot(x_mm, z_mm)
    j1 = math.degrees(math.atan2(x_mm, z_mm))

    dx = r - g["O2"] * 1000.0
    dy = y_mm - g["L1"] * 1000.0
    l2 = g["L2"] * 1000.0
    l3p = (g["L3"] + g["L4"]) * 1000.0
    d2 = dx * dx + dy * dy
    d = math.sqrt(d2)

    if d > l2 + l3p - 1e-6 or d < abs(l2 - l3p) + 1e-6 or d < 1e-6:
        raise ValueError(f"point ({x_mm:.1f},{y_mm:.1f},{z_mm:.1f}) unreachable "
                         f"(shoulder distance {d:.0f} mm)")

    # angle at the elbow joint (interior) and at the shoulder
    cos_elbow = (l2 * l2 + l3p * l3p - d2) / (2.0 * l2 * l3p)
    cos_shoulder = (d2 + l2 * l2 - l3p * l3p) / (2.0 * d * l2)
    cos_elbow = max(-1.0, min(1.0, cos_elbow))
    cos_shoulder = max(-1.0, min(1.0, cos_shoulder))
    theta_elbow = math.acos(cos_elbow)
    delta_shoulder = math.acos(cos_shoulder)

    gamma = math.atan2(dy, dx)
    phi2 = gamma + delta_shoulder                    # elbow-up configuration
    phi3 = phi2 + math.pi + theta_elbow              # forearm bends down-forward
    # normalise into (-pi, pi]
    phi2 = math.atan2(math.sin(phi2), math.cos(phi2))
    phi3 = math.atan2(math.sin(phi3), math.cos(phi3))

    j2 = math.degrees(phi2) + g["E2"]
    j3 = g["E3"] - math.degrees(phi3 - phi2)
    return j1, j2, j3


# ---------------------------------------------------------------------------
# Dashboard client (profile-aware)
# ---------------------------------------------------------------------------
class MG400Dashboard:
    def __init__(self, ip: str, port: int = 29900, timeout: float = 3.0,
                 profile: str = "mg400"):
        self.ip = ip
        self.port = port
        self.timeout = timeout
        self.profile = profile
        self.sock: socket.socket | None = None
        self._io_lock = threading.Lock()

    def connect(self) -> None:
        with self._io_lock:
            self._connect_unlocked()

    def _connect_unlocked(self) -> None:
        if self.sock is not None:
            return
        self.sock = socket.create_connection((self.ip, self.port), timeout=self.timeout)
        self.sock.settimeout(self.timeout)

    def close(self) -> None:
        with self._io_lock:
            self._close_unlocked()

    def _close_unlocked(self) -> None:
        if self.sock is None:
            return
        try:
            self.sock.close()
        finally:
            self.sock = None

    def is_connected(self) -> bool:
        return self.sock is not None

    # -- low level ---------------------------------------------------------
    def send_raw(self, command: str) -> str:
        """Send one dashboard command, return the response line."""
        with self._io_lock:
            self._connect_unlocked()
            sock = self.sock
            try:
                sock.sendall(assemble(command, self.profile))
                buf = b""
                deadline = time.time() + self.timeout
                while time.time() < deadline:
                    try:
                        chunk = sock.recv(256)
                    except socket.timeout:
                        break
                    if not chunk:
                        break
                    buf += chunk
                    if (b"\n" in buf
                            or (self.profile == "cr" and buf.rstrip().endswith(b";"))):
                        break
                if not buf:
                    raise socket.timeout(f"no response to {command}")
                return buf.decode("ascii", errors="replace").strip()
            except OSError:
                self._close_unlocked()
                raise

    @staticmethod
    def status_of(response: str) -> int:
        if not response or "timeout" in response.lower():
            return -1
        m = re.match(r"\s*(-?\d+)", response)
        return int(m.group(1)) if m else -1

    def send(self, command: str) -> tuple[bool, str]:
        """Send command, return (ok, response)."""
        try:
            resp = self.send_raw(command)
            return self.status_of(resp) == 0, resp
        except (OSError, socket.timeout) as exc:
            return False, f"conn-error: {exc}"

    # -- convenience commands ---------------------------------------------
    def enable(self):            return self.send("EnableRobot()")
    def disable(self):           return self.send("DisableRobot()")
    def clear_error(self):       return self.send("ClearError()")
    def home(self):              return self.send("Home()")
    def speed(self, pct: int):   return self.send(f"SpeedFactor({int(pct)})")
    def sync(self):              return self.send("Sync()")
    def get_pose(self):          return self.send("GetPose()")
    def get_angle(self):         return self.send("GetAngle()")
    def do(self, index: int, value: bool):   return self.send(f"DO({index},{1 if value else 0})")
    def do_execute(self, index: int, value: bool): return self.send(f"DOExecute({index},{1 if value else 0})")

    def mov_j(self, x, y, z, r, extra: str = ""):
        params = f"{x:.2f},{y:.2f},{z:.2f},{r:.2f}"
        if extra:
            params += "," + extra
        return self.send(f"MovJ({params})")

    def mov_l(self, x, y, z, r, extra: str = ""):
        params = f"{x:.2f},{y:.2f},{z:.2f},{r:.2f}"
        if extra:
            params += "," + extra
        return self.send(f"MovL({params})")

    def joint_mov_j(self, j1, j2, j3, j4, extra: str = ""):
        params = f"{j1:.2f},{j2:.2f},{j3:.2f},{j4:.2f}"
        if extra:
            params += "," + extra
        return self.send(f"JointMovJ({params})")

    def jog(self, mode: int, axis: int, step: float):
        return self.send(f"Jog({mode},{axis},{step})")


class MG400Motion:
    """CR-style motion command socket (plain ASCII, semicolon response)."""

    def __init__(self, ip: str, port: int = 30003, timeout: float = 3.0):
        self.ip = ip
        self.port = port
        self.timeout = timeout
        self.sock: socket.socket | None = None
        self._io_lock = threading.Lock()

    def connect(self) -> None:
        with self._io_lock:
            self._connect_unlocked()

    def _connect_unlocked(self) -> None:
        if self.sock is not None:
            return
        self.sock = socket.create_connection((self.ip, self.port), timeout=self.timeout)
        self.sock.settimeout(self.timeout)

    def close(self) -> None:
        with self._io_lock:
            if self.sock is not None:
                try:
                    self.sock.close()
                finally:
                    self.sock = None

    def send_raw(self, command: str, timeout: float | None = None) -> str:
        """Send one CR-style motion command and read through its semicolon."""
        with self._io_lock:
            self._connect_unlocked()
            sock = self.sock
            assert sock is not None
            try:
                sock.settimeout(timeout if timeout is not None else self.timeout)
                sock.sendall(command.strip().encode("ascii"))
                buf = bytearray()
                while not bytes(buf).rstrip().endswith(b";"):
                    chunk = sock.recv(4096)
                    if not chunk:
                        raise ConnectionError("motion socket closed before response terminator")
                    buf.extend(chunk)
                    if len(buf) > 65536:
                        raise ValueError("motion response exceeded 65536 bytes")
                return bytes(buf).decode("ascii", errors="replace").strip()
            except OSError:
                try:
                    sock.close()
                finally:
                    self.sock = None
                raise

    def send(self, command: str, timeout: float | None = None) -> tuple[bool, str]:
        try:
            response = self.send_raw(command, timeout=timeout)
            return MG400Dashboard.status_of(response) == 0, response
        except (OSError, socket.timeout, ConnectionError, ValueError) as exc:
            return False, f"conn-error: {exc}"


# ---------------------------------------------------------------------------
# Realtime feedback (two flavours)
# ---------------------------------------------------------------------------
class MG400Realtime:
    """Background thread reading JSON state lines from port 29901
    (MG400 profile)."""

    def __init__(self, ip: str, port: int = 29901):
        self.ip = ip
        self.port = port
        self.lock = threading.Lock()
        self.joints = [0.0, 0.0, 0.0, 0.0]
        self.pose = [0.0, 0.0, 0.0, 0.0]
        self.last_rx = 0.0
        self.running = False
        self._thread: threading.Thread | None = None
        self._sock: socket.socket | None = None

    def start(self) -> None:
        if self.running:
            return
        self.running = True
        self._thread = threading.Thread(target=self._loop, daemon=True, name="MG400Realtime")
        self._thread.start()

    def stop(self) -> None:
        self.running = False
        if self._sock:
            try:
                self._sock.close()
            except OSError:
                pass

    def snapshot(self) -> dict:
        with self.lock:
            return {
                "joints": list(self.joints),
                "pose": list(self.pose),
                "age": time.time() - self.last_rx if self.last_rx else -1.0,
            }

    # -- internals ----------------------------------------------------------
    def _loop(self) -> None:
        buf = b""
        while self.running:
            try:
                if self._sock is None:
                    self._sock = socket.create_connection((self.ip, self.port), timeout=3.0)
                    self._sock.settimeout(1.0)
                    buf = b""
                try:
                    chunk = self._sock.recv(4096)
                except socket.timeout:
                    continue
                if not chunk:
                    raise ConnectionResetError("realtime port closed")
                buf += chunk
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    self._parse(line.decode("ascii", errors="replace"))
            except OSError:
                self._sock = None
                time.sleep(1.0)

    def _parse(self, line: str) -> None:
        line = line.strip()
        if not line:
            return
        joints, pose = None, None
        try:
            data = json.loads(line)
            if isinstance(data, dict):
                j = data.get("Joint") or data.get("joint")
                p = data.get("Pose") or data.get("pose")
                if isinstance(j, list) and len(j) >= 4:
                    joints = [float(j[0]), float(j[1]), float(j[2]), float(j[3])]
                if isinstance(p, dict):
                    pose = [float(p.get("x", 0)), float(p.get("y", 0)),
                            float(p.get("z", 0)), float(p.get("r", 0))]
                elif isinstance(p, list) and len(p) >= 4:
                    pose = [float(p[0]), float(p[1]), float(p[2]), float(p[3])]
        except (ValueError, TypeError):
            pass
        if joints is None:
            # fallback: plain comma list "j1,j2,j3,j4,.."
            nums = re.findall(r"-?\d+\.?\d*", line)
            if len(nums) >= 4:
                joints = [float(n) for n in nums[:4]]
        if joints is None:
            return
        with self.lock:
            self.joints = joints
            if pose:
                self.pose = pose
            self.last_rx = time.time()


class PollingRealtime:
    """Realtime state by polling GetAngle()/GetPose() over the dashboard
    (CR profile / any firmware without a JSON feedback port)."""

    def __init__(self, dash: MG400Dashboard, rate_hz: float = 20.0):
        self.dash = dash
        self.period = 1.0 / max(1.0, rate_hz)
        self.lock = threading.Lock()
        self.joints = [0.0, 0.0, 0.0, 0.0]
        self.pose = [0.0, 0.0, 0.0, 0.0]
        self.last_rx = 0.0
        self.running = False
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        if self.running:
            return
        self.running = True
        self._thread = threading.Thread(target=self._loop, daemon=True, name="PollingRealtime")
        self._thread.start()

    def stop(self) -> None:
        self.running = False

    def snapshot(self) -> dict:
        with self.lock:
            return {
                "joints": list(self.joints),
                "pose": list(self.pose),
                "age": time.time() - self.last_rx if self.last_rx else -1.0,
            }

    def _loop(self) -> None:
        while self.running:
            try:
                ok_a, resp_a = self.dash.get_angle()
                ok_p, resp_p = self.dash.get_pose()
                joints = parse_response_numbers(resp_a, required_count=4) if ok_a else None
                pose = parse_response_numbers(resp_p, required_count=4) if ok_p else None
                if joints:
                    with self.lock:
                        self.joints = joints[:4]
                        if pose:
                            self.pose = pose[:4]
                        self.last_rx = time.time()
            except (OSError, socket.timeout):
                pass
            time.sleep(self.period)


def detect_profile(ip: str, timeout: float = 3.0) -> tuple[str | None, MG400Dashboard | None]:
    """Handshake with each profile's dashboard port; return the winner.

    Tries "mg400" (29900 + CRC16) first, then "cr" (29999, plain ASCII).
    A profile wins when GetPose() or GetAngle() returns a valid data payload.
    """
    for name in ("mg400", "cr"):
        spec = PROFILES[name]
        dash = MG400Dashboard(ip, port=spec["dash"], timeout=timeout, profile=name)
        try:
            dash.connect()
            for command in ("GetPose()", "GetAngle()"):
                ok, resp = dash.send(command)
                if ok and parse_response_numbers(resp, required_count=4):
                    return name, dash
            dash.close()
        except OSError:
            try:
                dash.close()
            except Exception:  # noqa: BLE001
                pass
    return None, None


# ---------------------------------------------------------------------------
# CLI smoke test
# ---------------------------------------------------------------------------
def main() -> None:
    ap = argparse.ArgumentParser(description="Dobot TCP driver smoke test")
    ap.add_argument("--ip", default="192.168.1.6", help="robot IP address")
    ap.add_argument("--profile", choices=["mg400", "cr", "auto"], default="auto")
    ap.add_argument("--enable", action="store_true", help="EnableRobot then Home")
    ap.add_argument("--cycle", action="store_true", help="Run a small pick demo cycle")
    ap.add_argument("--crc", choices=["ccitt-false", "modbus"], default=CRC_VARIANT)
    args = ap.parse_args()

    self_test()
    globals()["CRC_VARIANT"] = args.crc
    print(f"[CRC] default variant = {CRC_VARIANT}  (self-test passed)")

    profile = args.profile
    dash = None
    rt = None
    try:
        if profile == "auto":
            profile, dash = detect_profile(args.ip)
            if profile is None:
                print("[error] no protocol profile answered (checked 29900+CRC, 29999)")
                return
            print(f"[auto] detected profile: {profile}")
        else:
            spec = PROFILES[profile]
            dash = MG400Dashboard(args.ip, port=spec["dash"], profile=profile)
            dash.connect()
        print(f"[Dashboard] connected to {args.ip}:{PROFILES[profile]['dash']} ({profile})")

        if profile == "mg400":
            rt = MG400Realtime(args.ip, port=PROFILES["mg400"]["rt"])
            rt.start()
        else:
            rt = PollingRealtime(dash)
            rt.start()

        print("GetPose ->", dash.get_pose())
        print("GetAngle ->", dash.get_angle())

        if args.enable:
            print("Enable ->", dash.enable())
            time.sleep(0.5)
            print("Speed ->", dash.speed(50))
            print("Home ->", dash.home())

        if args.cycle and args.enable:
            print("Demo cycle: move above pick point, vacuum on, place ...")
            dash.mov_j(300, 0, 150, 0)
            dash.sync()
            dash.mov_l(300, 0, -10, 0)
            dash.do_execute(1, True)
            dash.mov_l(300, 0, 150, 0)
            dash.mov_j(-300, 0, 150, 0)
            dash.mov_l(-300, 0, -10, 0)
            dash.do_execute(1, False)
            dash.mov_l(-300, 0, 150, 0)
            dash.sync()
            print("Cycle done.")

        for _ in range(20):
            time.sleep(0.1)
            print("State:", rt.snapshot())
    finally:
        if rt:
            rt.stop()
        if dash:
            dash.close()
        print("[done] connection closed")


if __name__ == "__main__":
    main()
