# RobotControl - standalone MG400 pick-and-place

Our own program: no teammate bridge, no Unity. Python 3, standard library only.
`mg400.py` is the driver, `gem_picker.py` is the CLI + planner, `robot_config.json` holds every number.

## Protocol chosen: "cr" profile (dashboard 29999 + motion 30003)
Learned from `External/MG400_Unity_Control/Python_Automation/mg400_driver.py` and `bridge_server.py`
(their files are not imported; the code here is new):

| Item | Value | Why |
|------|-------|-----|
| Dashboard | TCP **29999**, plain ASCII, no checksum | The bridge connected to this robot with profile `cr` and read a real pose; the other profile (29900 + CRC16, "mg400") was not what answered. |
| Motion | TCP **30003**, `MovJ(x,y,z,r)` / `MovL(...)` / `Sync()` | The bridge sends motion commands here for the `cr` profile. |
| Feedback | Poll `GetPose()` / `GetAngle()` on the dashboard | No usable feedback port in this profile (30004 is not used). |
| Reply | `<code>,{payload},Command(args);` code 0 = OK, else the error id | Same format the bridge parses; `RobotMode()` 5 = enabled idle, 7 = running, 9 = error. |
| Wait for arrival | Poll `GetPose()` + `RobotMode()` every 0.1 s: the arm must **start moving within 2 s** (else the run stops with a clear message), then reach the target within 1 mm / 1 deg and stand still | A blocking `Sync()` hung forever when an accepted `MovL` never started (see the logs of 24 Nov, 14:43-14:47), so `Sync()` is no longer used. |
| Stop | Normal stop first: `ResetRobot()` (fallback `Pause()`), confirmed by polling; only if that fails: `EmergencyStop()` | `EmergencyStop()` latches alarm 85 (run `clear`). `ResetRobot()/Pause()` are **not yet verified** on this firmware. |

Not verified on this arm: `AccJ()/AccL()` (a refusal is logged, not fatal), `User()/Tool()` effect on the motion port
(see "Frames"), and whether `EmergencyStop()` stops a moving arm quickly. Keep a hand on the physical E-STOP.

## Network
1. PC Ethernet: static IP in the robot's subnet, e.g. `192.168.1.100`, mask `255.255.255.0`, no gateway. Robot: `192.168.1.6`.
2. `ping 192.168.1.6` must answer.
3. **Close DobotStudio** (and the teammate's bridge/console) before connecting: only one client can hold the dashboard.
4. The robot must be enabled-capable: no alarm, physical E-STOP released.

## Run
```powershell
cd "D:\unity assignment\Diamond shop2\RobotControl"
python gem_picker.py --dry-run pick 1        # no connection: print the plan
python gem_picker.py status                  # pose / mode / alarms, selects User 1 / Tool 1, no motion
python gem_picker.py grip on                 # DO10 OFF, DO9 ON for 0.5 s, DO9 OFF (valve latches vacuum)
python gem_picker.py grip off                # DO9 OFF, DO10 ON for 0.5 s, DO10 OFF (valve latches release)
python gem_picker.py goto above 1 --step     # ask before every step
python gem_picker.py pick 1                  # full sequence, prints the plan then "Run? [y/N]"
python gem_picker.py clear                   # ClearError() after an alarm / E-STOP
```
`--yes` skips the `Run?` question, `--step` asks before each step, `--speed P` overrides the speed (max 30),
`--config FILE`, `-v` echoes every request/reply. Every request and reply is always written to `logs/robot_<time>.log`.

`goto approach N --dz D`: one `MovL` straight down/up to (pick N x, y, pick z + D, r). Allowed only when the current
XY is already within 2 mm of pick N (else refused: run `goto above N` first); D must be at least 5 mm; the target
must be inside the z limits. Example: `python gem_picker.py goto approach 1 --dz 20 --step`.

`goto` targets: `pick N`, `safe`, `drop`, `above N`, `above drop`. Every goto first lifts straight up to safe z
(unless already there), then moves with MovJ above the target, then (pick/drop only) MovL down.

### The pick sequence (12 steps, a wait for arrival after every move)
1 enable + User/Tool + speed - 2 MovL up to safe z at the current XY (skipped if already at/above safe z) -
3 MovJ above pick N - 4 MovL down to pick z - 5 grip on (0.5 s vacuum pulse) - 6 MovL up to the pick approach z - 7 MovJ to safe pose -
8 MovJ above drop - 9 MovL down to drop z - 10 release (0.5 s release pulse) - 11 MovL up to safe z - 12 MovJ to safe pose.

## Safety rules (enforced in code, covered by the tests)
* The whole plan is printed first and `Run? [y/N]` is asked (unless `--yes`); `--step` asks before each step.
* Speed above **30 %** is refused (config, `--speed`, and again in the driver).
* A target with z outside `limits.z_min .. z_max` is refused before anything is sent.
* The arm must be free of alarms, not busy, and `GetPose()` must return a real pose, else nothing is sent.
* Ctrl+C or **any** error (if a move was ever sent): a normal stop (`ResetRobot()`, which also clears the queued move),
  confirmed by polling; `EmergencyStop()` only if no normal stop is accepted/confirmed. Then DO9 OFF and DO10 OFF, then exit.
  A failed stop is reported ("use the physical E-STOP").
* Before every move `RobotMode()` must be 5 (enabled, idle); `EnableRobot()` is always sent in the setup step and the
  program waits for mode 5. If an accepted move does not start within 2 s the run stops and prints mode, alarms and pose.
* The suction valve **latches**: a DO9 pulse switches it to vacuum, a DO10 pulse switches it to release. DO9 and DO10 are
  never ON together (each ON is only sent after the other is confirmed OFF), and both are OFF again after every grip step.
* Stop / Ctrl+C / error: DO9 and DO10 are switched OFF but **DO10 is never pulsed automatically** (it would drop the gem
  wherever the arm is). If a vacuum pulse was done and no release completed, the program prints a warning that the gem
  may still be held. To release it: move the arm over a safe place and run `python gem_picker.py grip off`.
* Declining `Run?` / a `--step` question stops without any stop command; the suction output is left as it is.

## robot_config.json (all values editable; JSON has no comments, so they are explained here)
| Key | Meaning |
|-----|---------|
| `robot_ip`, `dashboard_port`, `motion_port` | `192.168.1.6`, 29999, 30003 |
| `user_frame`, `tool_frame` | frames selected with `User(n)` / `Tool(n)`; all poses were read in User 1 / Tool 1 |
| `inline_frame_args` | `false`: only `User()/Tool()` select frames. `true`: also append `,User=1,Tool=1` to every MovJ/MovL (use it if `status` shows a pose that differs from DobotStudio, or if you want the frame stated in every move; the firmware may refuse the syntax) |
| `speed_percent` | `SpeedFactor`, default 20, hard cap 30 |
| `acc_percent` | `AccJ`/`AccL` |
| `safe_z` | travel height 160.0 (safe pose, above drop) |
| `pick_approach_z` | z of "above pick N" and of the lift after the grip: 50.0 for every pick (missing = `safe_z`) |
| `pick_poses` | `"N": [x, y, z, r]` for gems 1..9 (mm, deg) |
| `drop_pose` | `[5.36, 32.36, 90.58, -53.26]` |
| `safe_pose` | **DEFAULT - replace with a taught pose**: centre of the 9 picks at `safe_z`, R of pick 5 |
| `vacuum_do`, `blow_do` | 9 and 10 |
| `vacuum_pulse_s`, `release_pulse_s` | length of the DO9 vacuum pulse (0.5 s, replaces the old grip wait) and of the DO10 release pulse (0.5 s) |
| `limits.z_min`, `limits.z_max` | lowest pick/drop z - 5 = -24.60, safe_z + 50 = 210.0 |
| `arrive_tol_mm`, `arrive_tol_deg`, `move_timeout_s` | arrival check 1 mm / 1 deg, 60 s per move |

## Frames
`User(1)` and `Tool(1)` set the robot's global frames, and `GetPose()` reports in them. This program reads the pose
after selecting them and relies on the firmware using the same frames for `MovJ/MovL`. Nothing here proves that on
your robot: that is what checklist items 3-4 are for. The poses are Cartesian values in User 1, so the plan makes no
claim about the distance to the robot base or about reachability: the drop pose is far from the pick area and
`MovJ` to it may be refused by the firmware (an error is reported and the arm stops). Test it with `goto above drop --step` first.

## First-run checklist (stop at the first surprise)
1. `python gem_picker.py --dry-run pick 1` and `pick 9`: read the targets, they must match your taught poses.
2. DobotStudio closed, robot enabled-capable, physical E-STOP within reach, area clear.
3. `python gem_picker.py status`: no alarms; frames selected; **compare x, y, z, r with DobotStudio (User 1 / Tool 1). More than 1 mm difference: stop.**
4. If it differs: set `inline_frame_args` to `true`, run `status` again; still different: stop and report the log file.
5. With a gem under the nozzle: `grip on` (0.5 s DO9 pulse, the gem stays held), then `grip off` (DO10 pulse, it drops). Check DO9/DO10 on the robot.
6. `goto above 1 --step` (answer `y` one step at a time): the nozzle ends **directly above gem #1** at safe z.
7. `goto pick 1 --step`: the nozzle reaches the gem at z -19.60. Then `grip on`, `goto above 1`, `grip off` (over a safe place).
8. Test the stop once at low risk: start `goto above 5`, press Ctrl+C: the arm should stop with `ResetRobot()` (no alarm). If it prints that EmergencyStop() was used, run `clear`.
9. `goto above drop --step` (empty suction): only then the full `pick 1 --step`, then `pick 1`. Start at 10 % (`--speed 10`) for the first runs, then use the config speed (20 %).
10. Replace `safe_pose` with a taught pose when you have one.

After a stop or alarm: fix the cause, release the E-STOP if pressed, then `python gem_picker.py clear`
(the next `pick/goto` re-enables the robot). Commands answering `-1` usually mean the robot is in that alarm state.

## Slot mapping: Unity cabinet -> physical pick pose

The cabinet slots `Slot_0 .. Slot_8` in Unity (`GemShowcase`, prefab `ShowcaseCabinet`) are the physical pick poses #1 .. #9:
**pick N = slot index + 1**. Every order item carries `"slot": N` (1..9). Put the real gems in the cabinet like this:

| Unity slot | Slot # in order JSON | Gem id | Name | Shape | Color | `gem_picker.py pick N` | Pick pose x, y, z, r (mm/deg) |
|---|---|---|---|---|---|---|---|
| Slot_0 | 1 | gem_ruby_round | Ruby Round | round | red | pick 1 | -114.2, 263.2, -19.6, -16.04 |
| Slot_1 | 2 | gem_sapphire_round | Sapphire Round | round | blue | pick 2 | -113.32, 303.0, -19.6, -12.1 |
| Slot_2 | 3 | gem_amethyst_round | Amethyst Round | round | purple | pick 3 | -112.16, 338.15, -19.6, -17.01 |
| Slot_3 | 4 | gem_tsavorite_emerald | Tsavorite Emerald | emerald | green | pick 4 | -148.15, 266.61, -19.6, -11.33 |
| Slot_4 | 5 | gem_diamond_emerald | Diamond Emerald | emerald | white | pick 5 | -154.45, 306.39, -19.6, -5.97 |
| Slot_5 | 6 | gem_morganite_emerald | Morganite Emerald | emerald | pink | pick 6 | -154.92, 339.21, -19.6, -3.28 |
| Slot_6 | 7 | gem_peridot_round | Peridot Round | round | green | pick 7 | -194.18, 262.76, -19.6, -4.22 |
| Slot_7 | 8 | gem_sapphire_emerald | Sapphire Emerald | emerald | blue | pick 8 | -193.73, 301.8, -19.6, -0.76 |
| Slot_8 | 9 | gem_citrine_round | Citrine Round | round | yellow | pick 9 | -195.57, 340.97, -19.6, 2.32 |

Rules: one gem per slot (an order that asks for the same slot twice is rejected). The pick poses are the ones in
`robot_config.json`. If you change the gems in the Unity `GemShowcase`, change this table and the real cabinet too.

## robot_server.py: pay in Unity -> the arm picks (no bridge)

```powershell
cd "D:\unity assignment\Diamond shop2\RobotControl"
python robot_server.py            # DRY RUN: logs the planned steps and sends statuses, never touches the robot
python robot_server.py --arm      # ARMED: the robot moves (speed from robot_config.json, capped at 30 %)
```

TCP `0.0.0.0:5005`, one JSON per line, the protocol Unity's `RobotLink` speaks (`Docs/Robot_TCP_Interface.md`):
`ping` -> `pong`; `order` -> `ack` at once, then `status` `picking` / `packing` per gem and `done` after the last gem.
The pick sequence is the same code as `gem_picker.py pick N` (`pick_and_drop_steps`): setup once, then per gem
above pick, down, grip on (0.5 s vacuum pulse), up, safe pose (= `picking`), above drop, down, release, up, safe pose (= `packing`).

- Orders run one at a time in arrival order. The queue lives in memory only (nothing is written to disk).
- Rejected with status `error` (after the `ack`): slot missing or not 1..9, empty or 10+ items, the same slot twice, an
  order older than 10 minutes (`createdAt`, unix seconds; `--max-age` changes it; no `createdAt` = accepted), any order while
  E-STOP is active, a full queue (50). The same `orderId` again is acked again but run only once.
- `{"type":"estop"}` from Unity, or Ctrl+C on the server: normal stop first (`ResetRobot()`, `EmergencyStop()` only if that
  fails), DO9/DO10 OFF **without pulsing DO10**, queued orders cancelled (`error`), new orders rejected until
  `{"type":"estop_reset"}`. If a gem may still be gripped the server says so: move the arm over a safe place and run
  `python gem_picker.py grip off`.
- A robot error during an order (alarm, move refused, move not started, link lost): stop as above, status `error` with the
  robot's text, the server keeps running.
- Console: one line per step like `gem_picker.py`. Logs go to `logs/`.
- Unity path: `OrderEvents.OnOrderPaid` -> `OrderSender` -> `RobotLink` (host/port from the in-headset RobotAddress setting,
  default `127.0.0.1:5005` in the Editor; on the Quest use this PC's Wi-Fi IP and allow port 5005 in the firewall).
- The server refuses to start if the port is already in use (Windows `SO_EXCLUSIVEADDRUSE`) and prints the PID to stop
  (`Stop-Process -Id <pid>`). Only one server at a time; stop it when you are done testing.
- Close DobotStudio and the teammate's bridge before `--arm`.

## Tests (no robot needed)
```powershell
python -m unittest discover -s tests -v
```
55 tests against a fake controller: config limits, the planner (step list and targets, z limits, lift skipping),
grip order and "never both ON", error ids, arrival check, `--dry-run` (no connection), declining, `--step`, errors and
Ctrl+C (stop + both outputs OFF). They do not prove the real firmware accepts the commands.
