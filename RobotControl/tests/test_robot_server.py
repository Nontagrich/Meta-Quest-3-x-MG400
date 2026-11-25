"""Unit tests for robot_server message handling with a fake robot. Run:  python -m unittest discover -s tests -v"""

from __future__ import annotations

import json
import os
import socket
import sys
import threading
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)

import gem_picker as gp          # noqa: E402
import mg400                     # noqa: E402
import robot_server as rs        # noqa: E402
from fake_robot import FakeController, factory   # noqa: E402

CONFIG = os.path.join(os.path.dirname(HERE), "robot_config.json")


class FakeClient:
    def __init__(self):
        self.sent = []
        self.alive = True
        self.addr = ("test", 0)

    def send(self, obj):
        self.sent.append(obj)
        return True

    def close(self):
        self.alive = False

    def statuses(self, order_id=None):
        return [(m["status"], m["message"]) for m in self.sent
                if m.get("type") == "status" and (order_id is None or m["orderId"] == order_id)]


def item(slot, gem="gem_x"):
    return {"gemId": gem, "slot": slot, "shape": "round", "color": "red", "setting": "loose", "metal": "none"}


def order(oid, *slots, **extra):
    msg = {"type": "order", "orderId": oid, "items": [item(s) for s in slots]}
    msg.update(extra)
    return msg


class Base(unittest.TestCase):
    armed = False

    def setUp(self):
        self.cfg = gp.load_config(CONFIG)
        self.lines = []
        self.controller = FakeController()
        self.robot = None
        if self.armed:
            self.robot = mg400.MG400(link_factory=factory(self.controller))
            self.robot.connect()
            self.robot.poll_s = 0.001
        self.now = 1_700_000_000.0
        self.server = rs.Server(self.cfg, self.robot, dry_delay=0.0, out=self.lines.append,
                                clock=lambda: self.now, sleep=lambda s: None)
        self.client = FakeClient()
        self.server.clients.append(self.client)
        self.server.start_worker()

    def tearDown(self):
        self.server.stop()

    def send(self, msg):
        self.server.handle_message(self.client, msg)

    def run_order(self, msg):
        self.send(msg)
        self.assertTrue(self.server.wait_idle(20), "worker did not finish")

    @property
    def text(self):
        return "\n".join(self.lines)


class DryRunTests(Base):
    def test_ping_pong(self):
        self.send({"type": "ping"})
        self.assertEqual(self.client.sent, [{"type": "pong"}])

    def test_one_gem_acks_first_then_picking_packing_done(self):
        self.run_order(order("1234", 3))
        self.assertEqual(self.client.sent[0], {"type": "ack", "orderId": "1234"})
        statuses = self.client.statuses("1234")
        self.assertEqual([s for s, _ in statuses], ["picking", "packing", "done"])
        self.assertIn("pick 3", statuses[0][1])
        self.assertIn("MovJ to above pick 3", self.text)          # the planned steps are printed
        self.assertIn("DRY RUN (not sent)", self.text)
        self.assertEqual(self.controller.log, [])                  # the robot was never contacted

    def test_two_gems_statuses_per_item_and_correct_pick_numbers(self):
        self.run_order(order("2000", 2, 7))
        self.assertEqual([s for s, _ in self.client.statuses()], ["picking", "packing", "picking", "packing", "done"])
        self.assertIn("gem 1/2", self.client.statuses()[0][1])
        self.assertIn("gem 2/2", self.client.statuses()[2][1])
        self.assertIn("MovJ to above pick 2", self.text)
        self.assertIn("MovJ to above pick 7", self.text)
        self.assertLess(self.text.index("above pick 2"), self.text.index("above pick 7"))
        self.assertIn("slot 2 -> pick 2", self.text)
        self.assertIn("slot 7 -> pick 7", self.text)

    def test_setup_runs_once_per_order(self):
        self.run_order(order("2001", 1, 2))
        self.assertEqual(self.text.count("enable, User 1 / Tool 1"), 1)

    def test_bad_slots_are_acked_then_rejected_with_error(self):
        cases = {
            "3001": order("3001", 0), "3002": order("3002", 10), "3003": order("3003", -1),
            "3004": {"type": "order", "orderId": "3004", "items": [{"gemId": "x"}]},
            "3005": {"type": "order", "orderId": "3005", "items": [{"gemId": "x", "slot": "3"}]},
            "3006": {"type": "order", "orderId": "3006", "items": [{"gemId": "x", "slot": 2.5}]},
            "3007": order("3007", 4, 4), "3008": {"type": "order", "orderId": "3008", "items": []},
            "3009": order("3009", *([1] * 10)), "3010": {"type": "order", "orderId": "3010"},
        }
        for oid, msg in cases.items():
            self.client.sent.clear()
            self.send(msg)
            self.assertEqual(self.client.sent[0], {"type": "ack", "orderId": oid}, oid)
            self.assertEqual(self.client.statuses(oid)[0][0], "error", oid)
        self.assertTrue(self.server.wait_idle(5))
        self.assertEqual(self.server.jobs.qsize(), 0)
        self.assertIn("unknown slot 0", self.text)
        self.assertIn("unknown slot 10", self.text)

    def test_all_nine_slots_are_valid(self):
        self.run_order(order("4000", *range(1, 10)))
        self.assertEqual(self.client.statuses()[-1][0], "done")
        for n in range(1, 10):
            self.assertIn(f"MovJ to above pick {n}", self.text)

    def test_old_order_is_rejected_fresh_and_undated_are_accepted(self):
        self.run_order(order("5001", 1, createdAt=self.now - 11 * 60))
        self.assertEqual(self.client.statuses("5001")[0][0], "error")
        self.assertIn("too old", self.client.statuses("5001")[0][1])
        self.run_order(order("5002", 1, createdAt=self.now - 5 * 60))
        self.assertEqual(self.client.statuses("5002")[-1][0], "done")
        self.run_order(order("5003", 1))
        self.assertEqual(self.client.statuses("5003")[-1][0], "done")

    def test_duplicate_order_is_acked_twice_but_runs_once(self):
        self.run_order(order("6000", 5))
        self.run_order(order("6000", 5))
        acks = [m for m in self.client.sent if m["type"] == "ack"]
        self.assertEqual(len(acks), 2)
        self.assertEqual([s for s, _ in self.client.statuses("6000")].count("done"), 1)

    def test_orders_run_one_at_a_time_in_order(self):
        self.send(order("7001", 1))
        self.send(order("7002", 2))
        self.assertTrue(self.server.wait_idle(20))
        done = [m["orderId"] for m in self.client.sent if m.get("status") == "done"]
        self.assertEqual(done, ["7001", "7002"])

    def test_unknown_types_are_ignored_and_teleop_warns_once(self):
        self.send({"type": "mystery"})
        self.send({"type": "teleop", "enable": True})
        self.send({"type": "teleop", "enable": True})
        self.send(["not", "a", "dict"])
        self.assertEqual(self.client.sent, [])
        self.assertEqual(self.text.count("teleop messages are ignored"), 1)

    def test_estop_stops_a_running_order_and_blocks_new_ones_until_reset(self):
        calls = {"n": 0}

        def sleep(seconds):
            calls["n"] += 1
            if calls["n"] == 3:
                self.server.estop("test")
        self.server.sleep = sleep
        self.run_order(order("8001", 1))
        self.assertEqual(self.client.statuses("8001")[-1][0], "error")
        self.assertIn("E-STOP", self.client.statuses("8001")[-1][1])
        self.run_order(order("8002", 2))
        self.assertIn("E-STOP active", self.client.statuses("8002")[0][1])
        self.send({"type": "estop_reset"})
        self.server.sleep = lambda s: None
        self.run_order(order("8003", 3))
        self.assertEqual(self.client.statuses("8003")[-1][0], "done")

    def test_estop_cancels_queued_orders(self):
        gate = threading.Event()
        started = threading.Event()

        def sleep(seconds):
            started.set()
            gate.wait(5)
        self.server.sleep = sleep
        self.send(order("9001", 1))
        self.send(order("9002", 2))
        self.assertTrue(started.wait(5))
        self.send({"type": "estop"})
        gate.set()
        self.assertTrue(self.server.wait_idle(10))
        self.assertEqual(self.client.statuses("9001")[-1][0], "error")
        self.assertEqual(self.client.statuses("9002")[-1], ("error", "cancelled by E-STOP"))
        self.assertNotIn("done", [s for s, _ in self.client.statuses()])

    def test_repeated_estop_is_harmless(self):
        self.send({"type": "estop"})
        self.send({"type": "estop"})
        self.assertIn("already stopped", self.text)


class ArmedTests(Base):
    armed = True

    def test_order_moves_the_fake_robot_through_the_pick_sequence(self):
        self.run_order(order("1111", 3))
        self.assertEqual([s for s, _ in self.client.statuses()], ["picking", "packing", "done"])
        c = self.cfg
        log = self.controller.log
        self.assertIn("EnableRobot()", log)
        self.assertIn(f"SpeedFactor({c.speed_percent})", log)
        above3 = f"MovJ({c.picks[3][0]:.2f},{c.picks[3][1]:.2f},{c.pick_approach_z:.2f},{c.picks[3][3]:.2f})"
        self.assertIn(above3, log)
        self.assertIn(f"MovL({c.picks[3][0]:.2f},{c.picks[3][1]:.2f},{c.picks[3][2]:.2f},{c.picks[3][3]:.2f})", log)
        self.assertEqual(self.controller.do_history, [(10, 0), (9, 1), (9, 0), (9, 0), (10, 1), (10, 0)])
        self.assertFalse(self.controller.both_on_ever())
        for got, want in zip(self.controller.pose, c.safe_pose):
            self.assertAlmostEqual(got, want, places=1)
        self.assertNotIn("EmergencyStop()", log)

    def test_two_gems_pick_in_order_with_one_setup(self):
        self.run_order(order("1112", 8, 1))
        log = self.controller.log
        self.assertEqual(log.count("EnableRobot()"), 1)
        c = self.cfg

        def down(n):
            return f"MovL({c.picks[n][0]:.2f},{c.picks[n][1]:.2f},{c.picks[n][2]:.2f},{c.picks[n][3]:.2f})"
        self.assertLess(log.index(down(8)), log.index(down(1)))
        self.assertEqual(self.client.statuses()[-1][0], "done")

    def test_robot_error_stops_reports_error_and_keeps_the_server_running(self):
        self.controller.fail["MovJ"] = -3
        self.run_order(order("2222", 1))
        status, message = self.client.statuses("2222")[-1]
        self.assertEqual(status, "error")
        self.assertIn("-3", message)
        self.assertIn("ResetRobot()", self.controller.log)           # normal stop first
        self.assertNotIn("EmergencyStop()", self.controller.log)
        self.assertEqual((self.controller.do[9], self.controller.do[10]), (0, 0))
        self.controller.fail.clear()
        self.run_order(order("2223", 2))                              # still alive
        self.assertEqual(self.client.statuses("2223")[-1][0], "done")

    def test_alarm_before_the_order_is_reported_without_motion(self):
        self.controller.errors = [17]
        self.run_order(order("3333", 1))
        status, message = self.client.statuses("3333")[-1]
        self.assertEqual(status, "error")
        self.assertIn("alarm", message)
        self.assertFalse([c for c in self.controller.log if c.startswith(("MovJ", "MovL"))])

    def test_estop_before_the_grip_stops_normally_and_does_not_pulse_do10(self):
        done = {"x": False}

        def hook(cmd):
            if cmd.startswith("MovL") and not done["x"]:
                done["x"] = True
                self.server.estop("test")
        self.controller.hook = hook
        self.run_order(order("4444", 1))
        self.assertEqual(self.client.statuses("4444")[-1][0], "error")
        self.assertIn("ResetRobot()", self.controller.log)
        self.assertNotIn((10, 1), self.controller.do_history)
        self.assertEqual((self.controller.do[9], self.controller.do[10]), (0, 0))
        self.run_order(order("4445", 2))                              # rejected while stopped
        self.assertIn("E-STOP active", self.client.statuses("4445")[0][1])

    def test_estop_while_the_gem_is_held_does_not_pulse_do10_and_warns(self):
        moves = {"l": 0}

        def hook(cmd):
            if cmd.startswith("MovL"):
                moves["l"] += 1
                if moves["l"] == 2:                                   # the lift right after the vacuum pulse
                    self.server.estop("test")
        self.controller.hook = hook
        self.run_order(order("5555", 1))
        self.assertEqual(self.controller.do_history[:3], [(10, 0), (9, 1), (9, 0)])
        self.assertNotIn((10, 1), self.controller.do_history)
        self.assertIn("gem may STILL BE HELD", self.text)
        self.assertEqual((self.controller.do[9], self.controller.do[10]), (0, 0))

    def test_estop_reset_sends_clear_error(self):
        self.send({"type": "estop"})
        self.send({"type": "estop_reset"})
        self.assertIn("ClearError()", self.controller.log)


class SocketTests(unittest.TestCase):
    def test_end_to_end_over_tcp_in_dry_run(self):
        cfg = gp.load_config(CONFIG)
        lines = []
        server = rs.Server(cfg, None, dry_delay=0.0, out=lines.append)
        sock = server.listen("127.0.0.1", 0)
        port = sock.getsockname()[1]
        server.start_worker()
        threading.Thread(target=server.accept_loop, args=(sock,), daemon=True).start()
        try:
            conn = socket.create_connection(("127.0.0.1", port), timeout=5)
            f = conn.makefile("rwb")

            def send(obj):
                f.write((json.dumps(obj) + "\n").encode())
                f.flush()

            def recv():
                return json.loads(f.readline().decode())
            send({"type": "ping"})
            self.assertEqual(recv(), {"type": "pong"})
            send(order("1234", 4, 9))
            self.assertEqual(recv(), {"type": "ack", "orderId": "1234"})
            seen = []
            while True:
                msg = recv()
                seen.append(msg["status"])
                if msg["status"] in ("done", "error"):
                    break
            self.assertEqual(seen, ["picking", "packing", "picking", "packing", "done"])
            f.write(b"this is not json\n")
            f.flush()
            send({"type": "ping"})
            self.assertEqual(recv(), {"type": "pong"})                # a bad line does not kill the link
            f.close()
            conn.close()
        finally:
            server.stop()
            sock.close()


class PortInUseTests(Base):
    def test_second_listener_on_same_port_is_refused_and_names_the_pid(self):
        first = self.server.listen("127.0.0.1", 0)
        port = first.getsockname()[1]
        try:
            other = rs.Server(self.cfg, None, dry_delay=0.0, out=lambda m: None)
            for host in ("127.0.0.1", "0.0.0.0"):
                with self.assertRaises(OSError) as ctx:
                    other.listen(host, port)
                if os.name == "nt":
                    text = rs.listen_error_text(host, port, ctx.exception)
                    self.assertIn(f"PID {os.getpid()}", text)
                    self.assertIn("Stop-Process", text)
        finally:
            first.close()


if __name__ == "__main__":
    unittest.main()
