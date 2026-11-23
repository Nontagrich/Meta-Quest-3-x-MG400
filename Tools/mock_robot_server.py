#!/usr/bin/env python3
"""Mock robot arm server for the Gem Store AR app (Python 3, standard library only).

Protocol: one JSON object per line (UTF-8, ends with '\\n').
  ping    -> replies {"type":"pong"}
  order   -> prints it, replies {"type":"ack","orderId":...} at once, then sends status picking -> packing -> done, 2 s apart
  teleop  -> moves a simulated pose toward the target at --max-speed mm/s (enable:false holds the position)
          (Pick / Place macros from the app are plain teleop sequences: descend, grip on/off, lift; see Docs/Robot_TCP_Interface.md)
  estop   -> stops at once and ignores teleop until estop_reset
  (always) streams {"type":"robot_pose","x","y","z","r"} at --pose-hz (default 10 Hz), units mm

Run:   python Tools/mock_robot_server.py            (port 5005)
       python Tools/mock_robot_server.py --port 5005 --delay 2
"""
import argparse
import json
import socket
import threading
import time


def send(conn, lock, obj):
    data = (json.dumps(obj, separators=(",", ":")) + "\n").encode("utf-8")
    with lock:
        conn.sendall(data)


def run_order(conn, lock, order, delay):
    order_id = str(order.get("orderId", ""))
    for status in ("picking", "packing", "done"):
        time.sleep(delay)
        try:
            send(conn, lock, {"type": "status", "orderId": order_id, "status": status, "message": ""})
            print(f"  -> status {status} (order {order_id})", flush=True)
        except OSError:
            print("  (client gone, stopped sending status)", flush=True)
            return


class RobotSim:
    """Simulated arm of one client: the pose moves toward the last teleop target at max_speed (mm/s) and is streamed as robot_pose."""

    LIMITS = ((100.0, 400.0), (-150.0, 150.0), (0.0, 150.0))  # x, y, z in mm (same box as the app's default TeleopConfig)

    def __init__(self, conn, lock, max_speed, pose_hz):
        self.conn = conn
        self.lock = lock
        self.max_speed = max_speed
        self.period = 1.0 / pose_hz
        self.pose = [250.0, 0.0, 75.0]
        self.target = list(self.pose)
        self.enabled = False
        self.estopped = False
        self.grip = False
        self.last_seq = -1
        self.state_lock = threading.Lock()
        self.running = True

    def on_teleop(self, msg):
        with self.state_lock:
            if self.estopped:
                print("[TELEOP] ignored: E-STOP active", flush=True)
                return
            if not msg.get("enable", False):
                self.enabled = False
                self.target = list(self.pose)  # stop where it is
                self.last_seq = -1
                print("[TELEOP] enable=false -> holding position", flush=True)
                return
            seq = msg.get("seq", 0)
            if isinstance(seq, int) and seq <= self.last_seq:
                print(f"[TELEOP] stale seq {seq} ignored", flush=True)
                return
            try:
                point = [float(msg["x"]), float(msg["y"]), float(msg["z"])]
            except (KeyError, TypeError, ValueError):
                print(f"[TELEOP] bad message: {msg}", flush=True)
                return
            if any(v != v for v in point):  # NaN
                return
            # the robot enforces its own workspace too (never trust the sender)
            self.target = [min(max(point[i], self.LIMITS[i][0]), self.LIMITS[i][1]) for i in range(3)]
            self.enabled = True
            new_grip = bool(msg.get("grip", False))
            if new_grip != self.grip:
                print(f"[GRIP] suction/gripper {'ON' if new_grip else 'OFF'} at z={self.pose[2]:.1f}", flush=True)
            self.grip = new_grip
            self.last_seq = seq if isinstance(seq, int) else self.last_seq
            print(f"[TELEOP] seq={seq} target=({self.target[0]:.1f}, {self.target[1]:.1f}, {self.target[2]:.1f}) grip={self.grip}", flush=True)

    def on_estop(self):
        with self.state_lock:
            self.estopped = True
            self.enabled = False
            self.target = list(self.pose)
            print("[ESTOP] robot stopped, locked until estop_reset", flush=True)

    def on_reset(self):
        with self.state_lock:
            self.estopped = False
            print("[ESTOP] reset", flush=True)

    def run(self):
        step = 0.02
        last_pose_sent = 0.0
        while self.running:
            time.sleep(step)
            with self.state_lock:
                if self.enabled and not self.estopped:
                    delta = [self.target[i] - self.pose[i] for i in range(3)]
                    dist = sum(d * d for d in delta) ** 0.5
                    reach = self.max_speed * step
                    if dist <= reach:
                        self.pose = list(self.target)
                    elif dist > 0:
                        self.pose = [self.pose[i] + delta[i] / dist * reach for i in range(3)]
                snapshot = list(self.pose)
            now = time.time()
            if now - last_pose_sent >= self.period:
                last_pose_sent = now
                try:
                    send(self.conn, self.lock, {"type": "robot_pose", "x": round(snapshot[0], 1), "y": round(snapshot[1], 1),
                                                "z": round(snapshot[2], 1), "r": 0.0, "grip": self.grip})
                except OSError:
                    return


def handle_client(conn, addr, delay, no_ack, seen, max_speed, pose_hz):
    print(f"[+] client connected: {addr[0]}:{addr[1]}", flush=True)
    lock = threading.Lock()
    sim = RobotSim(conn, lock, max_speed, pose_hz)
    threading.Thread(target=sim.run, daemon=True).start()
    buffer = b""
    try:
        while True:
            chunk = conn.recv(4096)
            if not chunk:
                break
            buffer += chunk
            while b"\n" in buffer:
                raw, buffer = buffer.split(b"\n", 1)
                line = raw.decode("utf-8", errors="replace").strip()
                if not line:
                    continue
                try:
                    msg = json.loads(line)
                except ValueError:
                    print(f"[!] not JSON: {line}", flush=True)
                    continue
                kind = msg.get("type")
                if kind == "ping":
                    send(conn, lock, {"type": "pong"})
                elif kind == "pong":
                    pass
                elif kind == "order":
                    items = msg.get("items", [])
                    print(f"[ORDER] id={msg.get('orderId')} total={msg.get('total')} time={msg.get('time')}", flush=True)
                    for item in items:
                        # setting / metal are informational (the robot only picks the gem by shape + color); older apps may omit them
                        extra = f" setting={item.get('setting', 'loose')} metal={item.get('metal', 'none')}"
                        print(f"        - {item.get('gemId')} ({item.get('color')} {item.get('shape')}){extra}", flush=True)
                    if no_ack:
                        print("  (--no-ack: not acknowledging)", flush=True)
                        continue
                    send(conn, lock, {"type": "ack", "orderId": str(msg.get("orderId", ""))})
                    print(f"  -> ack (order {msg.get('orderId')})", flush=True)
                    # a resent order (same id) is acknowledged again but only processed once
                    oid = str(msg.get("orderId", ""))
                    if oid in seen:
                        print("  (duplicate order id, not processed again)", flush=True)
                        continue
                    seen.add(oid)
                    threading.Thread(target=run_order, args=(conn, lock, msg, delay), daemon=True).start()
                elif kind == "teleop":
                    sim.on_teleop(msg)
                elif kind == "estop":
                    sim.on_estop()
                elif kind == "estop_reset":
                    sim.on_reset()
                else:
                    print(f"[?] unknown message: {line}", flush=True)
    except OSError:
        pass
    finally:
        sim.running = False
        conn.close()
        print(f"[-] client disconnected: {addr[0]}:{addr[1]}", flush=True)


def main():
    parser = argparse.ArgumentParser(description="Mock robot arm TCP server")
    parser.add_argument("--host", default="0.0.0.0", help="address to listen on (default all interfaces)")
    parser.add_argument("--port", type=int, default=5005)
    parser.add_argument("--no-ack", action="store_true", help="never acknowledge orders (to test the resend logic)")
    parser.add_argument("--delay", type=float, default=2.0, help="seconds between order status messages")
    parser.add_argument("--max-speed", type=float, default=100.0, help="simulated robot speed in mm/s (default 100)")
    parser.add_argument("--pose-hz", type=float, default=10.0, help="robot_pose messages per second (default 10)")
    args = parser.parse_args()

    seen = set()  # order ids already processed (orders may arrive more than once)
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((args.host, args.port))
    server.listen(5)
    print(f"Mock robot server listening on {args.host}:{args.port} (Ctrl+C to stop)", flush=True)
    try:
        while True:
            conn, addr = server.accept()
            threading.Thread(target=handle_client, args=(conn, addr, args.delay, args.no_ack, seen, args.max_speed, args.pose_hz), daemon=True).start()
    except KeyboardInterrupt:
        print("\nstopped")
    finally:
        server.close()


if __name__ == "__main__":
    main()
