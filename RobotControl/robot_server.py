#!/usr/bin/env python3
"""robot_server.py - TCP order server between the Unity store app and the MG400 (our own code, no bridge).

    python robot_server.py                 DRY RUN: log every planned step, send the statuses, never touch the robot
    python robot_server.py --arm           armed: the robot really moves (speed from robot_config.json, max 30 %)
    options: --host 0.0.0.0 --port 5005 --config FILE --log-dir DIR --dry-delay S --max-age S

Protocol (same as Unity's RobotLink; one JSON object per line, UTF-8):
  Unity -> server  {"type":"ping"}                                   -> {"type":"pong"}
                   {"type":"order","orderId":"1234","createdAt":1700000000,
                    "items":[{"gemId":"gem_ruby_round","slot":1,"shape":"round","color":"red",...}]}
                                                                     -> {"type":"ack","orderId":"1234"} at once, then
                   {"type":"status","orderId":"1234","status":"picking|packing|done|error","message":"..."}
                   {"type":"estop"}  /  {"type":"estop_reset"}
Slot 1..9 = physical pick pose #1..#9 (cabinet Slot_0..Slot_8 + 1). The queue is kept in memory only. Orders older than
--max-age seconds (default 600; needs "createdAt", unix seconds) are rejected with status "error".
E-STOP (message from Unity, or Ctrl+C on this server): normal stop first, DO9/DO10 OFF (DO10 is NOT pulsed: the gem may
still be held), further orders are rejected until {"type":"estop_reset"}.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import queue
import socket
import sys
import threading
import time
from collections import OrderedDict
from datetime import datetime

import gem_picker as gp
import mg400

MAX_ITEMS = 9
SLOT_MIN, SLOT_MAX = 1, 9
MAX_AGE_S = 600.0
MAX_QUEUE = 50
MAX_LINE = 1 << 20

slog = logging.getLogger("robot_server")


class Client:
    """One connected Unity app."""

    def __init__(self, sock: socket.socket, addr):
        self.sock, self.addr = sock, addr
        self.alive = True
        self._lock = threading.Lock()

    def send(self, obj: dict) -> bool:
        data = (json.dumps(obj, separators=(",", ":")) + "\n").encode("utf-8")
        with self._lock:
            if not self.alive:
                return False
            try:
                self.sock.sendall(data)
                return True
            except OSError:
                self.alive = False
                return False

    def close(self) -> None:
        self.alive = False
        try:
            self.sock.close()
        except OSError:
            pass


class Job:
    def __init__(self, order_id: str, picks: list, items: list):
        self.order_id, self.picks, self.items = order_id, picks, items


class Server:
    """Order queue + worker. robot=None means dry run. All robot motion happens on the worker thread; E-STOP is
    handled directly on the network thread."""

    def __init__(self, cfg: gp.Config, robot=None, dry_delay: float = 0.3, max_age: float = MAX_AGE_S,
                 out=None, clock=time.time, sleep=time.sleep):
        self.cfg, self.robot = cfg, robot
        self.dry_delay, self.max_age, self.clock, self.sleep = dry_delay, max_age, clock, sleep
        self.out = out or (lambda text: print(text, flush=True))
        self.clients: list[Client] = []
        self.clients_lock = threading.Lock()
        self.jobs: queue.Queue = queue.Queue()
        self.seen: OrderedDict = OrderedDict()
        self.lock = threading.RLock()
        self.estopped = False
        self.abort = threading.Event()
        self.current: Job | None = None
        self.running = True
        self.worker: threading.Thread | None = None
        self._warned_teleop = False
        self._reconnect = False
        self._say_lock = threading.Lock()

    # -- output ------------------------------------------------------------------------------------------------
    def say(self, text: str) -> None:
        with self._say_lock:
            self.out(f"{datetime.now().strftime('%H:%M:%S')} {text}")
        slog.info(text)

    # -- sending -----------------------------------------------------------------------------------------------
    def broadcast(self, obj: dict) -> None:
        with self.clients_lock:
            clients = list(self.clients)
        for client in clients:
            client.send(obj)

    def status(self, order_id: str, status: str, message: str = "") -> None:
        self.say(f"-> status {order_id} {status}" + (f": {message}" if message else ""))
        self.broadcast({"type": "status", "orderId": order_id, "status": status, "message": message})

    # -- message handling (network threads) --------------------------------------------------------------------
    def handle_message(self, client: Client, msg) -> None:
        if not isinstance(msg, dict):
            return
        kind = msg.get("type")
        if kind == "ping":
            client.send({"type": "pong"})
        elif kind == "pong":
            pass
        elif kind == "order":
            self._on_order(client, msg)
        elif kind == "estop":
            self.estop("Unity")
        elif kind == "estop_reset":
            self.estop_reset("Unity")
        elif kind == "teleop":
            if not self._warned_teleop:
                self._warned_teleop = True
                self.say("teleop messages are ignored: this server only runs pick orders (hand-follow is not supported)")
        else:
            slog.debug("ignored message type %r", kind)

    def _validate(self, msg: dict) -> tuple[list, str]:
        """Returns (picks, error). picks[k] is the pick number (= slot) of item k."""
        items = msg.get("items")
        if not isinstance(items, list) or not items:
            return [], "order has no items"
        if len(items) > MAX_ITEMS:
            return [], f"too many items ({len(items)}, max {MAX_ITEMS})"
        picks = []
        for index, item in enumerate(items, 1):
            slot = item.get("slot") if isinstance(item, dict) else None
            if isinstance(slot, bool) or not isinstance(slot, int):
                return [], f"item {index}: missing or non-integer slot ({slot!r})"
            if not SLOT_MIN <= slot <= SLOT_MAX or slot not in self.cfg.picks:
                return [], f"item {index}: unknown slot {slot} (valid {SLOT_MIN}..{SLOT_MAX})"
            if slot in picks:
                return [], f"item {index}: slot {slot} is requested twice (only one gem per slot)"
            picks.append(slot)
        return picks, ""

    def _on_order(self, client: Client, msg: dict) -> None:
        order_id = str(msg.get("orderId") or "").strip()
        if not order_id:
            self.say("order without orderId ignored")
            return
        client.send({"type": "ack", "orderId": order_id})        # ack every copy, also duplicates and rejects
        with self.lock:
            if order_id in self.seen:
                self.say(f"order {order_id}: duplicate (acked again, not queued twice)")
                return
            self.seen[order_id] = self.clock()
            while len(self.seen) > 500:
                self.seen.popitem(last=False)
        self.say(f"<- order {order_id}: {len(msg.get('items') or [])} item(s)")

        created = msg.get("createdAt")
        if isinstance(created, (int, float)) and not isinstance(created, bool):
            age = self.clock() - float(created)
            if age > self.max_age:
                self.status(order_id, "error", f"order too old ({age / 60:.0f} min, limit {self.max_age / 60:.0f} min)")
                return
        if self.estopped:
            self.status(order_id, "error", "E-STOP active: order rejected until estop_reset")
            return
        picks, error = self._validate(msg)
        if error:
            self.status(order_id, "error", error)
            return
        if self.jobs.qsize() >= MAX_QUEUE:
            self.status(order_id, "error", "order queue is full")
            return
        job = Job(order_id, picks, msg["items"])
        for index, (n, item) in enumerate(zip(picks, job.items), 1):
            self.say(f"   item {index}/{len(picks)}: slot {n} -> pick {n}  gem={item.get('gemId')}")
        self.jobs.put(job)
        self.say(f"order {order_id} queued ({self.jobs.qsize()} waiting)")

    # -- E-STOP ------------------------------------------------------------------------------------------------
    def estop(self, source: str) -> None:
        with self.lock:
            repeat = self.estopped and self.current is None
            self.estopped = True
            self.abort.set()
            if self.robot is not None:
                self.robot.abort.set()
        if repeat:
            self.say(f"E-STOP ({source}): already stopped")
            return
        self.say(f"!! E-STOP ({source}): stopping")
        cancelled = []
        while True:
            try:
                job = self.jobs.get_nowait()
            except queue.Empty:
                break
            cancelled.append(job)
            self.jobs.task_done()
        for job in cancelled:
            self.status(job.order_id, "error", "cancelled by E-STOP")
        if self.robot is not None:
            try:
                text, latched = self.robot.stop_motion()
                self.say(f"!! {text}")
                failures = self.robot.outputs_off([self.cfg.vacuum_do, self.cfg.blow_do])
                for failure in failures:
                    self.say(f"!! {failure}")
                if not failures:
                    self.say(f"!! DO{self.cfg.vacuum_do} and DO{self.cfg.blow_do} are OFF")
                if self.robot.gem_possibly_held:
                    self.say(self._held_warning())
                if latched:
                    self.say("!! EmergencyStop() was used: the robot is latched in an alarm (it is cleared on estop_reset)")
            except Exception as exc:        # noqa: BLE001 - the stop path must never raise
                self.say(f"!! stop failed: {exc} - use the robot's physical E-STOP")
        else:
            self.say("!! (dry run: no robot to stop)")

    @staticmethod
    def _held_warning() -> str:
        return ("!! WARNING: the gem may STILL BE HELD. The valve latches, so DO9/DO10 OFF does not release it and DO10 "
                "was NOT pulsed. Move the arm over a safe place and run 'python gem_picker.py grip off'.")

    def estop_reset(self, source: str) -> None:
        with self.lock:
            self.estopped = False
            self.abort.clear()
            if self.robot is not None:
                self.robot.abort.clear()
        self.say(f"E-STOP reset ({source}): orders are accepted again")
        if self.robot is not None:
            try:
                self.robot.clear_error()
                self.say("ClearError() sent (check in DobotStudio that the cause of the stop is gone)")
            except mg400.MG400Error as exc:
                self.say(f"ClearError() refused: {exc}")

    # -- worker ------------------------------------------------------------------------------------------------
    def start_worker(self) -> None:
        self.worker = threading.Thread(target=self._work, name="orders", daemon=True)
        self.worker.start()

    def stop(self) -> None:
        self.running = False

    def wait_idle(self, timeout: float = 30.0) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.jobs.unfinished_tasks == 0:
                return True
            time.sleep(0.01)
        return False

    def _work(self) -> None:
        while self.running:
            try:
                job = self.jobs.get(timeout=0.2)
            except queue.Empty:
                continue
            try:
                self._run_job(job)
            except Exception as exc:        # noqa: BLE001 - the server must survive anything
                self.say(f"!! unexpected error in order {job.order_id}: {type(exc).__name__}: {exc}")
                self.status(job.order_id, "error", f"internal error: {exc}")
            finally:
                self.current = None
                self.jobs.task_done()

    def _line_out(self, prefix: str):
        buf = []

        def out(text: str = "", end: str = "\n") -> None:
            buf.append(text)
            if "\n" in end:
                self.say(f"{prefix} {''.join(buf)}".rstrip())
                buf.clear()
        return out

    def _run_job(self, job: Job) -> None:
        n_items = len(job.picks)
        with self.lock:
            if self.estopped:
                self.status(job.order_id, "error", "cancelled by E-STOP")
                return
            self.current = job
        self.say(f"== order {job.order_id}: picks {job.picks} ({'ARMED' if self.robot else 'dry run'})")
        state = {"key": None}

        def on_step(index: int, total: int, step: gp.Step) -> None:
            if self.abort.is_set():
                raise mg400.Aborted("stopped by E-STOP")
            if step.item and (step.item, step.phase) != state["key"]:
                state["key"] = (step.item, step.phase)
                pick = job.picks[step.item - 1]
                if step.phase == "picking":
                    self.status(job.order_id, "picking", f"gem {step.item}/{n_items}: moving to pick {pick} and gripping it")
                else:
                    self.status(job.order_id, "packing", f"gem {step.item}/{n_items}: carrying it to the drop and releasing it")

        try:
            cur = self._prepare()
            plan = gp.build_order_plan(self.cfg, job.picks, cur)
            out = self._line_out(f"[{job.order_id}]")
            if self.robot is None:
                self._dry_execute(plan, on_step, out)
            else:
                gp.execute(self.robot, self.cfg, plan, False, None, out, self.sleep, on_step=on_step)
        except mg400.Aborted as exc:
            self.status(job.order_id, "error", str(exc))
            return
        except (mg400.MG400Error, gp.PickerError) as exc:
            self.say(f"!! order {job.order_id} failed: {exc}")
            if isinstance(exc, mg400.LinkError):
                self._reconnect = True
            if self.robot is not None:
                gp.abort_safely(self.robot, self.cfg, self.say, "error: stopping")
            self.status(job.order_id, "error", str(exc))
            return
        self.status(job.order_id, "done", f"{n_items} gem(s) delivered")

    def _prepare(self) -> tuple:
        """Dry run: the arm is assumed to be parked. Armed: reconnect if needed, then the same preflight as the CLI."""
        if self.robot is None:
            return self.cfg.safe_pose
        if self._reconnect:
            self.robot.close()
            self.robot.connect()
            self._reconnect = False
        self.robot.motion_sent = False          # a stop after a failure is only for motion of THIS order
        return gp.preflight(self.robot, self.cfg, self.say)

    def _dry_execute(self, plan: gp.Plan, on_step, out) -> None:
        total = len(plan.steps)
        for index, step in enumerate(plan.steps, 1):
            on_step(index, total, step)
            target = f"  -> {gp.fmt(step.target)}" if step.target else ""
            out(f"[{index}/{total}] {step.label}{target} ... ", end="")
            self.sleep(self.dry_delay)
            out("DRY RUN (not sent)")

    # -- networking --------------------------------------------------------------------------------------------
    def serve_client(self, client: Client) -> None:
        with self.clients_lock:
            self.clients.append(client)
        self.say(f"Unity connected from {client.addr[0]}:{client.addr[1]}")
        buf = b""
        try:
            while self.running and client.alive:
                data = client.sock.recv(4096)
                if not data:
                    break
                buf += data
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        msg = json.loads(line.decode("utf-8"))
                    except (ValueError, UnicodeDecodeError):
                        self.say(f"ignored a line that is not valid JSON: {line[:80]!r}")
                        continue
                    self.handle_message(client, msg)
                if len(buf) > MAX_LINE:
                    buf = b""
        except OSError:
            pass
        finally:
            with self.clients_lock:
                if client in self.clients:
                    self.clients.remove(client)
            client.close()
            self.say(f"Unity disconnected ({client.addr[0]}:{client.addr[1]})")

    def listen(self, host: str, port: int) -> socket.socket:
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
                # Windows: SO_REUSEADDR would let a second server bind the same port and steal connections.
                server.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            else:
                # POSIX: SO_REUSEADDR only skips TIME_WAIT, a live listener still makes bind() fail.
                server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server.bind((host, port))
        except OSError:
            server.close()
            raise
        server.listen(5)
        server.settimeout(0.5)
        return server

    def accept_loop(self, server: socket.socket) -> None:
        while self.running:
            try:
                sock, addr = server.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=self.serve_client, args=(Client(sock, addr),), daemon=True).start()


def setup_server_log(log_dir: str) -> str:
    os.makedirs(log_dir, exist_ok=True)
    path = os.path.join(log_dir, datetime.now().strftime("server_%Y%m%d_%H%M%S.log"))
    slog.setLevel(logging.DEBUG)
    handler = logging.FileHandler(path, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s.%(msecs)03d %(message)s", "%H:%M:%S"))
    slog.addHandler(handler)
    slog.propagate = False
    return path


def find_listener_pids(port: int) -> list:
    """PIDs of the processes listening on TCP <port> (best effort, [] if unknown)."""
    import subprocess
    pids = []
    try:
        if os.name == "nt":
            out = subprocess.run(["netstat", "-ano", "-p", "tcp"], capture_output=True, text=True, timeout=10).stdout
            for line in out.splitlines():
                parts = line.split()
                if len(parts) >= 5 and parts[3].upper() == "LISTENING" and parts[1].rsplit(":", 1)[-1] == str(port):
                    pids.append(int(parts[4]))
        else:
            out = subprocess.run(["lsof", "-nP", f"-iTCP:{port}", "-sTCP:LISTEN", "-t"],
                                 capture_output=True, text=True, timeout=10).stdout
            pids = [int(x) for x in out.split()]
    except (OSError, ValueError, subprocess.SubprocessError):
        pass
    return sorted(set(pids))


def listen_error_text(host: str, port: int, exc: OSError) -> str:
    pids = find_listener_pids(port)
    text = f"ERROR: cannot listen on {host}:{port}: {exc}"
    if pids:
        who = ", ".join(str(p) for p in pids)
        text += f"\nPort {port} is already in use by PID {who}. Stop it first, e.g.:  Stop-Process -Id {pids[0]}"
    else:
        text += f"\nPort {port} is probably already in use by another program (PID unknown). Find it with:  netstat -ano | findstr :{port}"
    return text


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--arm", action="store_true", help="allow real robot motion (default: dry run)")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=5005)
    ap.add_argument("--config", default=gp.DEFAULT_CONFIG)
    ap.add_argument("--log-dir", default=gp.DEFAULT_LOG_DIR)
    ap.add_argument("--dry-delay", type=float, default=0.3, help="seconds per step in dry-run mode")
    ap.add_argument("--max-age", type=float, default=MAX_AGE_S, help="reject orders older than this many seconds")
    args = ap.parse_args(argv)

    try:
        cfg = gp.load_config(args.config)
    except gp.PickerError as exc:
        print(f"ERROR: {exc}")
        return 1
    log_path = setup_server_log(args.log_dir)
    robot = None
    if args.arm:
        mg400.setup_logging(args.log_dir)
        robot = mg400.MG400(cfg.robot_ip, cfg.dashboard_port, cfg.motion_port, None, cfg.inline_frame_args)
        try:
            robot.connect()
        except mg400.MG400Error as exc:
            print(f"ERROR: {exc}\nCheck: robot on, cable, PC Ethernet 192.168.1.x, DobotStudio closed.")
            return 1
    server = Server(cfg, robot, args.dry_delay, args.max_age)
    try:
        sock = server.listen(args.host, args.port)
    except OSError as exc:
        print(listen_error_text(args.host, args.port, exc))
        if robot:
            robot.close()
        return 1
    server.say(f"log file: {log_path}")
    if robot:
        server.say(f"*** ARMED: the robot at {cfg.robot_ip} WILL MOVE (speed {cfg.speed_percent} %, cap {gp.HARD_SPEED_CAP} %) ***")
        server.say("Keep a hand on the physical E-STOP. Ctrl+C = stop (normal stop first) and exit.")
    else:
        server.say("DRY RUN: no robot contact, orders are only logged and answered with statuses (use --arm to move)")
    server.say(f"listening on {args.host}:{args.port}")
    server.start_worker()
    accept = threading.Thread(target=server.accept_loop, args=(sock,), daemon=True)
    accept.start()
    try:
        while True:
            time.sleep(0.5)
    except KeyboardInterrupt:
        server.say("Ctrl+C")
        server.estop("Ctrl+C")
    finally:
        server.stop()
        sock.close()
        if robot:
            robot.close()
        mg400.close_logging()
    return 0


if __name__ == "__main__":
    sys.exit(main())
