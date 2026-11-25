"""
bridge_server.py -- CPE494 Jewelry Store control bridge.

Run on the demo PC (same machine as Unity, or any LAN host):

    python bridge_server.py --sim                  # virtual MG400 (no hardware)
    python bridge_server.py --real --ip 192.168.1.6
    python bridge_server.py --sim --no-audio       # disable voice relay
    python bridge_server.py --sim --mqtt-topic 494/staffcall

Channels
  TCP :5555  control  -- newline-delimited JSON, Unity <-> bridge
      Unity -> bridge:
        {"id":1,"cmd":"enable"} {"cmd":"disable"} {"cmd":"home"}
        {"id":2,"cmd":"speed","pct":50}
        {"id":3,"cmd":"move_joints","joints":[j1,j2,j3,j4],"speed":100}
        {"id":4,"cmd":"move_to","x":..,"y":..,"z":..,"r":..,"type":"L"|"J"}
        {"cmd":"check_packing_workspace","path":[{"park":true},{"x":..,"y":..}]}
        {"id":5,"cmd":"do","index":1,"value":true}
        {"id":6,"cmd":"sync"}
        {"id":7,"cmd":"raw","text":"GetPose()"}
        {"id":8,"cmd":"teleop","joints":[...]}      # simulation only
        {"cmd":"teleop_xy_begin"} {"cmd":"teleop_xy_prepare"}
        {"cmd":"teleop_xy_step","x":dx,"y":dy} {"cmd":"teleop_xy_end"}
        {"id":9,"cmd":"get_state"} {"cmd":"ping"}
        {"id":10,"cmd":"staff_call"} {"cmd":"staff_end"}
      bridge -> Unity:
        {"type":"ack","id":n,"ok":true,"msg":"..."}
        {"type":"state","t":..,"joints":[..],"pose":[x,y,z,r],"do1":0/1,
         "enabled":0/1,"sim":true,"moving":true}
        {"type":"event","event":"staff_call"|"staff_accepted"|"staff_ended"}

  TCP :5556  voice   -- binary frames: [1B ch][4B LE len][PCM16 mono 16kHz]
      ch 1 = PCM from Unity (played on staff speakers)
      ch 2 = PCM from staff mic (streamed to Unity)

  MQTT broker.hivemq.com topic "494/staffcall"
      Unity publishes the call; this bridge listens, alerts the caretaker
      console and auto-accepts (=> staff_accepted event + voice opens).

Dependencies: none required. Optional: paho-mqtt (staff-call over MQTT),
sounddevice (staff microphone + speakers).  Missing optionals degrade
gracefully with a console note.
"""

from __future__ import annotations

import argparse
import json
import socket
import socketserver
import sys
import threading
import time
import struct

from mg400_driver import (MG400Dashboard, MG400Motion, MG400Realtime, PollingRealtime,
                          PROFILES, detect_profile, self_test)
from sim_robot import SimMG400
from planar_teleop import PlanarTeleop
from motion_workspace import WorkspaceMoves, contains_xy

# --------------------------------------------------------------------------
# optional dependencies
# --------------------------------------------------------------------------
try:
    import paho.mqtt.client as mqtt_lib
    HAS_MQTT = True
except ImportError:
    mqtt_lib = None
    HAS_MQTT = False

try:
    import sounddevice as sd
    HAS_AUDIO = True
except ImportError:
    sd = None
    HAS_AUDIO = False

SAMPLE_RATE = 16000
VOICE_FRAME_MS = 60
VOICE_SAMPLES = SAMPLE_RATE * VOICE_FRAME_MS // 1000


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


# --------------------------------------------------------------------------
# Robot backends
# --------------------------------------------------------------------------
class RealBackend:
    """Talks to a physical Dobot over its native TCP ports.

    profile "mg400": dashboard 29900 (ASCII + CRC16), realtime 29901 JSON.
    profile "cr":    dashboard 29999 (plain ASCII), motion 30003, state via
                     GetAngle/GetPose polling.  profile "auto" detects which
                     dashboard profile answers.
    """

    def __init__(self, ip: str, profile: str = "auto"):
        self.sim = False
        self.profile = None
        self.dash = None
        self.motion = None
        self.rt = None

        if profile == "auto":
            self.profile, self.dash = detect_profile(ip)
            if self.profile is None:
                raise SystemExit(
                    f"[robot] no protocol answered at {ip} "
                    "(tried 29900+CRC16 and 29999) -- check IP/cable/firewall")
        else:
            spec = PROFILES[profile]
            self.profile = profile
            self.dash = MG400Dashboard(ip, port=spec["dash"], profile=profile)
            try:
                self.dash.connect()
            except OSError as exc:
                raise SystemExit(f"[robot] cannot connect to {ip}:{spec['dash']} -> {exc}")

        if self.profile == "cr":
            self.motion = MG400Motion(ip, port=30003)
            try:
                self.motion.connect()
            except OSError as exc:
                self.dash.close()
                raise SystemExit(f"[robot] cannot connect to {ip}:30003 -> {exc}")

        if self.profile == "mg400":
            self.rt = MG400Realtime(ip, port=PROFILES["mg400"]["rt"])
            self.rt.start()
        else:
            self.rt = PollingRealtime(self.dash)
            self.rt.start()
        log(f"[robot] connected to {ip} using profile '{self.profile}'")

    def command(self, cmd: str) -> tuple[bool, str]:
        command_name = cmd.partition("(")[0].strip()
        if self.profile == "cr" and self.motion is not None and command_name in {
            "MovJ", "MovL", "JointMovJ", "MoveJog", "Sync", "Arc",
            "RelMovJTool", "RelMovLTool", "RelMovJUser", "RelMovLUser",
            "RelJointMovJ",
        }:
            return self.motion.send(cmd)
        return self.dash.send(cmd)

    def sync(self, timeout: float = 60.0) -> tuple[bool, str]:
        # On real hardware Sync() blocks server-side until motion completes.
        if self.profile == "cr" and self.motion is not None:
            return self.motion.send("Sync()", timeout=timeout)
        self.dash.timeout = timeout
        try:
            return self.dash.send("Sync()")
        finally:
            self.dash.timeout = 3.0

    def state(self) -> dict:
        snap = self.rt.snapshot()
        return {"Joint": snap["joints"], "Pose": {"x": snap["pose"][0],
                                                  "y": snap["pose"][1],
                                                  "z": snap["pose"][2],
                                                  "r": snap["pose"][3]}}

    def close(self) -> None:
        if self.rt:
            self.rt.stop()
        if self.dash:
            self.dash.close()
        if self.motion:
            self.motion.close()


class SimBackend:
    """Wraps SimMG400 with the same interface as RealBackend."""

    def __init__(self):
        self.robot = SimMG400()
        self.sim = True
        self._stop = False
        threading.Thread(target=self._run, daemon=True, name="sim-poll").start()

    def _run(self) -> None:
        while not self._stop:
            self.robot.poll()
            time.sleep(0.01)

    def command(self, cmd: str) -> tuple[bool, str]:
        resp = self.robot.execute(cmd)
        return resp.startswith("0"), resp

    def sync(self, timeout: float = 30.0) -> tuple[bool, str]:
        ok = self.robot.wait_arrive(timeout)
        return ok, "synced" if ok else "sync timeout"

    def state(self) -> dict:
        return self.robot.snapshot()

    def close(self) -> None:
        self._stop = True


# --------------------------------------------------------------------------
# Bridge hub: clients, broadcaster, voice, MQTT
# --------------------------------------------------------------------------
class BridgeHub:
    CONTROL_PORT = 5555
    VOICE_PORT = 5556

    def __init__(self, backend, mqtt_topic: str | None, audio: bool):
        self.backend = backend
        self.audio = audio and HAS_AUDIO
        self.mqtt_topic = mqtt_topic
        self.lock = threading.Lock()
        self.clients: list[socket.socket] = []
        self.running = True
        self.staff_session = False
        self.teleop_last_sent = 0.0
        self.estopped = False
        self.frames = {"user": None, "tool": None}
        self.workspace_moves = WorkspaceMoves(backend)
        if backend.sim:   # start the simulated arm at park, above the pick area, like the real cell
            from motion_workspace import PARK_XY, DROP
            backend.robot.place(PARK_XY[0], PARK_XY[1], DROP["safe_z"] if DROP else 150.0)
        self.planar_teleop = PlanarTeleop(backend, self.workspace_moves)
        self.motion_lock = threading.Lock()

        if audio and not HAS_AUDIO:
            log("[voice] sounddevice not installed -> voice relay disabled "
                "(pip install sounddevice)")

    # ------------------------------------------------------------------ #
    # lifecycle
    # ------------------------------------------------------------------ #
    def serve(self) -> None:
        threading.Thread(target=self._broadcast_loop, daemon=True, name="state").start()
        threading.Thread(target=self._voice_server, daemon=True, name="voice").start()
        if self.mqtt_topic and HAS_MQTT:
            threading.Thread(target=self._mqtt_loop, daemon=True, name="mqtt").start()
        elif self.mqtt_topic:
            log("[mqtt] paho-mqtt not installed -> MQTT staff call disabled "
                "(pip install paho-mqtt). TCP staff_call still works.")

        with socketserver.ThreadingTCPServer(
            ("0.0.0.0", self.CONTROL_PORT), self._handler_cls, bind_and_activate=False
        ) as srv:
            srv.allow_reuse_address = True
            srv.server_bind()
            srv.server_activate()
            srv.daemon_threads = True
            srv.hub = self
            log(f"[bridge] control channel listening on :{self.CONTROL_PORT}")
            log(f"[bridge] voice channel listening on :{self.VOICE_PORT}"
                + ("" if self.audio else "  (voice relay OFF)"))
            log(f"[bridge] robot backend: {'SIM' if self.backend.sim else 'REAL'}")
            try:
                srv.serve_forever()
            except KeyboardInterrupt:
                log("[bridge] shutting down")
            finally:
                self.running = False
                self.backend.close()

    # ------------------------------------------------------------------ #
    # control channel
    # ------------------------------------------------------------------ #
    def _handler_cls(self, request, client_address, server):  # noqa: N802
        ControlHandler(request, client_address, server)

    def register(self, sock: socket.socket) -> None:
        with self.lock:
            self.clients.append(sock)

    def unregister(self, sock: socket.socket) -> None:
        with self.motion_lock:
            self.planar_teleop.end(sock)
        with self.lock:
            if sock in self.clients:
                self.clients.remove(sock)

    def broadcast(self, payload: dict) -> None:
        data = (json.dumps(payload) + "\n").encode("utf-8")
        with self.lock:
            dead = []
            for c in self.clients:
                try:
                    c.sendall(data)
                except OSError:
                    dead.append(c)
            for c in dead:
                self.clients.remove(c)

    def _broadcast_loop(self) -> None:
        while self.running:
            try:
                state = self.backend.state()
            except Exception as exc:  # noqa: BLE001
                log(f"[state] error: {exc}")
                state = {}
            moving = False
            joints = state.get("Joint", [])
            if self.backend.sim:
                moving = not self.backend.robot._arrived()
            pose = [state.get("Pose", {}).get(k, 0.0) for k in ("x", "y", "z", "r")]
            payload = {
                "type": "state",
                "t": time.time(),
                "joints": joints,
                "pose": pose,
                "outsideWorkspace": not self.backend.sim and not contains_xy(pose[:2]),
                "do1": 1 if getattr(self.backend, "robot", None) and self.backend.robot.vacuum else 0,
                "enabled": 1 if (self.backend.sim and self.backend.robot.enabled) else 1,
                "sim": self.backend.sim,
                "moving": moving,
            }
            self.broadcast(payload)
            time.sleep(1 / 30)

    def handle_command(self, msg: dict, send: callable, owner=None) -> None:
        cid = msg.get("id", 0)
        cmd = msg.get("cmd", "")
        try:
            ok, text = self._dispatch_control(cmd, msg, owner)
        except Exception as exc:  # noqa: BLE001
            ok, text = False, f"bridge error: {exc}"
        send({"type": "ack", "id": cid, "ok": bool(ok), "msg": text})

    def estop(self) -> tuple[bool, str]:
        """Immediate stop, callable from any thread without the motion lock."""
        self.estopped = True
        self.workspace_moves.abort.set()
        notes = []
        for command in ("EmergencyStop()", "DOExecute(1,0)"):
            try:
                ok, reply = self.backend.command(command)
                notes.append(f"{command.partition('(')[0]}:{'ok' if ok else reply}")
            except Exception as exc:  # noqa: BLE001
                notes.append(f"{command.partition('(')[0]}:error {exc}")
        try:
            self.planar_teleop.end(self.planar_teleop.owner)
        except Exception:  # noqa: BLE001
            pass
        log("[bridge] E-STOP " + " ".join(notes))
        return True, "estop " + " ".join(notes)

    def set_frame(self, kind: str, index: int) -> tuple[bool, str]:
        """Select the robot's global User/Tool frame (real robot only); remembered for get_workspace."""
        if self.backend.sim:
            return False, "frames are only selectable on the real robot"
        if kind not in ("User", "Tool") or not 0 <= int(index) <= 9:
            return False, "frame kind must be User or Tool, index 0..9"
        ok, reply = self.backend.command(f"{kind}({int(index)})")
        if ok:
            self.frames[kind.lower()] = int(index)
        return ok, reply

    def workspace_info(self) -> dict:
        import motion_workspace as mw
        info = {"corners": [list(c) for c in mw.CORNERS], "parkXY": list(mw.PARK_XY),
                "minRadius": mw.MIN_RADIUS, "maxRadius": mw.MAX_RADIUS, "sim": self.backend.sim,
                "estopped": self.estopped, "dropZone": None,
                "frames": {"user": self.frames["user"], "tool": self.frames["tool"],
                           "note": "null = the bridge never selected one; the robot uses its current global frame"}}
        d = mw.DROP
        if d is not None:
            info["dropZone"] = {"centerX": d["center"][0], "centerY": d["center"][1], "radius": d["radius"],
                                "dropZ": d["drop_z"], "minZ": d["min_z"], "safeZ": d["safe_z"],
                                "maxSpeedPct": d["speed"], "waypointX": d["waypoint"][0],
                                "waypointY": d["waypoint"][1]}
        return info

    def _dispatch_control(self, cmd: str, msg: dict, owner):
        if cmd == "estop":
            return self.estop()
        if cmd == "get_workspace":
            return True, json.dumps(self.workspace_info())
        if cmd == "set_frame":
            if self.estopped:
                return False, "E-STOP active"
            return self.set_frame(str(msg.get("kind", "")).capitalize(), int(msg.get("index", -1)))
        if cmd == "estop_reset":
            self.estopped = False
            self.workspace_moves.abort.clear()
            self.workspace_moves.pending = None   # the operator checked the arm: forget the interrupted move
            return self.backend.command("ClearError()")
        if self.estopped and cmd not in ("get_state", "ping", "do", "disable"):
            return False, "E-STOP active: send estop_reset after checking the robot"
        if cmd in ("get_state", "ping", "staff_call", "staff_end", "sync", "do"):
            return self._dispatch(cmd, msg)
        with self.motion_lock:
            tracking = self.planar_teleop
            if cmd == "teleop_xy_begin":
                return tracking.begin(owner)
            if cmd == "teleop_xy_prepare":
                return tracking.prepare(owner)
            if cmd == "teleop_xy_step":
                return tracking.step(owner, msg)
            if cmd == "teleop_xy_end":
                tracking.end(owner)
                return True, "Controller tracking ended"
            if cmd == "disable":
                tracking.end(tracking.owner)
            elif tracking.active():
                return False, "Stop controller tracking with T before sending other robot commands"
            return self._dispatch(cmd, msg)

    def _dispatch(self, cmd: str, msg: dict) -> tuple[bool, str]:
        if cmd in ("enable", "disable"):
            return self.backend.command(f"{cmd.capitalize()}Robot()")
        if cmd == "home":
            return self.workspace_moves.move(msg, park=True)
        if cmd == "sync":
            return self.workspace_moves.wait(timeout=max(1.0, min(120.0, float(msg.get("timeout", 60.0)))))
        if cmd == "speed":
            pct = max(1, min(100, int(msg.get("pct", 50))))
            return self.backend.command(f"SpeedFactor({pct})")
        if cmd == "move_joints":
            if not self.backend.sim:
                return False, "Joint moves cannot be checked against the XY workspace. Use a linear Cartesian target."
            j = [float(v) for v in msg.get("joints", [])]
            if len(j) != 4:
                return False, "need 4 joint values"
            speed = msg.get("speed")
            extra = f",Speed={int(speed)}" if speed else ""
            return self.backend.command(f"JointMovJ({j[0]:.2f},{j[1]:.2f},{j[2]:.2f},{j[3]:.2f}{extra})")
        if cmd == "move_to":
            return self.workspace_moves.move(msg)
        if cmd == "do":
            idx = int(msg.get("index", 1))
            val = 1 if msg.get("value") else 0
            return self.backend.command(f"DOExecute({idx},{val})")
        if cmd == "raw":
            command = msg.get("text", "")
            if not self.backend.sim and command not in ("GetPose()", "GetAngle()", "RobotMode()", "GetErrorID()"):
                return False, "Raw commands on the physical robot are limited to read-only status queries"
            return self.backend.command(command)
        if cmd == "check_packing_workspace":
            return self.workspace_moves.check_path(msg.get("path"))
        if cmd in ("teleop", "teleop_sim"):
            if not self.backend.sim:
                return False, "Joint tracking is simulation only; use bounded controller tracking (T)"
            j = [float(v) for v in msg.get("joints", [])]
            if len(j) != 4:
                return False, "need 4 joint values"
            now = time.time()
            if now - self.teleop_last_sent < 0.05:      # 20 Hz max
                return True, "throttled"
            self.teleop_last_sent = now
            return self.backend.command(
                f"JointMovJ({j[0]:.2f},{j[1]:.2f},{j[2]:.2f},{j[3]:.2f},Speed=100)")
        if cmd == "get_state":
            st = self.backend.state()
            return True, json.dumps(st)
        if cmd == "ping":
            return True, "pong"
        if cmd == "staff_call":
            self._on_staff_call("unity")
            return True, "staff call sent"
        if cmd == "staff_end":
            self._on_staff_end()
            return True, "staff session ended"
        return False, f"unknown cmd '{cmd}'"

    # ------------------------------------------------------------------ #
    # staff call
    # ------------------------------------------------------------------ #
    def _on_staff_call(self, source: str) -> None:
        log(f"[staff] CALL received from {source}")
        self.broadcast({"type": "event", "event": "staff_call", "source": source})
        try:
            if sys.platform == "win32":
                import winsound
                winsound.Beep(880, 300)
                winsound.Beep(1175, 300)
        except Exception:  # noqa: BLE001
            pass
        # caretaker accepts (auto-accept after a short pause; in a real
        # deployment this would be a GUI "Answer" button)
        threading.Timer(1.5, self._on_staff_accept).start()

    def _on_staff_accept(self) -> None:
        self.staff_session = True
        log("[staff] accepted -> voice session open")
        self.broadcast({"type": "event", "event": "staff_accepted"})

    def _on_staff_end(self) -> None:
        if self.staff_session:
            self.staff_session = False
            log("[staff] ended")
            self.broadcast({"type": "event", "event": "staff_ended"})

    # ------------------------------------------------------------------ #
    # MQTT staff call
    # ------------------------------------------------------------------ #
    def _mqtt_loop(self) -> None:
        def on_connect(client, *_args):
            client.subscribe(self.mqtt_topic)
            log(f"[mqtt] subscribed to {self.mqtt_topic}")

        def on_message(_client, _userdata, message):
            try:
                data = json.loads(message.payload.decode("utf-8", errors="replace"))
            except ValueError:
                data = {}
            self._on_staff_call(f"mqtt:{data.get('buyer', 'zone')}")

        try:
            client = mqtt_lib.Client(mqtt_lib.CallbackAPIVersion.VERSION2)
        except (AttributeError, TypeError):        # paho-mqtt 1.x
            client = mqtt_lib.Client()
        client.on_connect = on_connect
        client.on_message = on_message
        try:
            client.connect("broker.hivemq.com")
            client.loop_forever()
        except Exception as exc:  # noqa: BLE001
            log(f"[mqtt] failed: {exc}")

    # ------------------------------------------------------------------ #
    # voice channel
    # ------------------------------------------------------------------ #
    def _voice_server(self) -> None:
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("0.0.0.0", self.VOICE_PORT))
        srv.listen(2)
        while self.running:
            try:
                conn, addr = srv.accept()
            except OSError:
                break
            log(f"[voice] client connected {addr}")
            threading.Thread(target=self._voice_client, args=(conn,), daemon=True).start()
            if self.audio:
                threading.Thread(target=self._staff_mic_sender, args=(conn,), daemon=True).start()

    def _voice_client(self, conn: socket.socket) -> None:
        """Receive PCM frames from Unity and play on staff speakers."""
        if not self.audio:
            try:
                while True:
                    hdr = conn.recv(5)
                    if not hdr or len(hdr) < 5:
                        break
                    (length,) = struct.unpack("<I", hdr[1:])
                    got = 0
                    while got < length:
                        chunk = conn.recv(min(4096, length - got))
                        if not chunk:
                            return
                        got += len(chunk)
            except OSError:
                pass
            return

        playback = sd.OutputStream(samplerate=SAMPLE_RATE, channels=1, dtype="int16")
        playback.start()
        try:
            while True:
                hdr = b""
                while len(hdr) < 5:
                    part = conn.recv(5 - len(hdr))
                    if not part:
                        return
                    hdr += part
                channel = hdr[0]
                (length,) = struct.unpack("<I", hdr[1:])
                data = b""
                while len(data) < length:
                    part = conn.recv(min(4096, length - len(data)))
                    if not part:
                        return
                    data += part
                if channel == 1:  # Unity buyer mic -> staff speakers
                    playback.write(data)
        except OSError:
            pass
        finally:
            playback.stop()
            playback.close()
            log("[voice] client disconnected")

    def _staff_mic_sender(self, conn: socket.socket) -> None:
        """Capture staff mic and stream PCM frames to Unity."""
        try:
            def callback(indata, _frames, _time_info, status):  # noqa: ANN001
                if status:
                    pass
                frame = b"\x02" + struct.pack("<I", len(indata)) + indata.tobytes()
                try:
                    conn.sendall(frame)
                except OSError:
                    raise sd.CallbackStop

            with sd.InputStream(samplerate=SAMPLE_RATE, channels=1,
                                dtype="int16", blocksize=VOICE_SAMPLES,
                                callback=callback):
                while self.running:
                    time.sleep(0.2)
        except Exception as exc:  # noqa: BLE001
            log(f"[voice] mic stream ended: {exc}")


# --------------------------------------------------------------------------
# per-connection control handler
# --------------------------------------------------------------------------
class ControlHandler(socketserver.StreamRequestHandler):
    def setup(self) -> None:
        super().setup()
        self.hub: BridgeHub = self.server.hub
        self.hub.register(self.connection)
        log(f"[bridge] Unity client connected {self.client_address}")

    def finish(self) -> None:
        self.hub.unregister(self.connection)
        log("[bridge] Unity client disconnected")
        super().finish()

    def _worker(self, commands) -> None:
        while True:
            msg = commands.get()
            if msg is None:
                return
            self.hub.handle_command(msg, self._send, self.connection)

    def handle(self) -> None:
        import queue
        commands = queue.Queue()
        threading.Thread(target=self._worker, args=(commands,), daemon=True, name="cmd-worker").start()
        try:
            self._read_loop(commands)
        finally:
            commands.put(None)

    def _read_loop(self, commands) -> None:
        buf = b""
        while self.hub.running:
            try:
                chunk = self.connection.recv(4096)
            except OSError:
                break
            if not chunk:
                break
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                if not line.strip():
                    continue
                try:
                    msg = json.loads(line.decode("utf-8"))
                except ValueError:
                    self._send({"type": "ack", "id": 0, "ok": False, "msg": "bad json"})
                    continue
                if msg.get("cmd") == "estop":
                    # never wait behind a blocking sync: stop now, drop queued commands
                    while not commands.empty():
                        try:
                            commands.get_nowait()
                        except Exception:  # noqa: BLE001
                            break
                    self.hub.handle_command(msg, self._send, self.connection)
                else:
                    commands.put(msg)

    def _send(self, payload: dict) -> None:
        data = (json.dumps(payload) + "\n").encode("utf-8")
        with self.hub.lock:
            try:
                self.connection.sendall(data)
            except OSError:
                pass


# --------------------------------------------------------------------------
def main() -> None:
    ap = argparse.ArgumentParser(description="CPE494 MG400 bridge server")
    ap.add_argument("--sim", action="store_true", help="virtual MG400 (no hardware)")
    ap.add_argument("--real", action="store_true", help="connect to a physical robot")
    ap.add_argument("--ip", default="192.168.1.6", help="robot IP (with --real)")
    ap.add_argument("--profile", choices=["mg400", "cr", "auto"], default="auto",
                    help="protocol profile: mg400=29900+CRC16, cr=29999 plain, auto=detect")
    ap.add_argument("--no-audio", action="store_true", help="disable voice relay")
    ap.add_argument("--mqtt-topic", default="494/staffcall")
    ap.add_argument("--no-mqtt", action="store_true", help="disable MQTT staff call")
    ap.add_argument("--user-frame", type=int, default=None,
                    help="select this User frame on the real robot at start (poses are then in that frame)")
    ap.add_argument("--tool-frame", type=int, default=None,
                    help="select this Tool frame on the real robot at start")
    args = ap.parse_args()

    self_test()
    log("[crc] self-test OK (CCITT-FALSE default; profiles: mg400=29900+CRC, cr=29999)")

    backend = (SimBackend() if args.sim or not args.real
               else RealBackend(args.ip, args.profile))
    hub = BridgeHub(backend,
                    mqtt_topic=None if args.no_mqtt else args.mqtt_topic,
                    audio=not args.no_audio)
    if not backend.sim:
        for flag, name in ((args.user_frame, "User"), (args.tool_frame, "Tool")):
            if flag is not None:
                ok, reply = hub.set_frame(name, flag)
                log(f"[frame] {name}({flag}) -> {'ok' if ok else 'FAILED'}: {reply}")
                if not ok:
                    raise SystemExit(f"cannot select {name} frame {flag}: {reply}")
    try:
        hub.serve()
    finally:
        backend.close()


if __name__ == "__main__":
    main()
