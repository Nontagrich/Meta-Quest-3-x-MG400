"""Unit tests for gem_picker / mg400 against a fake robot. Run:  python -m unittest discover -s tests -v"""

from __future__ import annotations

import dataclasses
import json
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
sys.path.insert(0, HERE)

import gem_picker as gp          # noqa: E402
import mg400                     # noqa: E402
from fake_robot import FakeController, factory   # noqa: E402

CONFIG = os.path.join(os.path.dirname(HERE), "robot_config.json")
LOG_DIR = tempfile.mkdtemp(prefix="robotcontrol_test_")


def cfg() -> gp.Config:
    return gp.load_config(CONFIG)


class Run:
    """Run the CLI against a fake robot; collect the output."""

    def __init__(self, argv, controller=None, answers=("y",)):
        self.controller = controller or FakeController()
        self.lines = []
        self.answers = list(answers)
        self.prompts = []
        argv = list(argv) + ["--log-dir", LOG_DIR]
        self.code = gp.run(argv, link_factory=factory(self.controller), input_fn=self._input, out=self._out,
                           sleep=lambda s: None)

    def _input(self, prompt):
        self.prompts.append(prompt)
        return self.answers.pop(0) if self.answers else "n"

    def _out(self, text="", end="\n"):
        self.lines.append(text + end)

    @property
    def text(self):
        return "".join(self.lines)


class ConfigTests(unittest.TestCase):
    def test_loads_all_values_from_the_brief(self):
        c = cfg()
        self.assertEqual(c.robot_ip, "192.168.1.6")
        self.assertEqual((c.user_frame, c.tool_frame), (1, 1))
        self.assertEqual((c.speed_percent, c.acc_percent, c.safe_z), (20, 10, 160.0))
        self.assertEqual(sorted(c.picks), list(range(1, 10)))
        self.assertEqual(c.picks[1], (-114.20, 263.20, -19.60, -16.04))
        self.assertEqual(c.picks[9], (-195.57, 340.97, -19.60, 2.32))
        self.assertEqual(c.drop, (5.36, 32.36, 90.58, -53.26))
        self.assertEqual((c.vacuum_do, c.blow_do, c.vacuum_pulse_s, c.release_pulse_s), (9, 10, 0.5, 0.5))
        self.assertFalse(hasattr(c, "grip_wait_s"))

    def test_limits_follow_the_rule(self):
        c = cfg()
        lowest = min([p[2] for p in c.picks.values()] + [c.drop[2]])
        self.assertAlmostEqual(c.z_min, lowest - 5, places=2)
        self.assertAlmostEqual(c.z_max, c.safe_z + 50, places=2)

    def test_default_safe_pose_is_centre_of_picks_at_safe_z_with_r_of_pick_5(self):
        c = cfg()
        xs = [p[0] for p in c.picks.values()]
        ys = [p[1] for p in c.picks.values()]
        self.assertAlmostEqual(c.safe_pose[0], sum(xs) / 9, delta=1.0)   # taught pose: only roughly the centre
        self.assertAlmostEqual(c.safe_pose[1], sum(ys) / 9, delta=1.0)
        self.assertEqual(c.safe_pose[2], c.safe_z)
        self.assertEqual(c.safe_pose[3], c.picks[5][3])

    def test_speed_above_cap_is_refused(self):
        with self.assertRaises(gp.ConfigError):
            gp.validate_config(dataclasses.replace(cfg(), speed_percent=31))

    def test_pose_outside_z_limits_is_refused(self):
        c = cfg()
        bad = dict(c.picks)
        bad[1] = (0.0, 0.0, c.z_min - 1, 0.0)
        with self.assertRaises(gp.ConfigError):
            gp.validate_config(dataclasses.replace(c, picks=bad))

    def test_bad_json_and_missing_key(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "c.json")
            with open(path, "w") as f:
                f.write("{not json")
            with self.assertRaises(gp.ConfigError):
                gp.load_config(path)
            with open(path, "w") as f:
                json.dump({"robot_ip": "1.2.3.4"}, f)
            with self.assertRaises(gp.ConfigError):
                gp.load_config(path)


class PlannerTests(unittest.TestCase):
    def test_pick_plan_has_the_12_steps_in_order(self):
        c = cfg()
        plan = gp.build_pick_plan(c, 3, (-153.80, 302.88, 0.0, -5.97))
        kinds = [s.kind for s in plan.steps]
        self.assertEqual(kinds, ["setup", "movl", "movj", "movl", "grip_on", "movl", "movj", "movj", "movl",
                                 "grip_off", "movl", "movj"])
        t = [s.target for s in plan.steps]
        self.assertEqual(t[1], (-153.80, 302.88, c.safe_z, -5.97))            # up at current XY
        self.assertEqual(c.pick_approach_z, 50.0)
        self.assertEqual(t[2], (c.picks[3][0], c.picks[3][1], c.pick_approach_z, c.picks[3][3]))
        self.assertEqual(t[3], c.picks[3])                                    # down to pick z
        self.assertEqual(t[5], (c.picks[3][0], c.picks[3][1], c.pick_approach_z, c.picks[3][3]))   # up after the grip
        self.assertEqual(t[6], c.safe_pose)
        self.assertEqual(t[7], (c.drop[0], c.drop[1], c.safe_z, c.drop[3]))
        self.assertEqual(t[8], c.drop)
        self.assertEqual(t[10], (c.drop[0], c.drop[1], c.safe_z, c.drop[3]))
        self.assertEqual(t[11], c.safe_pose)

    def test_lift_is_skipped_when_already_at_safe_z(self):
        c = cfg()
        plan = gp.build_pick_plan(c, 1, c.safe_pose)
        self.assertEqual(len(plan.steps), 11)
        self.assertEqual(plan.steps[1].kind, "movj")

    def test_every_target_is_inside_the_z_limits(self):
        c = cfg()
        for n in range(1, 10):
            for step in gp.build_pick_plan(c, n, (0.0, 0.0, 0.0, 0.0)).steps:
                if step.target:
                    self.assertTrue(c.z_min <= step.target[2] <= c.z_max)

    def test_target_below_z_min_is_refused(self):
        c = cfg()
        bad = dict(c.picks)
        bad[2] = (-113.32, 303.0, c.z_min - 0.01, -12.1)
        c2 = dataclasses.replace(c, picks=bad)
        with self.assertRaises(gp.LimitError):
            gp.build_pick_plan(c2, 2, c.safe_pose)
        with self.assertRaises(gp.LimitError):
            gp.check_target(c, (0, 0, c.z_max + 1, 0))

    def test_unknown_pick_number(self):
        with self.assertRaises(gp.PickerError):
            gp.build_pick_plan(cfg(), 10, cfg().safe_pose)

    def test_goto_variants(self):
        c = cfg()
        cur = (0.0, 0.0, 0.0, 0.0)
        self.assertEqual([s.kind for s in gp.build_goto_plan(c, ["safe"], cur).steps], ["setup", "movl", "movj"])
        self.assertEqual(gp.build_goto_plan(c, ["above", "4"], cur).steps[-1].target[2], c.pick_approach_z)
        down = gp.build_goto_plan(c, ["pick", "4"], cur).steps
        self.assertEqual(down[-1].target, c.picks[4])
        self.assertEqual(gp.build_goto_plan(c, ["drop"], cur).steps[-1].target, c.drop)
        self.assertEqual(gp.build_goto_plan(c, ["above", "drop"], cur).steps[-1].target[:2], c.drop[:2])
        for bad in (["pick"], ["above", "x"], ["above", "12"], ["fly"]):
            with self.assertRaises(gp.PickerError):
                gp.build_goto_plan(c, bad, cur)


class PlanTextTests(unittest.TestCase):
    def test_grip_step_texts_describe_the_latching_pulses(self):
        steps = gp.build_pick_plan(cfg(), 1, cfg().safe_pose).steps
        on = next(s for s in steps if s.kind == "grip_on").label
        off = next(s for s in steps if s.kind == "grip_off").label
        self.assertEqual(on, "grip on (DO10 OFF, DO9 ON for 0.5 s, DO9 OFF; the valve latches vacuum)")
        self.assertEqual(off, "release (DO9 OFF, DO10 ON for 0.5 s, DO10 OFF; the valve latches release)")

    def test_pulse_times_are_validated(self):
        for field in ("vacuum_pulse_s", "release_pulse_s"):
            for bad in (0.0, 99.0):
                with self.assertRaises(gp.ConfigError):
                    gp.validate_config(dataclasses.replace(cfg(), **{field: bad}))


class ApproachTests(unittest.TestCase):
    def over(self, n, z=160.0, dx=0.0):
        p = cfg().picks[n]
        return (p[0] + dx, p[1], z, p[3] + 10)

    def test_plan_is_one_movl_to_pick_z_plus_dz_with_pick_r(self):
        c = cfg()
        plan = gp.build_goto_plan(c, ["approach", "3"], self.over(3), dz=20)
        self.assertEqual([s.kind for s in plan.steps], ["setup", "movl"])
        p = c.picks[3]
        self.assertEqual(plan.steps[1].target, (p[0], p[1], p[2] + 20, p[3]))

    def test_works_from_below_too_and_within_2mm(self):
        gp.build_goto_plan(cfg(), ["approach", "3"], self.over(3, z=-70.0, dx=1.9), dz=5)

    def test_refused_when_not_over_the_pick(self):
        with self.assertRaises(gp.PickerError) as ctx:
            gp.build_goto_plan(cfg(), ["approach", "3"], self.over(3, dx=2.5), dz=20)
        self.assertIn("goto above 3", str(ctx.exception))

    def test_dz_must_be_at_least_5(self):
        for bad in (None, 4.99, 0, -10, float("nan")):
            with self.assertRaises(gp.PickerError):
                gp.build_goto_plan(cfg(), ["approach", "3"], self.over(3), dz=bad)

    def test_target_above_z_max_is_refused(self):
        c = cfg()
        with self.assertRaises(gp.LimitError):
            gp.build_goto_plan(c, ["approach", "3"], self.over(3), dz=c.z_max - c.picks[3][2] + 1)

    def test_dz_with_other_targets_is_refused(self):
        with self.assertRaises(gp.PickerError):
            gp.build_goto_plan(cfg(), ["above", "3"], self.over(3), dz=10)

    def test_cli_refuses_without_moving_when_not_above(self):
        r = Run(["goto", "approach", "1", "--dz", "20"], FakeController(pose=(0.0, 300.0, 160.0, 0.0)))
        self.assertEqual(r.code, 1)
        self.assertIn("goto above 1", r.text)
        names = r.controller.names()
        self.assertNotIn("MovL", names)
        self.assertNotIn("EnableRobot", names)
        self.assertNotIn("EmergencyStop", names)

    def test_cli_runs_with_confirmation(self):
        p = cfg().picks[2]
        r = Run(["goto", "approach", "2", "--dz", "15"], FakeController(pose=(p[0], p[1], 160.0, p[3])))
        self.assertEqual(r.code, 0, r.text)
        self.assertEqual(r.prompts, ["Run? [y/N] "])
        self.assertAlmostEqual(r.controller.pose[2], p[2] + 15, places=1)
        self.assertEqual(r.controller.names().count("MovL"), 1)

    def test_cli_declined_sends_nothing(self):
        p = cfg().picks[2]
        r = Run(["goto", "approach", "2", "--dz", "15"], FakeController(pose=(p[0], p[1], 160.0, p[3])), answers=("n",))
        self.assertEqual(r.code, 2)
        self.assertNotIn("MovL", r.controller.names())

    def test_dry_run_assumes_the_arm_is_above_the_pick(self):
        lines = []
        code = gp.run(["--dry-run", "goto", "approach", "4", "--dz", "10"], out=lambda t="", end="\n": lines.append(t))
        self.assertEqual(code, 0)
        self.assertIn("MovL straight to pick 4 z + 10 mm", "\n".join(lines))


class GripTests(unittest.TestCase):
    def _gripper(self):
        controller = FakeController()
        robot = mg400.MG400(link_factory=factory(controller))
        robot.connect()
        return controller, robot, gp.Gripper(robot, cfg(), sleep=lambda s: None)

    def test_grip_on_order_is_a_latching_vacuum_pulse(self):
        controller, robot, grip = self._gripper()
        waits = []
        grip.sleep = waits.append
        grip.on()
        self.assertEqual(controller.do_history, [(10, 0), (9, 1), (9, 0)])   # DO10 OFF, DO9 ON, wait, DO9 OFF
        self.assertEqual(waits, [0.5])                                       # vacuum_pulse_s, no extra grip wait
        self.assertTrue(robot.gem_possibly_held)

    def test_release_order_and_pulse(self):
        controller, robot, grip = self._gripper()
        waits = []
        grip.on()
        controller.do_history.clear()
        grip.sleep = waits.append
        grip.off()
        self.assertEqual(controller.do_history, [(9, 0), (10, 1), (10, 0)])  # DO9 OFF, DO10 ON, wait, DO10 OFF
        self.assertEqual(waits, [0.5])                                       # release_pulse_s
        self.assertFalse(robot.gem_possibly_held)

    def test_never_both_on(self):
        controller, _, grip = self._gripper()
        for _ in range(3):
            grip.on()
            grip.off()
        self.assertFalse(controller.both_on_ever())

    def test_refuses_vacuum_while_blow_is_not_confirmed_off(self):
        controller, robot, grip = self._gripper()
        robot.set_do(10, True)
        with self.assertRaises(gp.GripError):
            grip._set(9, True)
        self.assertNotIn((9, 1), controller.do_history)

    def test_outputs_are_off_even_if_a_pulse_is_interrupted(self):
        controller, robot, grip = self._gripper()

        def boom(seconds):
            raise KeyboardInterrupt
        grip.sleep = boom
        with self.assertRaises(KeyboardInterrupt):
            grip.on()
        self.assertEqual((controller.do[9], controller.do[10]), (0, 0))
        self.assertTrue(robot.gem_possibly_held)              # the valve is latched on vacuum: still holding
        with self.assertRaises(KeyboardInterrupt):
            grip.off()
        self.assertEqual((controller.do[9], controller.do[10]), (0, 0))
        self.assertFalse(controller.both_on_ever())


class DriverTests(unittest.TestCase):
    def test_parse_reply(self):
        self.assertEqual(mg400.parse_reply("0,{1.5,2,3,4},GetPose();"), (0, "1.5,2,3,4"))
        self.assertEqual(mg400.parse_reply("-1,{},User(1);"), (-1, ""))
        with self.assertRaises(ValueError):
            mg400.parse_reply("garbage")

    def test_error_reply_raises_with_the_error_id(self):
        controller = FakeController()
        controller.fail["EnableRobot"] = -7
        robot = mg400.MG400(link_factory=factory(controller))
        robot.connect()
        with self.assertRaises(mg400.RobotError) as ctx:
            robot.enable()
        self.assertEqual(ctx.exception.code, -7)

    def test_speed_cap(self):
        robot = mg400.MG400(link_factory=factory(FakeController()))
        robot.connect()
        for bad in (0, 31, 100):
            with self.assertRaises(mg400.MG400Error):
                robot.set_speed(bad)

    def test_all_zero_pose_is_unreadable(self):
        controller = FakeController()
        controller.zero_pose = True
        robot = mg400.MG400(link_factory=factory(controller))
        robot.connect()
        with self.assertRaises(mg400.MG400Error):
            robot.get_pose()

    def test_angle_wrap(self):
        self.assertAlmostEqual(mg400.angle_diff(179.5, -179.5), 1.0)

    def test_inline_frame_args(self):
        controller = FakeController()
        robot = mg400.MG400(link_factory=factory(controller), inline_frame_args=True)
        robot.connect()
        controller.mode = 5
        robot.select_frames(1, 1)
        robot.mov_l(1, 2, 3, 4)
        self.assertIn("MovL(1.00,2.00,3.00,4.00,User=1,Tool=1)", controller.log)


class RunTests(unittest.TestCase):
    def test_dry_run_pick_1_and_9_do_not_touch_the_robot(self):
        for n in (1, 9):
            lines = []
            code = gp.run(["--dry-run", "pick", str(n)], link_factory=lambda *a: self.fail("connected"),
                          out=lambda t="", end="\n": lines.append(t))
            self.assertEqual(code, 0)
            text = "\n".join(lines)
            self.assertIn(f"above pick {n}", text)
            self.assertIn("nothing was sent", text)

    def test_status(self):
        r = Run(["status"])
        self.assertEqual(r.code, 0)
        self.assertIn("pose", r.text)
        self.assertEqual(r.controller.frames, {"user": 1, "tool": 1})

    def test_pick_runs_all_steps_and_ends_at_safe_pose(self):
        c = cfg()
        r = Run(["pick", "5"], FakeController(pose=(0.0, 400.0, 20.0, 0.0)))
        self.assertEqual(r.code, 0, r.text)
        self.assertIn("[12/12] MovJ to safe pose ... OK", r.text)
        self.assertEqual(r.controller.speed, 20)
        self.assertEqual(r.controller.frames, {"user": 1, "tool": 1})
        for got, want in zip(r.controller.pose, c.safe_pose):
            self.assertAlmostEqual(got, want, places=1)
        self.assertFalse(r.controller.both_on_ever())
        self.assertEqual((r.controller.do[9], r.controller.do[10]), (0, 0))
        names = r.controller.names()
        self.assertEqual(names.count("MovL") + names.count("MovJ"), 9)   # 12 steps - setup - 2 grips
        self.assertNotIn("Sync", names)                        # arrival is watched by polling, never a blocking Sync
        self.assertNotIn("ResetRobot", names)
        self.assertEqual(names.count("EnableRobot"), 1)
        self.assertNotIn("EmergencyStop", names)
        self.assertEqual(r.prompts, ["Run? [y/N] "])

    def test_declining_sends_no_motion(self):
        r = Run(["pick", "1"], answers=("n",))
        self.assertEqual(r.code, 2)
        self.assertNotIn("MovL", r.controller.names())
        self.assertNotIn("MovJ", r.controller.names())
        self.assertNotIn("EnableRobot", r.controller.names())

    def test_yes_skips_the_question(self):
        r = Run(["pick", "1", "--yes"], answers=())
        self.assertEqual(r.code, 0, r.text)
        self.assertEqual(r.prompts, [])

    def test_step_mode_asks_before_every_step_and_stops_on_no(self):
        r = Run(["pick", "1", "--step"], answers=("y", "y", "y", "n"))
        self.assertEqual(r.code, 2)
        self.assertEqual(len(r.prompts), 4)
        self.assertIn("step 3/11", r.prompts[3])                # robot starts at the safe pose: no lift step
        self.assertEqual(r.controller.names().count("MovJ"), 1)  # step 2 ran, step 3 (MovL down) was declined
        self.assertEqual(r.controller.names().count("MovL"), 0)

    def test_speed_above_30_is_refused_before_connecting(self):
        r = Run(["pick", "1", "--speed", "31"])
        self.assertEqual(r.code, 1)
        self.assertEqual(r.controller.log, [])

    def test_unreadable_pose_refuses_to_run(self):
        controller = FakeController()
        controller.zero_pose = True
        r = Run(["pick", "1"], controller)
        self.assertEqual(r.code, 1)
        self.assertNotIn("MovL", controller.names())
        self.assertNotIn("EmergencyStop", controller.names())    # nothing was moving: no latching stop
        self.assertEqual((controller.do[9], controller.do[10]), (0, 0))

    def test_alarm_refuses_to_run_and_points_to_clear(self):
        controller = FakeController()
        controller.errors = [17]
        r = Run(["pick", "1"], controller)
        self.assertEqual(r.code, 1)
        self.assertIn("clear", r.text)
        self.assertNotIn("EnableRobot", controller.names())

    def test_error_during_a_move_stops_and_switches_outputs_off(self):
        controller = FakeController()
        controller.fail["MovJ"] = -3
        r = Run(["pick", "1"], controller)
        self.assertEqual(r.code, 1)
        self.assertIn("FAILED", r.text)
        names = controller.names()
        self.assertIn("ResetRobot", names)
        self.assertNotIn("EmergencyStop", names)               # normal stop first, no alarm latched
        self.assertEqual((controller.do[9], controller.do[10]), (0, 0))
        self.assertIn("-3", r.text)

    def test_ctrl_c_during_motion_does_a_normal_stop_first(self):
        controller = FakeController()
        controller.interrupt_on = "MovJ"
        r = Run(["pick", "1"], controller)
        self.assertEqual(r.code, 130)
        names = controller.names()
        self.assertIn("ResetRobot", names)
        self.assertNotIn("EmergencyStop", names)
        self.assertLess(names.index("ResetRobot"), names.index("DOExecute"))
        self.assertEqual((controller.do[9], controller.do[10]), (0, 0))
        self.assertFalse(controller.both_on_ever())
        self.assertNotIn("latched", r.text)
        self.assertNotIn("STILL BE HELD", r.text)             # interrupted before the vacuum pulse: nothing held

    def test_ctrl_c_escalates_to_emergency_stop_when_the_normal_stop_is_refused(self):
        controller = FakeController()
        controller.interrupt_on = "MovJ"
        controller.fail["ResetRobot"] = -1
        controller.fail["Pause"] = -1
        r = Run(["pick", "1"], controller)
        self.assertEqual(r.code, 130)
        self.assertIn("EmergencyStop", controller.names())
        self.assertIn("latched", r.text)
        self.assertEqual((controller.do[9], controller.do[10]), (0, 0))

    def test_ctrl_c_while_holding_the_gem_turns_the_suction_off(self):
        controller = FakeController()
        original = controller.handle
        seen = {"movl": 0}

        def handle(command):
            if command.startswith("MovL"):
                seen["movl"] += 1
                if seen["movl"] == 2:                  # the lift after grip on
                    raise KeyboardInterrupt
            return original(command)
        controller.handle = handle
        r = Run(["pick", "1"], controller)
        self.assertEqual(r.code, 130)
        self.assertEqual(controller.do[9], 0)
        self.assertEqual(controller.do[10], 0)
        self.assertEqual(controller.do_history[:3], [(10, 0), (9, 1), (9, 0)])   # the vacuum pulse completed
        self.assertNotIn((10, 1), controller.do_history)       # DO10 is NOT pulsed automatically: the gem is kept
        self.assertEqual(controller.do_history[-2:], [(9, 0), (10, 0)])         # abort: both OFF
        self.assertIn("gem may STILL BE HELD", r.text)
        self.assertIn("grip off", r.text)

    def _fast_clock(self):
        class Clock:
            def __enter__(inner):
                inner.sleep, inner.mono = mg400.time.sleep, mg400.time.monotonic
                ticks = iter(range(0, 100000))
                mg400.time.sleep = lambda s: None
                mg400.time.monotonic = lambda: next(ticks) * 0.1
                return inner

            def __exit__(inner, *a):
                mg400.time.sleep, mg400.time.monotonic = inner.sleep, inner.mono
        return Clock()

    def test_move_that_never_starts_stops_with_a_clear_message_instead_of_hanging(self):
        controller = FakeController()
        controller.ignore_moves = True                 # MovL/MovJ reply 0 but the arm does not move
        with self._fast_clock():
            r = Run(["goto", "above", "1"], controller)
        self.assertEqual(r.code, 1)
        self.assertIn("did NOT start moving", r.text)
        self.assertIn("RobotMode=5", r.text)
        names = controller.names()
        self.assertIn("ResetRobot", names)             # the queued command is cancelled
        self.assertNotIn("EmergencyStop", names)
        self.assertNotIn("Sync", names)

    def test_move_that_stops_half_way_is_an_arrival_error(self):
        controller = FakeController()
        controller.partial = True
        with self._fast_clock():
            r = Run(["goto", "above", "1"], controller)
        self.assertEqual(r.code, 1)
        self.assertIn("not at target", r.text)
        self.assertIn("ResetRobot", controller.names())

    def test_move_is_refused_when_robot_mode_is_not_5(self):
        controller = FakeController()
        robot = mg400.MG400(link_factory=factory(controller))
        robot.connect()
        controller.mode = 4
        with self.assertRaises(mg400.MG400Error) as ctx:
            robot.mov_l(0, 0, 0, 0)
        self.assertIn("RobotMode=4", str(ctx.exception))
        self.assertNotIn("MovL", controller.names())

    def test_enable_is_always_sent_and_waits_for_mode_5(self):
        controller = FakeController()
        controller.mode = 5                            # already enabled, like in the hanging runs
        r = Run(["goto", "above", "1"], controller)
        self.assertEqual(r.code, 0, r.text)
        names = controller.names()
        self.assertLess(names.index("EnableRobot"), names.index("MovJ"))

    def test_enable_refused_while_already_in_mode_5_continues(self):
        controller = FakeController()
        controller.mode = 5
        controller.fail["EnableRobot"] = -1
        r = Run(["goto", "above", "1"], controller)
        self.assertEqual(r.code, 0, r.text)
        self.assertIn("EnableRobot refused", r.text)

    def test_grip_commands(self):
        r = Run(["grip", "on"])
        self.assertEqual(r.code, 0, r.text)
        self.assertEqual(r.controller.do_history, [(10, 0), (9, 1), (9, 0)])
        r = Run(["grip", "off", "--yes"], answers=())
        self.assertEqual(r.controller.do_history, [(9, 0), (10, 1), (10, 0)])

    def test_clear(self):
        controller = FakeController()
        controller.errors = [5]
        r = Run(["clear"], controller)
        self.assertEqual(r.code, 0)
        self.assertIn("ClearError() ok", r.text)

    def test_goto_pick_from_safe_pose(self):
        r = Run(["goto", "pick", "2"])
        self.assertEqual(r.code, 0, r.text)
        self.assertAlmostEqual(r.controller.pose[2], -19.60, places=1)

    def test_nothing_is_sent_outside_limits(self):
        c = cfg()
        controller = FakeController(pose=(0.0, 300.0, c.z_max + 30, 0.0))
        r = Run(["goto", "above", "1"], controller)
        # robot is above z_max: no lift, targets are inside, so the plan still runs and ends inside the limits
        self.assertEqual(r.code, 0, r.text)


if __name__ == "__main__":
    unittest.main()
