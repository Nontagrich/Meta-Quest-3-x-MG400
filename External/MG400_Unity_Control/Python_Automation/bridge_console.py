#!/usr/bin/env python3
"""bridge_console.py - drive the MG400 bridge (bridge_server.py) from a terminal, no Unity. Standard library only.

    python bridge_console.py --host 127.0.0.1 --port 5555   # bridge on this PC
    python bridge_console.py --host 192.168.1.50            # bridge on another PC
    (suction = DO9, blow-off = DO10 by default; change with --vac-do / --blow-do, --blow-do 0 = no blow-off)

Type "help" for the commands. SAFETY: every motion shows pose / target / distance / speed and asks "Move? [y/N]".
Ctrl+C, or typing "s" + Enter at any time (also while a move is running), sends estop.
Everything is logged to logs/console_<timestamp>.log next to this file.
"""

import argparse
import json
import math
import os
import queue
import socket
import sys
import threading
import time
from datetime import datetime

MAX_SPEED_PCT = 30
ACK_TIMEOUT = 10.0
SYNC_TIMEOUT = 60.0
SAFE_Z = 152.03
# Taught pick poses: robot display values, User Frame 1 / Tool Frame 1 (x, y, z, r)
PICKS = {
    1: (-114.20, 263.20, -77.18, -16.04), 2: (-113.32, 303.00, -77.18, -12.10), 3: (-115.67, 341.96, -77.18, -8.47),
    4: (-148.15, 266.61, -77.18, -11.33), 5: (-154.45, 306.39, -77.18, -5.97), 6: (-154.92, 339.21, -77.18, -3.28),
    7: (-194.18, 262.76, -77.18, -4.22), 8: (-193.73, 301.80, -77.18, -0.76), 9: (-195.57, 340.97, -77.18, 2.32),
}
DROP = (5.36, 32.36, 90.58, -53.26)
EXPECTED_FRAME = 1

HELP = """Commands
  state                  latest full state (JSON)
  ws                     get_workspace: polygon, radii, park, W, drop zone, frames, DO config
  live on|off            compact live state line (once per second)
  check X Y              check_packing_workspace: straight path from the current pose to (X, Y), NO motion
  enable | disable       enable / disable the arm (asks first)
  speed P                SpeedFactor P percent (max 30 here)
  move X Y Z R           linear move (MovL) then sync
  movej X Y Z R          joint move (MovJ) then sync; the bridge only allows it on the drop-zone route
  rel dX dY dZ           linear move relative to the current pose (same R) then sync
  up Z                   straight up/down to Z at the current X, Y
  grip on                blow DO OFF, then vacuum DO ON (default DO10 off, DO9 on)
  grip off               vacuum OFF, blow ON for 0.3 s, blow OFF. Never both ON. estop/quit: both OFF
  do N 0|1               raw digital output (diagnostics; vacuum+blow ON together is refused)
  home                   bridge home (park X,Y at the current Z) then sync
  estop | s              emergency stop NOW (also Ctrl+C)
  reset                  estop_reset (asks first; check the arm is safe)
  frame U T              select User / Tool frame on the robot (set_frame), then check with ws
  goto N [safe]          pick pose 1..9: up to safe Z, over it, then down (with "safe": stop at safe Z)
  goto drop [safe]       drop route: J to (drop x, y, safe Z) (bridge: only from W), then down
  goto W                 L to W at safe Z (W from the bridge)
  help | quit
"""


class Console:
    def __init__(self, host, port, vac_do, blow_do):
        self.host, self.port, self.vac_do, self.blow_do = host, port, vac_do, blow_do
        self.sock = None
        self.connected = False
        self.state = None
        self.state_time = 0.0
        self.ws = None
        self.speed = None            # last speed accepted by the bridge in THIS session
        self.estopped = False
        self.nozzle_checked = False  # set after "goto 1 safe" + visual confirmation
        self.do_on = {}            # last DO values this console set successfully {index: 0|1}
        self.live = True
        self.asking = False
        self.next_id = 1
        self.acks = {}
        self.lock = threading.Lock()
        self.print_lock = threading.Lock()
        self.stdin_queue = queue.Queue()
        self.last_live = 0.0
        self.last_state_log = 0.0
        os.makedirs(os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs"), exist_ok=True)
        self.log_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs",
                                     datetime.now().strftime("console_%Y%m%d_%H%M%S.log"))
        self.log_file = open(self.log_path, "a", encoding="utf-8")

    # ---- output / log ---------------------------------------------------------------------------------------
    def log(self, kind, text):
        self.log_file.write(f"{datetime.now().strftime('%H:%M:%S.%f')[:-3]} {kind:5s} {text}\n")
        self.log_file.flush()

    def say(self, text="", kind="INFO"):
        with self.print_lock:
            print(text, flush=True)
        self.log(kind, text)

    # ---- connection -----------------------------------------------------------------------------------------
    def connect(self):
        self.sock = socket.create_connection((self.host, self.port), timeout=5)
        self.sock.settimeout(None)
        self.connected = True
        threading.Thread(target=self._reader, daemon=True, name="reader").start()
        threading.Thread(target=self._stdin, daemon=True, name="stdin").start()

    def _stdin(self):
        for line in sys.stdin:
            line = line.strip()
            if line.lower() in ("s", "estop"):       # always immediate, even while a move is running
                self.estop()
                if self.asking:
                    self.stdin_queue.put("n")        # answers a pending "Move? [y/N]" with no
                continue
            self.stdin_queue.put(line)
        self.stdin_queue.put(None)

    def _reader(self):
        buf = b""
        while True:
            try:
                chunk = self.sock.recv(4096)
            except OSError:
                chunk = b""
            if not chunk:
                self.connected = False
                self.say("!! bridge connection lost", "INFO")
                return
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                if line.strip():
                    self._handle(line.decode("utf-8", "replace"))

    def _handle(self, line):
        try:
            msg = json.loads(line)
        except ValueError:
            self.log("RX?", line)
            return
        kind = msg.get("type")
        if kind == "state":
            self.state, self.state_time = msg, time.time()
            now = time.time()
            if now - self.last_state_log >= 0.5:
                self.last_state_log = now
                self.log("STATE", line)
            if self.live and now - self.last_live >= 1.0:
                self.last_live = now
                p, j = msg.get("pose", [0, 0, 0, 0]), msg.get("joints", [0, 0, 0, 0])
                with self.print_lock:
                    print(f"  [state] pose=({p[0]:.2f}, {p[1]:.2f}, {p[2]:.2f}, {p[3]:.2f}) "
                          f"joints=({', '.join(f'{v:.1f}' for v in j)}) sim={msg.get('sim')} "
                          f"enabled={msg.get('enabled')} vacuum(do1)={msg.get('do1')}", flush=True)
        elif kind == "ack":
            self.log("ACK", line)
            with self.lock:
                self.acks[msg.get("id")] = msg
        elif kind == "event":
            self.say(f"  [event] {msg.get('event')} (source {msg.get('source')})", "EVENT")
        else:
            self.log("RX", line)

    # ---- commands -------------------------------------------------------------------------------------------
    def send(self, cmd, **fields):
        if not self.connected:
            raise ConnectionError("not connected")
        with self.lock:
            cid = self.next_id
            self.next_id += 1
        payload = {"id": cid, "cmd": cmd, **fields}
        text = json.dumps(payload)
        self.log("TX", text)
        self.sock.sendall((text + "\n").encode("utf-8"))
        return cid

    def wait_ack(self, cid, timeout):
        """Wait for the ack of `cid`; typing 's' meanwhile sends estop."""
        end = time.time() + timeout
        while time.time() < end:
            with self.lock:
                ack = self.acks.pop(cid, None)
            if ack is not None:
                return ack
            if not self.connected:
                return {"ok": False, "msg": "connection lost"}
            time.sleep(0.03)
        return {"ok": False, "msg": f"no ack within {timeout:.0f}s"}

    def call(self, cmd, wait=ACK_TIMEOUT, **fields):
        ack = self.wait_ack(self.send(cmd, **fields), wait)
        self.say(f"  ack {cmd}: ok={ack.get('ok')} msg={ack.get('msg')}", "ACK")
        return ack

    def estop(self):
        self.estopped = True
        try:
            cid = self.send("estop")
            ack = self.wait_ack(cid, 5)
            self.say(f"!! E-STOP sent: ok={ack.get('ok')} msg={ack.get('msg')}", "ESTOP")
        except ConnectionError as exc:
            self.say(f"!! E-STOP could NOT be sent: {exc}. Use the robot's own stop button.", "ESTOP")
            return
        self.both_off()

    def both_off(self):
        """Suction and blow-off both OFF (the bridge allows 'do' while E-STOP is latched). Never raises."""
        for index in (self.vac_do, self.blow_do):
            if index:
                try:
                    ack = self.wait_ack(self.send("do", index=index, value=0), 5)
                    if ack.get("ok"):
                        self.do_on[index] = 0
                    else:
                        self.say(f"!! DO{index} OFF refused: {ack.get('msg')}", "ESTOP")
                except Exception as exc:
                    self.say(f"!! DO{index} OFF failed: {exc}", "ESTOP")

    def ask(self, prompt):
        """y/N prompt; 's' = estop (returns False)."""
        with self.print_lock:
            print(prompt, end=" ", flush=True)
        self.asking = True
        try:
            while True:
                try:
                    line = self.stdin_queue.get(timeout=0.2)
                except queue.Empty:
                    continue
                if line is None:
                    return False
                self.log("ASK", f"{prompt} -> {line}")
                return line.lower() in ("y", "yes")
        finally:
            self.asking = False

    # ---- state helpers --------------------------------------------------------------------------------------
    def pose(self):
        if self.state is None or time.time() - self.state_time > 2.0:
            return None
        return tuple(self.state.get("pose", [0, 0, 0, 0]))

    def refresh_ws(self):
        ack = self.call("get_workspace")
        if ack.get("ok"):
            try:
                self.ws = json.loads(ack["msg"])
                if self.ws.get("estopped"):
                    self.estopped = True
            except ValueError:
                self.ws = None
        return self.ws

    # ---- motion ---------------------------------------------------------------------------------------------
    def motion_allowed(self):
        if not self.connected:
            self.say("REFUSED: not connected")
            return False
        if self.estopped:
            self.say("REFUSED: E-STOP active - check the arm, then 'reset'")
            return False
        if self.speed is None:
            self.say(f"REFUSED: speed not set in this session. Use 'speed P' first (max {MAX_SPEED_PCT}).")
            return False
        if self.speed > MAX_SPEED_PCT:
            self.say(f"REFUSED: speed {self.speed}% > {MAX_SPEED_PCT}%")
            return False
        if self.pose() is None:
            self.say("REFUSED: no fresh state from the bridge")
            return False
        return True

    def move(self, move_type, target, label=""):
        """One checked move: confirm, move_to, sync. Returns True on success."""
        if not self.motion_allowed():
            return False
        pose = self.pose()
        dist = math.dist(pose[:3], target[:3])
        self.say(f"\n  MOVE {label}type={move_type}")
        self.say(f"    from   ({pose[0]:.2f}, {pose[1]:.2f}, {pose[2]:.2f}, {pose[3]:.2f})")
        self.say(f"    to     ({target[0]:.2f}, {target[1]:.2f}, {target[2]:.2f}, {target[3]:.2f})")
        self.say(f"    distance {dist:.1f} mm   speed {self.speed}%"
                 f"{'   (bridge caps drop-zone moves lower)' if move_type == 'J' else ''}")
        if not self.ask("  Move? [y/N]"):
            self.say("  cancelled")
            return False
        ack = self.call("move_to", x=target[0], y=target[1], z=target[2], r=target[3], type=move_type)
        if not ack.get("ok"):
            return False
        return self.sync()

    def sync(self):
        ack = self.call("sync", wait=SYNC_TIMEOUT + 10, timeout=SYNC_TIMEOUT)
        if ack.get("ok"):
            p = self.pose()
            if p:
                self.say(f"  arrived: ({p[0]:.2f}, {p[1]:.2f}, {p[2]:.2f}, {p[3]:.2f})")
        return bool(ack.get("ok"))

    def go_to(self, x, y, z, r, label):
        """L path to (x,y,z) lifting to safe Z first when the arm is low."""
        pose = self.pose()
        if pose is None:
            self.say("REFUSED: no fresh state")
            return False
        far = math.hypot(pose[0] - x, pose[1] - y) > 1.0
        if far and pose[2] < SAFE_Z - 5:
            if not self.move("L", (pose[0], pose[1], SAFE_Z, pose[3]), f"{label} - lift to safe Z: "):
                return False
        if far and z != SAFE_Z:
            if not self.move("L", (x, y, SAFE_Z, r), f"{label} - over target at safe Z: "):
                return False
        return self.move("L", (x, y, z, r), f"{label}: ")

    def frames_ok(self):
        ws = self.refresh_ws()
        frames = (ws or {}).get("frames", {})
        user, tool = frames.get("user"), frames.get("tool")
        if user == EXPECTED_FRAME and tool == EXPECTED_FRAME:
            return True
        self.say(f"  WARNING: the pick poses are User Frame {EXPECTED_FRAME} / Tool Frame {EXPECTED_FRAME}, but the bridge "
                 f"reports user={user}, tool={tool} (null = never selected, the robot uses whatever is current).")
        return self.ask("  Continue anyway? [y/N]")

    # ---- command handlers -----------------------------------------------------------------------------------
    def run(self, line):
        parts = line.split()
        if not parts:
            return True
        cmd, args = parts[0].lower(), parts[1:]
        try:
            handler = getattr(self, "c_" + cmd, None)
            if handler is None:
                self.say(f"unknown command '{cmd}' - type help")
            else:
                return handler(args) is not False or cmd != "quit"
        except (ValueError, IndexError):
            self.say("bad arguments - type help")
        except ConnectionError as exc:
            self.say(f"!! {exc}")
        except Exception as exc:  # noqa: BLE001 - never die with the arm moving
            self.say(f"!! internal error: {exc!r}")
        return True

    def c_help(self, a):
        self.say(HELP)

    def c_state(self, a):
        self.say(json.dumps(self.state, indent=2) if self.state else "no state yet")

    def c_ws(self, a):
        ws = self.refresh_ws()
        if not ws:
            return
        self.say(f"  corners  {ws['corners']}\n  radius   {ws['minRadius']}..{ws['maxRadius']}   park {ws['parkXY']}"
                 f"\n  sim={ws['sim']} estopped={ws['estopped']}\n  frames   {ws.get('frames')}")
        dz = ws.get("dropZone")
        if dz:
            self.say(f"  W        ({dz['waypointX']}, {dz['waypointY']}) at safe Z {dz['safeZ']}\n  drop     centre ({dz['centerX']}, "
                     f"{dz['centerY']}) r {dz['radius']}  Z {dz['dropZ']} (min {dz['minZ']})  speed cap {dz['maxSpeedPct']}%")
        self.say(f"  DO config (this console): vacuum DO{self.vac_do}, blow-off "
                 f"{'DO' + str(self.blow_do) if self.blow_do else 'none'}")

    def c_live(self, a):
        self.live = a[0].lower() == "on"

    def c_check(self, a):
        x, y = float(a[0]), float(a[1])
        if self.pose() is None:
            self.say("no fresh state")
            return
        self.call("check_packing_workspace", path=[{"x": x, "y": y}])

    def c_enable(self, a):
        if self.ask("  Enable the arm (brakes release)? [y/N]"):
            self.call("enable", wait=35)

    def c_disable(self, a):
        if self.ask("  Disable the arm (it may sag)? [y/N]"):
            self.call("disable", wait=35)

    def c_speed(self, a):
        pct = int(a[0])
        if not 1 <= pct <= MAX_SPEED_PCT:
            self.say(f"REFUSED: speed must be 1..{MAX_SPEED_PCT}%")
            return
        if self.call("speed", pct=pct).get("ok"):
            self.speed = pct

    def c_move(self, a):
        x, y, z, r = (float(v) for v in a[:4])
        self.move("L", (x, y, z, r))

    def c_movej(self, a):
        x, y, z, r = (float(v) for v in a[:4])
        self.move("J", (x, y, z, r))

    def c_rel(self, a):
        p = self.pose()
        if p is None:
            self.say("no fresh state")
            return
        dx, dy, dz = (float(v) for v in a[:3])
        self.move("L", (p[0] + dx, p[1] + dy, p[2] + dz, p[3]))

    def c_up(self, a):
        p = self.pose()
        if p is None:
            self.say("no fresh state")
            return
        self.move("L", (p[0], p[1], float(a[0]), p[3]))

    def _do(self, index, value):
        """One digital output. Suction and blow-off are never ON together; ON is refused while E-STOP is latched."""
        if value:
            if self.estopped:
                self.say("  refused: E-STOP active, outputs stay OFF")
                return False
            other = {self.vac_do: self.blow_do, self.blow_do: self.vac_do}.get(index)
            if other and self.do_on.get(other):
                self.say(f"  refused: DO{other} is ON, suction and blow-off are never ON together")
                return False
        ok = self.call("do", index=index, value=value, wait=10).get("ok")
        if ok:
            self.do_on[index] = value
        return ok

    def c_grip(self, a):
        if a[0].lower() == "on":
            # blow OFF first, then suction ON
            if self.blow_do and not self._do(self.blow_do, 0):
                return
            self._do(self.vac_do, 1)
        else:
            # suction OFF, blow ON for 0.3 s, blow OFF
            if not self._do(self.vac_do, 0):
                return
            if self.blow_do:
                try:
                    if self._do(self.blow_do, 1):
                        time.sleep(0.3)
                finally:
                    self._do(self.blow_do, 0)

    def c_do(self, a):
        self._do(int(a[0]), int(a[1]))

    def c_home(self, a):
        if not self.motion_allowed():
            return
        ws = self.ws or self.refresh_ws()
        p = self.pose()
        if ws and p:
            self.say(f"\n  HOME: park XY {ws['parkXY']} at the current Z {p[2]:.2f} (from {p[0]:.2f}, {p[1]:.2f}), speed {self.speed}%")
        if self.ask("  Move? [y/N]") and self.call("home").get("ok"):
            self.sync()

    def c_estop(self, a):
        self.estop()

    def c_s(self, a):
        self.estop()

    def c_reset(self, a):
        if self.ask("  Is the arm safe and the cause of the stop cleared? Reset E-STOP? [y/N]"):
            if self.call("estop_reset").get("ok"):
                self.estopped = False

    def c_frame(self, a):
        user, tool = int(a[0]), int(a[1])
        if self.ask(f"  Select User {user} / Tool {tool} on the robot? [y/N]"):
            self.call("set_frame", kind="user", index=user)
            self.call("set_frame", kind="tool", index=tool)
            self.refresh_ws()

    def c_goto(self, a):
        name = a[0].lower()
        safe_only = len(a) > 1 and a[1].lower() == "safe"
        p = self.pose()
        if p is None:
            self.say("no fresh state")
            return
        if name == "w":
            ws = self.ws or self.refresh_ws()
            dz = (ws or {}).get("dropZone")
            if not dz:
                self.say("bridge reports no drop zone / W")
                return
            self.go_to(dz["waypointX"], dz["waypointY"], dz["safeZ"], p[3], "W")
            return
        if name.isdigit() and not safe_only and not self.nozzle_checked:
            self.say("  refused: first run 'goto 1 safe' and check by eye that the nozzle is directly above gem #1.")
            return
        if not self.frames_ok():
            return
        if name == "drop":
            x, y, z, r = DROP
            if p[2] < SAFE_Z - 5 and math.hypot(p[0] - x, p[1] - y) > 40:
                self.say("  Lift to safe Z first (up 152.03), then 'goto W', then 'goto drop'.")
                return
            if self.move("J", (x, y, SAFE_Z, r), "drop route (J, bridge only allows it from W): ") and not safe_only:
                self.move("L", (x, y, z, r), "drop - down: ")
            return
        n = int(name)
        if n not in PICKS:
            self.say("pick number must be 1..9")
            return
        x, y, z, r = PICKS[n]
        self.go_to(x, y, SAFE_Z if safe_only else z, r, f"pick #{n}")
        if n == 1 and safe_only and not self.nozzle_checked:
            p2 = self.pose()
            if p2 and math.hypot(p2[0] - x, p2[1] - y) < 2.0 and abs(p2[2] - SAFE_Z) < 2.0:
                if self.ask("  Is the nozzle directly above gem #1? [y/N]"):
                    self.nozzle_checked = True
                    self.say("  nozzle check done: descending goto N is now allowed")
                else:
                    self.say("  nozzle check NOT confirmed: stop, re-check frames / pick pose (compare with DobotStudio)")
            else:
                self.say("  arm is not at pick #1 safe Z yet: no nozzle check")

    def c_quit(self, a):
        try:
            self.both_off()
            if self.ask("  Disable the arm before exiting? [y/N]"):
                self.call("disable", wait=35)
        finally:
            return False


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5555)
    ap.add_argument("--vac-do", type=int, default=9, help="digital output that switches the suction (default 9)")
    ap.add_argument("--blow-do", type=int, default=10,
                    help="digital output for the blow-off pulse on release (default 10, 0 = none)")
    args = ap.parse_args()

    con = Console(args.host, args.port, args.vac_do, args.blow_do)
    con.say(f"log file: {con.log_path}")
    try:
        con.connect()
    except OSError as exc:
        con.say(f"cannot connect to {args.host}:{args.port}: {exc}")
        return 1
    con.say(f"connected to {args.host}:{args.port}. Type help. Ctrl+C or 's' = E-STOP.")
    time.sleep(1.2)
    con.refresh_ws()
    if con.state and con.state.get("sim") is False:
        con.say("*** REAL ROBOT: the bridge reports sim=false ***")
    interrupted = 0.0
    while True:
        try:
            with con.print_lock:
                print("> ", end="", flush=True)
            while True:
                try:
                    line = con.stdin_queue.get(timeout=0.2)
                    break
                except queue.Empty:
                    continue
            if line is None:
                break
            con.log("CMD", line)
            if line.strip().lower() in ("quit", "exit"):
                con.c_quit([])
                break
            con.run(line)
        except KeyboardInterrupt:
            con.estop()
            if time.time() - interrupted < 1.5:
                break
            interrupted = time.time()
    con.say("bye")
    return 0


if __name__ == "__main__":
    sys.exit(main())
