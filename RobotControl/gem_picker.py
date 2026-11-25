#!/usr/bin/env python3
"""gem_picker.py - standalone MG400 pick-and-place for the gem store (no bridge, no Unity). Standard library only.

    python gem_picker.py status                       connect, print pose (User 1 / Tool 1), mode, alarms
    python gem_picker.py clear                        ClearError() after an alarm / E-STOP, then print the state
    python gem_picker.py pick N [--yes] [--step]      full pick-and-drop sequence for gem N (1..9)
    python gem_picker.py goto pick N | safe | drop | above N | above drop
    python gem_picker.py grip on|off
    python gem_picker.py --dry-run pick 1             no connection: print every planned step and target

Safety: the whole plan is printed and confirmed first ("Run? [y/N]", skip with --yes, --step asks before every step),
speed is capped at 30 %, targets outside the z limits are refused, the arm must report a real pose, and Ctrl+C or any
error stops the arm and switches DO9 and DO10 OFF (it never pulses the release valve by itself). The suction valve latches:
a DO9 pulse = vacuum, a DO10 pulse = release. DO9 and DO10 are never ON together.
"""

from __future__ import annotations

import argparse
import dataclasses
import json
import math
import os
import sys
import time
from dataclasses import dataclass, field

import mg400
from mg400 import MG400, MG400Error, MODE_ERROR, MODE_NAMES

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG = os.path.join(HERE, "robot_config.json")
DEFAULT_LOG_DIR = os.path.join(HERE, "logs")
HARD_SPEED_CAP = mg400.HARD_SPEED_CAP
BUSY_MODES = {6, 7, 8, 10, 11}          # drag, running, recording, paused, jog: do not start a plan


# ---------------------------------------------------------------------------------------------------------------
# errors
# ---------------------------------------------------------------------------------------------------------------
class PickerError(Exception):
    pass


class ConfigError(PickerError):
    pass


class LimitError(PickerError):
    pass


class GripError(PickerError):
    pass


class PlanDeclined(PickerError):
    pass


# ---------------------------------------------------------------------------------------------------------------
# configuration
# ---------------------------------------------------------------------------------------------------------------
@dataclass(frozen=True)
class Config:
    robot_ip: str
    dashboard_port: int
    motion_port: int
    user_frame: int
    tool_frame: int
    inline_frame_args: bool
    speed_percent: int
    acc_percent: int
    safe_z: float
    picks: dict
    drop: tuple
    safe_pose: tuple
    vacuum_do: int
    blow_do: int
    vacuum_pulse_s: float
    release_pulse_s: float
    z_min: float
    z_max: float
    arrive_tol_mm: float
    arrive_tol_deg: float
    move_timeout_s: float
    pick_approach_z: float = None   # z of 'above pick N' (None = safe_z); drop and travel keep safe_z


def _pose(value, name: str) -> tuple:
    try:
        pose = tuple(float(v) for v in value)
    except (TypeError, ValueError):
        raise ConfigError(f"{name}: expected [x, y, z, r], got {value!r}") from None
    if len(pose) != 4 or not all(math.isfinite(v) for v in pose):
        raise ConfigError(f"{name}: expected 4 finite numbers [x, y, z, r], got {value!r}")
    return pose


def validate_config(cfg: Config) -> Config:
    if not 1 <= cfg.speed_percent <= HARD_SPEED_CAP:
        raise ConfigError(f"speed_percent {cfg.speed_percent} refused (allowed 1..{HARD_SPEED_CAP})")
    if not 1 <= cfg.acc_percent <= 100:
        raise ConfigError(f"acc_percent {cfg.acc_percent} must be 1..100")
    if cfg.z_min >= cfg.z_max:
        raise ConfigError(f"limits: z_min {cfg.z_min} must be below z_max {cfg.z_max}")
    if cfg.vacuum_do == cfg.blow_do:
        raise ConfigError("vacuum_do and blow_do must be different outputs")
    if not 0.1 <= cfg.vacuum_pulse_s <= 10.0:
        raise ConfigError("vacuum_pulse_s must be 0.1..10 s")
    if not 0.1 <= cfg.release_pulse_s <= 5.0:
        raise ConfigError("release_pulse_s must be 0.1..5 s")
    if not cfg.z_min <= cfg.safe_z <= cfg.z_max:
        raise ConfigError(f"safe_z {cfg.safe_z} is outside the z limits {cfg.z_min}..{cfg.z_max}")
    if cfg.pick_approach_z is not None and not cfg.z_min <= cfg.pick_approach_z <= cfg.z_max:
        raise ConfigError(f"pick_approach_z {cfg.pick_approach_z} is outside the z limits {cfg.z_min}..{cfg.z_max}")
    if not cfg.picks:
        raise ConfigError("no pick_poses configured")
    for name, pose in list(cfg.picks.items()) + [("drop", cfg.drop), ("safe_pose", cfg.safe_pose)]:
        if not cfg.z_min <= pose[2] <= cfg.z_max:
            raise ConfigError(f"pose {name} z={pose[2]} is outside the z limits {cfg.z_min}..{cfg.z_max}")
    return cfg


def load_config(path: str = DEFAULT_CONFIG) -> Config:
    try:
        with open(path, encoding="utf-8") as handle:
            raw = json.load(handle)
    except OSError as exc:
        raise ConfigError(f"cannot read config {path}: {exc}") from exc
    except json.JSONDecodeError as exc:
        raise ConfigError(f"config {path} is not valid JSON: {exc}") from exc
    try:
        picks = {int(k): _pose(v, f"pick_poses[{k}]") for k, v in raw["pick_poses"].items()}
        limits = raw["limits"]
        cfg = Config(
            robot_ip=str(raw["robot_ip"]),
            dashboard_port=int(raw.get("dashboard_port", 29999)),
            motion_port=int(raw.get("motion_port", 30003)),
            user_frame=int(raw["user_frame"]),
            tool_frame=int(raw["tool_frame"]),
            inline_frame_args=bool(raw.get("inline_frame_args", False)),
            speed_percent=int(raw["speed_percent"]),
            acc_percent=int(raw["acc_percent"]),
            safe_z=float(raw["safe_z"]),
            picks=picks,
            drop=_pose(raw["drop_pose"], "drop_pose"),
            safe_pose=_pose(raw["safe_pose"], "safe_pose"),
            vacuum_do=int(raw["vacuum_do"]),
            blow_do=int(raw["blow_do"]),
            vacuum_pulse_s=float(raw["vacuum_pulse_s"]),
            release_pulse_s=float(raw["release_pulse_s"]),
            z_min=float(limits["z_min"]),
            z_max=float(limits["z_max"]),
            arrive_tol_mm=float(raw.get("arrive_tol_mm", 1.0)),
            arrive_tol_deg=float(raw.get("arrive_tol_deg", 1.0)),
            move_timeout_s=float(raw.get("move_timeout_s", 60)),
            pick_approach_z=float(raw.get("pick_approach_z", raw["safe_z"])),
        )
    except KeyError as exc:
        raise ConfigError(f"config {path}: missing key {exc}") from exc
    except (TypeError, ValueError, AttributeError) as exc:
        raise ConfigError(f"config {path}: bad value ({exc})") from exc
    return validate_config(cfg)


def check_target(cfg: Config, pose, what: str = "target") -> None:
    """Refuse a target outside the z limits (or not a finite number)."""
    if len(pose) != 4 or not all(math.isfinite(v) for v in pose):
        raise LimitError(f"{what}: {pose} is not a finite (x, y, z, r)")
    if not cfg.z_min <= pose[2] <= cfg.z_max:
        raise LimitError(f"{what}: z={pose[2]:.2f} is outside the allowed range {cfg.z_min:.2f}..{cfg.z_max:.2f}")


def fmt(pose) -> str:
    return "(" + ", ".join(f"{v:.2f}" for v in pose) + ")"


# ---------------------------------------------------------------------------------------------------------------
# planner (pure functions, no robot)
# ---------------------------------------------------------------------------------------------------------------
@dataclass
class Step:
    kind: str                    # setup | movl | movj | grip_on | grip_off
    label: str
    target: tuple | None = None
    item: int = 0                # 1-based gem number inside an order (0 = not part of an order)
    phase: str = ""              # "picking" (to the gem, gripping, lifting) | "packing" (carry, drop, release)


@dataclass
class Plan:
    title: str
    steps: list = field(default_factory=list)
    start: tuple | None = None
    notes: list = field(default_factory=list)


def _setup_step(cfg: Config) -> Step:
    return Step("setup", f"enable, User {cfg.user_frame} / Tool {cfg.tool_frame}, speed {cfg.speed_percent} %, "
                         f"acc {cfg.acc_percent} %")


def _lift(cfg: Config, cur: tuple, plan: Plan) -> list:
    if cur[2] >= cfg.safe_z - 0.5:
        plan.notes.append(f"current z {cur[2]:.2f} is already at/above safe z {cfg.safe_z:.2f}: no lift step")
        return []
    return [Step("movl", "MovL up to safe z at current XY", (cur[0], cur[1], cfg.safe_z, cur[3]))]


def _above(cfg: Config, pose: tuple) -> tuple:
    return (pose[0], pose[1], cfg.safe_z, pose[3])


def _above_pick(cfg: Config, pose: tuple) -> tuple:
    z = cfg.safe_z if cfg.pick_approach_z is None else cfg.pick_approach_z
    return (pose[0], pose[1], z, pose[3])


def _validate_plan(cfg: Config, plan: Plan) -> Plan:
    for index, step in enumerate(plan.steps, 1):
        if step.target is not None:
            check_target(cfg, step.target, f"step {index} ({step.label})")
    return plan


def _grip_on_step(cfg: Config) -> Step:
    return Step("grip_on", f"grip on (DO{cfg.blow_do} OFF, DO{cfg.vacuum_do} ON for {cfg.vacuum_pulse_s:g} s, "
                           f"DO{cfg.vacuum_do} OFF; the valve latches vacuum)")


def _grip_off_step(cfg: Config) -> Step:
    return Step("grip_off", f"release (DO{cfg.vacuum_do} OFF, DO{cfg.blow_do} ON for {cfg.release_pulse_s:g} s, "
                            f"DO{cfg.blow_do} OFF; the valve latches release)")


def _tag(steps: list, item: int, phase: str) -> list:
    for step in steps:
        step.item, step.phase = item, phase
    return steps


def pick_and_drop_steps(cfg: Config, n: int, cur: tuple, plan: Plan, item: int = 1, first: bool = True) -> list:
    """THE pick sequence, shared by 'gem_picker.py pick N' and robot_server.py orders:
    [setup] [lift] MovJ above pick, MovL down, grip on, MovL up, MovJ safe pose  (phase "picking")
    MovJ above drop, MovL down, release, MovL up, MovJ safe pose                 (phase "packing")."""
    if n not in cfg.picks:
        raise PickerError(f"no pick pose {n} (configured: {sorted(cfg.picks)})")
    pick, drop = cfg.picks[n], cfg.drop
    picking = []
    if first:
        picking.append(_setup_step(cfg))
    picking += _lift(cfg, cur, plan)
    picking += [
        Step("movj", f"MovJ to above pick {n}", _above_pick(cfg, pick)),
        Step("movl", "MovL down to pick z", pick),
        _grip_on_step(cfg),
        Step("movl", "MovL up from pick", _above_pick(cfg, pick)),
    ]
    packing = [
        Step("movj", "MovJ to safe pose", cfg.safe_pose),
        Step("movj", "MovJ to above drop", _above(cfg, drop)),
        Step("movl", "MovL down to drop z", drop),
        _grip_off_step(cfg),
        Step("movl", "MovL up to safe z", _above(cfg, drop)),
        Step("movj", "MovJ to safe pose", cfg.safe_pose),
    ]
    return _tag(picking, item, "picking") + _tag(packing, item, "packing")


def build_pick_plan(cfg: Config, n: int, cur: tuple) -> Plan:
    plan = Plan(f"pick gem {n} and drop it", start=cur)
    plan.steps += pick_and_drop_steps(cfg, n, cur, plan)
    return _validate_plan(cfg, plan)


def build_order_plan(cfg: Config, picks: list, cur: tuple) -> Plan:
    """Several gems in one run: setup once, then the same pick sequence per gem (each starts at the safe pose)."""
    if not picks:
        raise PickerError("an order needs at least one pick")
    plan = Plan("order: pick " + ", ".join(str(n) for n in picks), start=cur)
    position = cur
    for index, n in enumerate(picks, 1):
        plan.steps += pick_and_drop_steps(cfg, n, position, plan, item=index, first=(index == 1))
        position = cfg.safe_pose
    return _validate_plan(cfg, plan)


def parse_goto(cfg: Config, tokens: list) -> tuple:
    """['pick','3'] -> ('pick', 3); ['safe'] -> ('safe', None); ['above','drop'] -> ('above', 'drop')."""
    words = [t.lower() for t in tokens]
    usage = "goto pick N | safe | drop | above N | above drop | approach N --dz D"
    if words == ["safe"]:
        return "safe", None
    if words == ["drop"]:
        return "drop", None
    if len(words) == 2 and words[0] in ("pick", "above", "approach"):
        if words[0] == "above" and words[1] == "drop":
            return "above", "drop"
        try:
            n = int(words[1])
        except ValueError:
            raise PickerError(f"bad goto target {tokens!r}; use: {usage}") from None
        if n not in cfg.picks:
            raise PickerError(f"no pick pose {n} (configured: {sorted(cfg.picks)})")
        return words[0], n
    raise PickerError(f"bad goto target {tokens!r}; use: {usage}")


APPROACH_XY_TOL_MM = 2.0
APPROACH_MIN_DZ_MM = 5.0


def build_approach_plan(cfg: Config, n: int, dz, cur: tuple) -> Plan:
    """MovL straight to (pick N x, y, pick z + dz, r). Only if the current XY is already over pick N."""
    if dz is None:
        raise PickerError("goto approach N needs --dz D (mm above the pick z, at least 5)")
    if not math.isfinite(dz) or dz < APPROACH_MIN_DZ_MM:
        raise PickerError(f"--dz {dz} refused: D must be at least {APPROACH_MIN_DZ_MM:g} mm above the pick z")
    pick = cfg.picks[n]
    off = math.hypot(cur[0] - pick[0], cur[1] - pick[1])
    if off > APPROACH_XY_TOL_MM:
        raise PickerError(f"refused: the arm is {off:.1f} mm away from pick {n} in XY (limit {APPROACH_XY_TOL_MM:g} mm). "
                          f"Run 'goto above {n}' first")
    plan = Plan(f"goto approach {n} --dz {dz:g}", start=cur)
    plan.steps.append(_setup_step(cfg))
    plan.steps.append(Step("movl", f"MovL straight to pick {n} z + {dz:g} mm", (pick[0], pick[1], pick[2] + dz, pick[3])))
    return _validate_plan(cfg, plan)


def build_goto_plan(cfg: Config, tokens: list, cur: tuple, dz=None) -> Plan:
    kind, which = parse_goto(cfg, tokens)
    if kind == "approach":
        return build_approach_plan(cfg, which, dz, cur)
    if dz is not None:
        raise PickerError("--dz is only used with 'goto approach N'")
    plan = Plan("goto " + " ".join(tokens), start=cur)
    plan.steps.append(_setup_step(cfg))
    plan.steps += _lift(cfg, cur, plan)
    if kind == "safe":
        plan.steps.append(Step("movj", "MovJ to safe pose", cfg.safe_pose))
    else:
        pose = cfg.drop if (kind == "drop" or which == "drop") else cfg.picks[which]
        name = "drop" if pose is cfg.drop else f"pick {which}"
        above = _above(cfg, pose) if pose is cfg.drop else _above_pick(cfg, pose)
        plan.steps.append(Step("movj", f"MovJ to above {name}", above))
        if kind in ("pick", "drop"):
            plan.steps.append(Step("movl", f"MovL down to {name} z", pose))
    return _validate_plan(cfg, plan)


def build_grip_plan(cfg: Config, state: str) -> Plan:
    plan = Plan(f"grip {state}")
    plan.steps.append(_grip_on_step(cfg) if state == "on" else _grip_off_step(cfg))
    return plan


def format_plan(cfg: Config, plan: Plan) -> list:
    lines = [f"PLAN: {plan.title}",
             f"  robot {cfg.robot_ip}, User {cfg.user_frame} / Tool {cfg.tool_frame}, speed {cfg.speed_percent} % "
             f"(cap {HARD_SPEED_CAP} %), z limits {cfg.z_min:.2f}..{cfg.z_max:.2f}, safe z {cfg.safe_z:.2f}"]
    if plan.start:
        lines.append(f"  current pose {fmt(plan.start)}")
    for note in plan.notes:
        lines.append(f"  note: {note}")
    total = len(plan.steps)
    for index, step in enumerate(plan.steps, 1):
        target = f"  -> {fmt(step.target)}" if step.target else ""
        lines.append(f"  [{index:>2}/{total}] {step.label}{target}")
    return lines


# ---------------------------------------------------------------------------------------------------------------
# grip logic
# ---------------------------------------------------------------------------------------------------------------
class Gripper:
    """Latching suction valve: a vacuum-DO pulse switches to vacuum, a blow-DO pulse switches to release.
    The two outputs are never ON together, and both are OFF after every call (also after an interrupt)."""

    def __init__(self, robot, cfg: Config, sleep=time.sleep):
        self.robot, self.cfg, self.sleep = robot, cfg, sleep

    def _set(self, index: int, on: bool) -> None:
        if on:
            other = self.cfg.blow_do if index == self.cfg.vacuum_do else self.cfg.vacuum_do
            if self.robot.do_state.get(other) != 0:
                raise GripError(f"refused: DO{other} is not confirmed OFF, never DO{index} ON together with it")
        self.robot.set_do(index, on)

    def on(self) -> None:
        self._set(self.cfg.blow_do, False)
        self.robot.gem_possibly_held = True
        try:
            self._set(self.cfg.vacuum_do, True)
            self.sleep(self.cfg.vacuum_pulse_s)
        finally:
            self._set(self.cfg.vacuum_do, False)

    def off(self) -> None:
        self._set(self.cfg.vacuum_do, False)
        try:
            self._set(self.cfg.blow_do, True)
            self.sleep(self.cfg.release_pulse_s)
        finally:
            self._set(self.cfg.blow_do, False)
        self.robot.gem_possibly_held = False


# ---------------------------------------------------------------------------------------------------------------
# execution
# ---------------------------------------------------------------------------------------------------------------
def _print(text: str = "", end: str = "\n") -> None:
    print(text, end=end, flush=True)


def preflight(robot, cfg: Config, out=_print) -> tuple:
    """No motion: refuse on alarms / busy robot, select User/Tool, read the pose (must be a real pose)."""
    errors = robot.error_ids()
    mode = robot.robot_mode()
    if errors or mode == MODE_ERROR:
        raise PickerError(f"robot reports an alarm (RobotMode {mode}, error ids {errors}). Fix the cause, then run "
                          f"'python gem_picker.py clear'")
    if mode in BUSY_MODES:
        raise PickerError(f"robot is busy (RobotMode {mode}: {MODE_NAMES.get(mode, '?')}); stop it first")
    robot.select_frames(cfg.user_frame, cfg.tool_frame)
    return robot.get_pose()


def _do_setup(robot, cfg: Config, out) -> None:
    try:
        robot.enable()
    except mg400.RobotError as exc:
        if robot.robot_mode() != mg400.MODE_IDLE:
            raise
        out(f"(EnableRobot refused [{exc.code}] but RobotMode is already 5: continuing) ", end="")
    robot.wait_enabled()          # RobotMode() must be 5 (enabled, idle) before any move is queued
    robot.select_frames(cfg.user_frame, cfg.tool_frame)
    robot.set_speed(cfg.speed_percent)
    if not robot.set_acc(cfg.acc_percent):
        out("(acceleration not accepted by the firmware, using its default) ", end="")


def execute(robot, cfg: Config, plan: Plan, step_mode: bool = False, input_fn=input, out=_print,
            sleep=time.sleep, on_step=None) -> None:
    """Run a plan step by step. on_step(index, total, step) is called before each step (it may raise to stop)."""
    gripper = Gripper(robot, cfg, sleep)
    total = len(plan.steps)
    for index, step in enumerate(plan.steps, 1):
        if on_step is not None:
            on_step(index, total, step)
        if step_mode:
            target = f" -> {fmt(step.target)}" if step.target else ""
            answer = input_fn(f"  step {index}/{total}: {step.label}{target}  Do it? [y/N] ")
            if answer.strip().lower() not in ("y", "yes"):
                raise PlanDeclined(f"stopped before step {index}/{total}")
        out(f"[{index}/{total}] {step.label} ... ", end="")
        try:
            if step.kind == "setup":
                _do_setup(robot, cfg, out)
            elif step.kind in ("movl", "movj"):
                check_target(cfg, step.target, f"step {index}")
                (robot.mov_l if step.kind == "movl" else robot.mov_j)(*step.target)
                robot.wait_until_arrived(step.target, cfg.arrive_tol_mm, cfg.arrive_tol_deg, cfg.move_timeout_s)
            elif step.kind == "grip_on":
                gripper.on()
            elif step.kind == "grip_off":
                gripper.off()
            else:
                raise PickerError(f"unknown step kind {step.kind!r}")
        except BaseException:
            out("FAILED")
            raise
        out("OK")


def abort_safely(robot, cfg: Config, out, reason: str) -> None:
    """Stop motion (only if we ever sent a move), then DO9 and DO10 OFF. Never raises. The release valve is NOT pulsed:
    that would drop the gem wherever the arm is."""
    out(f"!! {reason}")
    latched = False
    if robot.motion_sent:
        text, latched = robot.stop_motion()
        out(f"!! {text}")
    failures = robot.outputs_off([cfg.vacuum_do, cfg.blow_do])
    for failure in failures:
        out(f"!! {failure}")
    if not failures:
        out(f"!! DO{cfg.vacuum_do} and DO{cfg.blow_do} are OFF")
    if robot.gem_possibly_held:
        out("!! WARNING: the gem may STILL BE HELD. The valve latches, so switching DO9/DO10 OFF does not release it, and "
            "DO10 was NOT pulsed (that would drop it wherever the arm is). To release it: move the arm over a safe "
            "place and run 'python gem_picker.py grip off'.")
    if latched:
        out("!! EmergencyStop() was used: the robot is latched in an alarm. Run 'python gem_picker.py clear' before "
            "the next run. If the arm did not stop, use the physical E-STOP.")


def confirm(plan_lines: list, input_fn, out, yes: bool) -> None:
    for line in plan_lines:
        out(line)
    if yes:
        out("--yes given: running without the Run? question")
        return
    if input_fn("Run? [y/N] ").strip().lower() not in ("y", "yes"):
        raise PlanDeclined("not confirmed")


# ---------------------------------------------------------------------------------------------------------------
# commands
# ---------------------------------------------------------------------------------------------------------------
def cmd_status(robot, cfg: Config, out) -> None:
    out(f"robot {cfg.robot_ip}: connected (dashboard {cfg.dashboard_port}, motion {cfg.motion_port})")
    errors = robot.error_ids()
    mode = robot.robot_mode()
    out(f"mode   : {mode} = {MODE_NAMES.get(mode, 'unknown')}")
    out(f"alarms : {errors if errors else 'none'}")
    try:
        robot.select_frames(cfg.user_frame, cfg.tool_frame)
        out(f"frames : User {cfg.user_frame} / Tool {cfg.tool_frame} selected")
    except MG400Error as exc:
        out(f"frames : could NOT select User {cfg.user_frame} / Tool {cfg.tool_frame}: {exc}")
    pose = robot.get_pose()
    out(f"pose   : {fmt(pose)}   (x, y, z mm; r deg)")
    try:
        out(f"joints : {fmt(robot.get_joints())}")
    except MG400Error as exc:
        out(f"joints : unavailable ({exc})")
    out("Compare the pose with DobotStudio (User 1 / Tool 1). Differences above 1 mm: do NOT move the arm.")


def cmd_clear(robot, cfg: Config, out) -> None:
    robot.clear_error()
    mode = robot.robot_mode()
    errors = robot.error_ids()
    out(f"ClearError() ok. mode {mode} = {MODE_NAMES.get(mode, 'unknown')}, alarms: {errors if errors else 'none'}")
    if errors or mode == MODE_ERROR:
        out("alarm still active: check the robot / DobotStudio (physical E-STOP released?)")


def parse_assume_pose(text: str | None, cfg: Config) -> tuple:
    if text:
        try:
            values = tuple(float(v) for v in text.split(","))
        except ValueError:
            raise PickerError(f"--assume-pose expects x,y,z,r numbers, got {text!r}") from None
        if len(values) != 4:
            raise PickerError("--assume-pose expects exactly 4 numbers x,y,z,r")
        return values
    # default for the dry run: parked over the safe-pose XY, low, so that the lift step is shown
    return (cfg.safe_pose[0], cfg.safe_pose[1], 0.0, cfg.safe_pose[3])


# ---------------------------------------------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------------------------------------------
def build_parser() -> argparse.ArgumentParser:
    def add_common(parser, suppress):
        default = argparse.SUPPRESS if suppress else None
        parser.add_argument("--config", default=default or DEFAULT_CONFIG, help="robot_config.json to use")
        parser.add_argument("--dry-run", action="store_true", default=argparse.SUPPRESS if suppress else False,
                            help="no connection: print every planned step and its target")
        parser.add_argument("--yes", action="store_true", default=argparse.SUPPRESS if suppress else False,
                            help="skip the 'Run? [y/N]' question")
        parser.add_argument("--step", action="store_true", default=argparse.SUPPRESS if suppress else False,
                            help="ask before every single step")
        parser.add_argument("--speed", type=int, default=default, help=f"speed percent (max {HARD_SPEED_CAP})")
        parser.add_argument("--assume-pose", default=default, metavar="X,Y,Z,R",
                            help="dry run only: pretend the robot is at this pose")
        parser.add_argument("--log-dir", default=default or DEFAULT_LOG_DIR, help="where robot_<time>.log is written")
        parser.add_argument("-v", "--verbose", action="store_true", default=argparse.SUPPRESS if suppress else False,
                            help="echo every request/reply on the console")

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_common(parser, suppress=False)
    sub = parser.add_subparsers(dest="command", required=True)
    for name, text in (("status", "connect and print pose / mode / alarms"), ("clear", "ClearError() and print state")):
        add_common(sub.add_parser(name, help=text), suppress=True)
    pick = sub.add_parser("pick", help="pick gem N and drop it")
    add_common(pick, suppress=True)
    pick.add_argument("n", type=int, help="gem number 1..9")
    goto = sub.add_parser("goto", help="pick N | safe | drop | above N | above drop")
    add_common(goto, suppress=True)
    goto.add_argument("target", nargs="+")
    goto.add_argument("--dz", type=float, default=None, metavar="D",
                      help="approach only: mm above the pick z (at least 5)")
    grip = sub.add_parser("grip", help="suction on/off")
    add_common(grip, suppress=True)
    grip.add_argument("state", choices=("on", "off"))
    return parser


def run(argv=None, link_factory=None, input_fn=input, out=_print, sleep=time.sleep) -> int:
    args = build_parser().parse_args(argv)
    try:
        cfg = load_config(args.config)
        if args.speed is not None:
            cfg = validate_config(dataclasses.replace(cfg, speed_percent=args.speed))
        if args.command == "pick" and args.n not in cfg.picks:
            raise PickerError(f"no pick pose {args.n} (configured: {sorted(cfg.picks)})")
    except PickerError as exc:
        out(f"ERROR: {exc}")
        return 1

    if args.dry_run:
        return _dry_run(args, cfg, out)

    robot = MG400(cfg.robot_ip, cfg.dashboard_port, cfg.motion_port, link_factory, cfg.inline_frame_args)
    log_path = mg400.setup_logging(args.log_dir, args.verbose)
    out(f"log file: {log_path}")
    try:
        robot.connect()
    except MG400Error as exc:
        out(f"ERROR: {exc}")
        out("Check: robot on, cable, PC Ethernet 192.168.1.x, DobotStudio closed (it holds the connection).")
        return 1
    try:
        _dispatch(args, cfg, robot, input_fn, out, sleep)
        return 0
    except PlanDeclined as exc:
        out(f"cancelled: {exc}. Nothing further was sent; the suction output was left as it is.")
        return 2
    except KeyboardInterrupt:
        out("")
        abort_safely(robot, cfg, out, "Ctrl+C")
        return 130
    except (MG400Error, PickerError) as exc:
        out(f"ERROR: {exc}")
        abort_safely(robot, cfg, out, "error: stopping")
        return 1
    except Exception as exc:     # noqa: BLE001 - any exception must stop the arm
        out(f"UNEXPECTED ERROR: {type(exc).__name__}: {exc}")
        abort_safely(robot, cfg, out, "unexpected error: stopping")
        return 1
    finally:
        robot.close()
        mg400.close_logging()


def _dispatch(args, cfg: Config, robot, input_fn, out, sleep) -> None:
    if args.command == "status":
        cmd_status(robot, cfg, out)
        return
    if args.command == "clear":
        cmd_clear(robot, cfg, out)
        return
    if args.command == "grip":
        plan = build_grip_plan(cfg, args.state)
    else:
        cur = preflight(robot, cfg, out)
        plan = build_pick_plan(cfg, args.n, cur) if args.command == "pick" else build_goto_plan(cfg, args.target, cur, args.dz)
    confirm(format_plan(cfg, plan), input_fn, out, args.yes)
    execute(robot, cfg, plan, args.step, input_fn, out, sleep)
    out("done.")


def _dry_run(args, cfg: Config, out) -> int:
    try:
        if args.command in ("status", "clear"):
            out(f"DRY RUN: '{args.command}' would connect to {cfg.robot_ip}:{cfg.dashboard_port} and print the state. "
                "Nothing sent.")
            return 0
        if args.command == "grip":
            plan = build_grip_plan(cfg, args.state)
        else:
            cur = parse_assume_pose(args.assume_pose, cfg)
            if args.command == "goto" and not args.assume_pose and args.target[0].lower() == "approach":
                kind, which = parse_goto(cfg, args.target)      # default: pretend we are already above pick N
                cur = _above_pick(cfg, cfg.picks[which])
            plan = (build_pick_plan(cfg, args.n, cur) if args.command == "pick"
                    else build_goto_plan(cfg, args.target, cur, args.dz))
            plan.notes.insert(0, "DRY RUN: the current pose is assumed (see --assume-pose), the robot is not contacted")
    except PickerError as exc:
        out(f"ERROR: {exc}")
        return 1
    for line in format_plan(cfg, plan):
        out(line)
    out("DRY RUN: nothing was sent.")
    return 0


def main() -> int:
    return run(sys.argv[1:])


if __name__ == "__main__":
    sys.exit(main())
