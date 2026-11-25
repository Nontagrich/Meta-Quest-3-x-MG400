"""A fake MG400 controller speaking the same ASCII protocol, for tests without the robot."""

from __future__ import annotations

import re

import mg400


class FakeController:
    def __init__(self, pose=(-153.80, 302.88, 160.0, -5.97)):
        self.pose = list(pose)
        self.mode = 4                    # disabled
        self.errors: list[int] = []
        self.do = {}                     # index -> 0|1
        self.do_history = []             # [(index, value)]
        self.log: list[str] = []         # every command received, in order
        self.frames = {"user": None, "tool": None}
        self.speed = None
        self.estopped = False
        self.pending = None              # target of the queued motion
        self.fail = {}                   # command name -> reply code to return (e.g. {"MovL": -1})
        self.ignore_moves = False        # motion is accepted (reply 0) but the arm never starts moving
        self.partial = False             # the arm moves half way and stops
        self.start_mode = 5
        self.hook = None                 # called with every command before it is handled (tests: trigger an E-STOP)
        self.interrupt_on = None         # command name that raises KeyboardInterrupt (simulated Ctrl+C)
        self.zero_pose = False

    # -- helpers ------------------------------------------------------------------------------------------------
    def names(self):
        return [c.partition("(")[0] for c in self.log]

    def both_on_ever(self, a=9, b=10) -> bool:
        state = {a: 0, b: 0}
        for index, value in self.do_history:
            state[index] = value
            if state[a] and state[b]:
                return True
        return False

    def handle(self, command: str) -> str:
        self.log.append(command)
        if self.hook is not None:
            self.hook(command)
        name, _, rest = command.partition("(")
        args = rest.rsplit(")", 1)[0]
        if self.interrupt_on == name:
            raise KeyboardInterrupt
        if name in self.fail:
            return f"{self.fail[name]},{{}},{command};"
        if name != "EmergencyStop" and self.estopped and name not in ("ClearError", "DOExecute", "RobotMode", "GetErrorID"):
            return f"-1,{{}},{command};"
        if name == "ClearError":
            self.estopped = False
            self.errors = []
            self.mode = 4
            return f"0,{{}},{command};"
        if name == "EnableRobot":
            self.mode = 5
            return f"0,{{}},{command};"
        if name == "RobotMode":
            return f"0,{{{self.mode}}},{command};"
        if name == "GetErrorID":
            body = "[" + ",".join(f"[{e}]" for e in self.errors) + "]" if self.errors else "[[],[],[],[],[],[],[]]"
            return f"0,{{{body}}},{command};"
        if name in ("User", "Tool"):
            self.frames[name.lower()] = int(args)
            return f"0,{{}},{command};"
        if name == "SpeedFactor":
            self.speed = int(args)
            return f"0,{{}},{command};"
        if name in ("AccJ", "AccL"):
            return f"0,{{}},{command};"
        if name == "GetPose":
            pose = [0.0] * 4 if self.zero_pose else self.pose
            return "0,{%s},GetPose();" % ",".join(f"{v:.6f}" for v in pose)
        if name == "GetAngle":
            return "0,{10.0,20.0,30.0,40.0},GetAngle();"
        if name == "DOExecute":
            index, value = (int(v) for v in args.split(","))
            self.do[index] = value
            self.do_history.append((index, value))
            return f"0,{{}},{command};"
        if name == "EmergencyStop":
            self.estopped = True
            self.mode = 9
            self.pending = None
            return f"0,{{}},{command};"
        if name in ("MovJ", "MovL"):
            vals = [float(v) for v in re.findall(r"-?\d+\.?\d*", args.split(",User")[0])[:4]]
            if not self.ignore_moves:
                self.pose = [(a + b) / 2 for a, b in zip(self.pose, vals)] if self.partial else list(vals)
            return f"0,{{}},{command};"
        if name == "ResetRobot":
            self.pending = None
            return f"0,{{}},{command};"
        if name == "Pause":
            self.mode = 10
            return f"0,{{}},{command};"
        if name == "Sync":
            if self.pending is not None and not self.ignore_moves:
                self.pose = list(self.pending)
            self.pending = None
            return f"0,{{}},{command};"
        return f"-1,{{}},{command};"


class FakeLink:
    def __init__(self, controller: FakeController, name: str):
        self.controller, self.name = controller, name
        self.connected = False

    def connect(self):
        self.connected = True

    def close(self):
        self.connected = False

    def request(self, command, timeout=5.0):
        return self.controller.handle(command)


def factory(controller: FakeController):
    return lambda name, ip, port: FakeLink(controller, name)
