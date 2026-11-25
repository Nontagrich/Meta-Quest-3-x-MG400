# Testing the bridge with the REAL MG400 (no Unity)

## 0. Start commands (copy exactly)
Terminal 1 - bridge on the real arm (run from this folder; the launcher passes extra args on to `bridge_server.py`):
```powershell
cd "D:\unity assignment\Diamond shop2\External\MG400_Unity_Control\Python_Automation"
.\Start-MG400Bridge.cmd -RobotIp 192.168.1.6 --user-frame 1 --tool-frame 1
```
Terminal 2 - console:
```powershell
python bridge_console.py --host 127.0.0.1 --port 5555
```
The launcher always adds `--real --ip <RobotIp> --profile cr --no-audio --no-mqtt`. It refuses to start while a bridge
client (Unity, a console) is connected or while another program holds the robot's ports.
**DobotStudio must be disconnected/closed BEFORE the bridge connects to the robot** (only one dashboard client at a time).
Suction = DO9, blow-off = DO10 (console defaults). Change with `--vac-do N --blow-do M` (`--blow-do 0` = none).

Keep a hand on the robot's physical E-STOP for every step. The console `estop` is a software stop and has not been
verified on the real arm yet (step 9 tests it).

## 1. Network
```
[Robot 192.168.1.6] --LAN cable-- [PC Ethernet: static 192.168.1.100 / 255.255.255.0, no gateway]
[PC Wi-Fi: group router]  (only needed later for the Quest)
```
1. Windows: Settings > Network > Ethernet > IP assignment: Manual, IPv4 `192.168.1.100`, mask `255.255.255.0`, gateway empty.
2. `ping 192.168.1.6` must answer. Robot ports used: 29999 (dashboard, "cr" profile) and 30003 (motion);
   the "mg400" profile uses 29900/29901. `--profile auto` tries both.
3. Close DobotStudio (see section 0) before starting the bridge.

The console prints `*** REAL ROBOT ***` when the bridge reports `sim=false`. Logs: `logs/console_<time>.log`.
First prove the console on the simulator: `python bridge_server.py --sim --no-audio --no-mqtt`
(the simulator's arm model cannot reach the drop pose, so `goto drop` fails there; frames cannot be set in sim).

## 2. Right after starting the bridge: compare the pose with DobotStudio  (STOP rule)
1. Console: `state`. Note x, y, z, r.
2. Read the same pose in DobotStudio with **User Frame 1 / Tool Frame 1** selected (DobotStudio can only be open
   while the bridge is NOT connected: read it first, close it, then start the bridge, or read the robot's teach pendant).
3. If any of x, y, z differs by **more than 1 mm** (r by more than 1 deg): **STOP. Do not move the arm.**
   Try `frame 1 1` once, run `state` again; if it still differs, stop and report it with the log file name.

What the bridge uses: it never sent `User=`/`Tool=` in its motion commands, so it used whatever global frame the robot
had selected, and `GetPose` reports in that same frame. The bridge now selects frames only when asked
(`--user-frame 1 --tool-frame 1` at start, or `frame 1 1` in the console). `ws` shows `frames: user/tool`;
`null` = never selected = unknown. `goto` warns when the frames are not 1/1. Do NOT test other frames while the arm holds a gem.

## 3. First real pick: nozzle check  (console enforces it)
Before the first descent run `goto 1 safe` and look at the arm: the nozzle must be **directly above gem #1**.
The console asks "Is the nozzle directly above gem #1?" after arriving; `goto N` without `safe` is refused until you answer y.
If it is not above the gem: stop, re-check frames and the pick pose.

## 4. What the console refuses
Motion is refused when: not connected, E-STOP active, no fresh state (older than 2 s), speed not set in this session,
or speed above 30 %. Each motion prints pose, target, distance, speed and asks `Move? [y/N]`.
`s` + Enter or Ctrl+C sends `estop` at any time, also while a move is waiting for `sync`.
Bridge state fields `enabled`, `do1` and `moving` are NOT truthful on the real arm (see the integration plan); trust DobotStudio.

Suction rules (console):
* `grip on`: DO10 OFF, then DO9 ON.
* `grip off`: DO9 OFF, DO10 ON for 0.3 s, then DO10 OFF.
* DO9 and DO10 are never ON together (also for raw `do N 1`; refused). Nothing turns ON while E-STOP is active.
* `estop` and `quit`: both DO9 and DO10 OFF (after `estop` the bridge still accepts `do`).

## 5. Test plan (stop at the first surprise)
Use speed 10 % for steps 4-9, never above 30 %.

| # | Step | Commands | Expected |
|---|------|----------|----------|
| 0 | Simulator run | bridge `--sim`, console `ws`, `speed 20`, `goto 1 safe`, `estop`, `reset` | all commands work |
| 1 | Connect, no motion | `ws`, `state` | `sim=false`; polygon X -221..-89, Y 238..366; frames user/tool 1 |
| 2 | Pose vs DobotStudio | section 2 | x,y,z within 1 mm, else STOP |
| 3 | Enable | `enable` | brakes release, no motion, ack ok |
| 4 | Tiny moves | `speed 10`, `rel 0 0 5`, `rel 0 0 -5`, `rel 5 0 0`, `rel -5 0 0` | moves 5 mm, `sync` ack, pose matches |
| 5 | Limit checks (no motion) | `check -150 300`, `check 250 0`, `check 0 100` | first ok; others refused |
| 6 | Safe height + nozzle check | `up 152.03`, `goto 1 safe`, answer the nozzle question | nozzle directly above gem #1 |
| 7 | Descend and suction | `goto 1`, `grip on`, wait, `up 152.03`, `grip off` | tool reaches Z -77.18; gem lifted, released with the blow pulse |
| 8 | All pick poses | `goto N safe` for N = 2..9, then `goto N` for 1-2 of them | poses reachable, no limit errors |
| 9 | E-STOP | start `rel 0 0 -30`, answer y, then type `s` | arm stops; DO9/DO10 OFF; further moves refused; `reset` clears. If it does not stop, use the physical button and report |
| 10 | Carry route | gem held: `goto W`, `goto drop safe` (J, only from W), `goto drop`, `grip off`, `up 152.03`, `movej -96.18 244.96 152.03 -53.26` | bridge allows only this route into the drop zone; speed capped at 10 % |
| 11 | Disconnect | `quit` while idle, answer n | DO9/DO10 OFF; bridge keeps running; reconnect works |

If a step fails: `estop`, note the log file name and the exact message, do not retry with a higher speed.

## 6. Known risks
* The drop pose is only 33 mm from the base (the bridge's drop zone allows it only via W). Run step 10 at 10 % with a hand on the stop.
* Pick #9 (-195.57, 340.97) is far from the base (about 393 mm); the real arm's reach there is not verified.
* `EmergencyStop()` as a dashboard command is not verified on this firmware.
* The temporary polygon in `workspace.json` is computed from the pick range, not measured.

## 7. Results (fill in)
| Step | Pass / fail | Message (ack msg / error) | Notes |
|------|-------------|---------------------------|-------|
| 0 Simulator run | | | |
| 1 Connect, no motion | | | |
| 2 Pose vs DobotStudio (<= 1 mm) | | | |
| 3 Enable | | | |
| 4 Tiny moves | | | |
| 5 Limit checks | | | |
| 6 Safe height + nozzle check | | | |
| 7 Descend and suction | | | |
| 8 All pick poses | | | |
| 9 E-STOP | | | |
| 10 Carry route | | | |
| 11 Disconnect | | | |

Log file(s): `logs/console_________.log`   Date / tester: ____________
