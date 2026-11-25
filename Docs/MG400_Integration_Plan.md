# MG400 integration plan — teammate's bridge as the only robot backend

Status: **analysis + import only (step 12A).** Our features still use `RobotLink` (TCP 5005) and `Tools/mock_robot_server.py`.
Nothing in Storefront / Maintenance was rewired. The teammate's files were not modified.

Sources read: `External/MG400_Unity_Control/README_TH.md`, `PROJECT_OVERVIEW_TH.md`, all 6 Runtime `.cs` files, and
`Python_Automation/bridge_server.py`, `motion_workspace.py`, `planar_teleop.py`, `sim_robot.py`, `mg400_driver.py` (geometry,
profiles), `Start-MG400Bridge.ps1`, `requirements.txt`.

---

## 0. What was done in this step

| Item | Result |
|---|---|
| Imported | `External/.../Unity_Assets/MG400Control/Runtime/*.cs` + `.cs.meta` → `Assets/ThirdParty/MG400Control/Runtime/` (byte-identical, GUIDs unique in the project) |
| Not imported | `Compatibility/` (GemInfo, GemStorage, FallenGemSpawner), all `.py/.ps1/.cmd` |
| Our side | `Assets/ThirdParty/MG400Control/GemStore.MG400Control.asmdef` (refs: Unity.InputSystem, Unity.TextMeshPro, Unity.XR.Interaction.Toolkit; autoReferenced) and `Assets/ThirdParty/MG400Control/GemStoreShims/FallenGemSpawnerShim.cs` (see §2.6) |
| Compile | 0 errors. Their classes compile into assembly `GemStore.MG400Control` (namespace `CPE494`). Only pre-existing Agora sample warnings remain. |
| Storefront / Maintenance | Played in the Editor after the import: 0 errors, same warnings as before (MQTT broker offline, old pending orders); `RobotLink` still on 127.0.0.1:5005; no MG400 objects in either scene. |
| Test scene | `Assets/_Project/Scenes/Test_MG400Bridge.unity`: camera + light + `MG400_Root` (MG400BridgeClient host 127.0.0.1:5555, MG400DigitalTwin without a model, MG400Controller, RobotSafetyMonitor). Not in Build Settings. |
| Smoke test | §9 |

---

## 1a. Their Unity runtime vs ours

| Their class (CPE494) | What it does | Our counterpart (GemStore.*) | Overlap / conflict | Decision |
|---|---|---|---|---|
| `MG400BridgeClient` | TCP client to :5555, reader thread → main-thread queues, reconnect 3 s, ping 5 s, events `OnState/OnAck/OnEvent/OnConnectionChanged`, `Send(cmd, setup)`, `LastState`, `StateFresh` (<1.5 s) | `RobotLink` (TCP client to :5005, ping 2 s, 6 s silence = drop, reconnect 3 s, `SendRaw`, `OnMessage(type,json)`, `SetAddress()` + PlayerPrefs, **DontDestroyOnLoad**) | Same job, different protocol. Theirs has no runtime address change (host is read once in `Start`), no DDOL. | **Replace** RobotLink's transport with MG400BridgeClient. **Adapt on our side**: a small `MG400Link` (ours) that owns the MG400_Root, keeps it across scenes (DDOL), sets `host/controlPort` from our `RobotAddress` before `Start`, and recreates the client when the address changes. |
| `MG400Controller` | High-level coroutines: `Enable/Disable/SpeedFactor/Home/MoveJoints/MoveTo/MoveCartesianMm/SyncWait/SetVacuum/Raw/CheckPackingWorkspace`, ack waiting with timeouts + exceptions, `EStop()/ResetFromStop()`, **LocalSim** when the bridge is offline, Editor test panel | `OrderSender` + `RobotOrderBridge` (order queue/resend/status), `TeleopController` macros (Pick/Place as teleop sequences) | Theirs = motion primitives; ours = business logic. No name clash. | **Keep** as the motion API. Our order sequence (12B) and macros (12C) call it. Do not use `MoveTo(Vector3)` (twin-frame metres, LocalSim IK path) — use `MoveCartesianMm`. |
| `MG400DigitalTwin` | Drives J1–J4 transforms from joints, `TcpPosition`, `BasePointToWorld` | Our blue "robot pose" cube in `TeleopVisuals` (from `robot_pose` mm via calibration) | Both visualise the real arm. | **Keep** (required by MG400Controller, which dereferences `Twin` every frame). Optional model later; place it with our calibration (§4). Keep our pose marker as the exact TCP indicator. |
| `MG400Geometry` (+ `MG400State`, `MG400Ack`) | Constants, FK/IK (twin frame, metres), `ClampWorkspace` (radius 220–440, Z −20…450 in twin frame), state/ack DTOs | `RobotFrame` (calibration math), `TeleopConfig` workspace box | Different frames (§4). | **Keep** DTOs; do not use its clamp for real-robot limits (the bridge's polygon is authoritative). |
| `RobotSafetyMonitor` | E-Stop flag + event, **F9 = E-Stop, F10 = reset** (keyboard), optional safety-zone collider (camera inside → teleop pause), status text | `EStopPanel` + `TeleopController.TriggerEStop/ResetEStop` (+ B button), auto-stops (tracking lost, focus lost, link drop) | Two E-STOP owners would disagree. | **Adapt**: keep it in MG400_Root (theirs expects it) but drive it from our E-STOP (`TriggerEStop()` / `ResetStop()`), our UI stays the only UI. |
| `TeleopStation` | Teleop: keyboard **T** toggles real `teleop_xy_*` session from the right controller's tracked position; pad/ray/mouse preview in sim; **Space** = vacuum; auto-wires the first `XRRayInteractor` | `TeleopController` + `ControlVolume` (deadman, OFFSET/ABSOLUTE, precision, gem marker, macros, HUD) | **Conflicts**: T is our simulator trigger and (since 11/23) grabs in the store; Space is vacuum; no deadman. | **Do not put it in our scenes.** Reuse its protocol logic as a reference; our TeleopController talks to `teleop_xy_*` itself (§5). |

## 1b. Names, assemblies, input, singletons, scenes, editor-only

- **Namespaces**: theirs `CPE494`, ours `GemStore.*` → no collisions. `CPE494.MainThread` (static dispatcher) is unique.
  `GemShape`/`GemInfo` exist only in their Compatibility folder (not imported); importing it later would clash with
  `GemStore.Showcase.GemShape` only in files that `using` both namespaces.
- **Assembly**: their code had no asmdef. We added `GemStore.MG400Control.asmdef` (ours) next to their folder, so their code
  is isolated and our Assembly-CSharp references it automatically. Their code cannot see ours (good).
- **Packages**: they need Input System 1.20.0, XRI 3.6.1, uGUI 2.6.0 (TMP) — we have exactly these. Unity 6000.6.5f1 vs our
  6000.6.4f1: compiles fine.
- **Active Input Handling**: theirs needs Input System (New) or Both. Ours is already **Input System Package (New)**
  (`activeInputHandler: 1`). `TeleopStation.toggleDirectKey` is a `KeyCode` field but is never read through the old Input
  Manager, so no change needed.
- **Keyboard shortcuts** (Editor/PC only): F9 E-Stop, F10 reset (RobotSafetyMonitor), T toggle tracking + Space vacuum
  (TeleopStation). T/Space conflict with our XR Interaction Simulator use → keep TeleopStation out.
- **Singletons**: all four use `FindAnyObjectByType` lazy `Instance`, and `Awake` does `Destroy(gameObject)` on a duplicate —
  this destroys the **whole GameObject**, so a duplicate component on another MG400_Root would delete that root.
  **No DontDestroyOnLoad** anywhere → the TCP connection is closed on every scene load (our RobotLink survives scene changes).
- **Scene requirements**: one `MG400_Root` with BridgeClient + Controller + DigitalTwin + SafetyMonitor. **DigitalTwin is
  mandatory** (`MG400Controller.Update` calls `Twin.SetTargetJoints` without a null check → NullReferenceException every
  frame without it). Joint transforms may be empty.
- **Editor-only**: `MG400Controller.OnGUI` "MG400 TEST" jog panel is inside `#if UNITY_EDITOR` (shows in the Game view of any
  scene with an MG400Controller in Play Mode; not in builds). Its jog buttons are disabled in SIM ("SIM MODE - MOVES DISABLED").
- **LocalSim**: when the bridge is offline the controller silently animates the twin locally and `MoveTo/MoveJoints/Home`
  "succeed" without hardware. `MoveCartesianMm` refuses offline (good). Our code must check `BridgeOnline && !SimMode`
  before telling the customer a robot did something.

---

## 2. Findings that need a decision (teammate files were NOT changed)

| # | Finding | Effect | Proposed change (for the teammate) |
|---|---|---|---|
| 2.1 | `MG400Controller.Enable()` / `Disable()` call `SendAndWait(0, ...)`, but `HandleAck` ignores `id <= 0` | **Verified**: `Enable()` always ends with `TimeoutException: no ack for 'enable' within 30s` (the bridge did answer) | `SendAndWait(MG400BridgeClient.Instance.NextId(), "enable", null)` (same for disable) |
| 2.2 | Bridge state on REAL: `"enabled": 1 if (sim and robot.enabled) else 1` → always 1; `do1` reads `backend.robot.vacuum` which RealBackend does not have → always 0; `moving` only computed in sim → always false | Unity cannot trust enabled / vacuum / moving on the real arm | Read `RobotMode()` (5 = enabled idle, 7 = running) and `GetDO(1)` in the broadcast loop (or in PollingRealtime) |
| 2.3 | `MG400Controller.EStop()` sends `SpeedFactor(10)` + `DisableRobot()` — not a Dobot emergency stop | Arm brakes on disable, but there is no latched E-stop on the bridge; another client could re-enable | Add `estop` → `EmergencyStop()` (dashboard) + bridge-side lock until `estop_reset` (§3) |
| 2.4 | SIM backend uses **twin axes** (x lateral, y up, z forward, mm) while REAL uses **MG400 axes** (x forward, y lateral, z up) for `pose` and `move_to` | A sim test of real coordinates is misleading (our pick pose is rejected in sim for a different reason than on the robot) | Convert in SimBackend (`state()` and `MovL/MovJ` args) so sim speaks MG400 axes |
| 2.5 | Sim skips the workspace polygon entirely; `teleop_xy_*` refuses sim | The polygon and teleop can only be tested on the real arm | Apply `WorkspaceMoves` in sim too; let `PlanarTeleop` run on SimBackend |
| 2.6 | `TeleopStation.cs:457` uses `FallenGemSpawner` from Compatibility | Without it the Runtime does not compile | We added an inert shim `CPE494.FallenGemSpawner` (internal, `Instance == null`) in **our** `GemStoreShims/` folder. Teammate option: guard the line with a define or remove it; then delete our shim. |
| 2.7 | Bridge listens on `0.0.0.0:5555` with no authentication | Anyone on the group Wi-Fi can drive the arm | Bind to the Wi-Fi IP only + a shared token in the first message, or an IP allow-list |
| 2.8 | `MG400BridgeClient` reads `host/controlPort` once (in `Start`) | Our in-headset address editor cannot reconnect it | Add `public void Reconnect(string host, int port)`; until then we recreate the component |

---

## 3. Protocol: ours (RobotLink :5005) vs theirs (bridge :5555)

Both: one JSON object per line. Theirs: every Unity message is a **command** `{"id":n,"cmd":...}` and gets exactly one
`{"type":"ack","id":n,"ok":bool,"msg":...}`; the bridge pushes `{"type":"state",...}` at **~30 Hz** (measured 29 Hz).

| Our message | Direction | Their equivalent | Notes |
|---|---|---|---|
| `{"type":"ping"}` / `pong` | both | `{"cmd":"ping"}` → ack `msg:"pong"` | Their client pings every 5 s; link loss is detected by the socket, not by a silence timeout. Use `StateFresh` (state < 1.5 s old) as our "alive" signal. |
| `order` (orderId, items[shape,color,setting,metal]) | U→R | **none** | Proposal below, or Unity runs the sequence itself with primitives (12B). |
| `ack` (orderId) | R→U | **none** (only command acks) | |
| `status` picking/packing/done/error | R→U | **none** (`event` exists but only staff_call/accepted/ended) | Proposal below. |
| `teleop` {seq, enable:true, x,y,z,r, grip} | U→R | `teleop_xy_begin` → `teleop_xy_prepare`… → `teleop_xy_step {x:dx, y:dy}` → `teleop_xy_end`; grip → `do {index:1, value}` | XY only, **offsets from the session start**, Z and R locked, max 2 mm per step, ≤ ~20 mm/s (§5). REAL `cr` profile only. |
| `teleop` {enable:false} | U→R | `teleop_xy_end` | |
| `estop` | U→R | **none** (`disable` is the closest) | Proposal below. |
| `estop_reset` | U→R | `enable` (after clearing errors) | |
| `robot_pose` {x,y,z,r,grip} 10 Hz | R→U | `state` {pose[x,y,z,r], joints[4], do1, enabled, sim, moving, outsideWorkspace} 30 Hz | see 2.2 for real-arm caveats |
| (none) | U→R | `enable`, `disable`, `home` (real: PARK_XY at current Z/R), `speed {pct}`, `move_to`, `move_joints` (sim only), `do`, `sync` (wait arrival, real 18 s), `raw` (real: read-only queries), `check_packing_workspace {path}`, `get_state`, `staff_call/staff_end` | |

**Missing commands we need — proposals only (not implemented):**

1. `{"cmd":"estop"}` → dashboard `EmergencyStop()`, end any teleop, reject all motion until `{"cmd":"estop_reset"}`
   (`ClearError()` + `EnableRobot()`); broadcast `{"type":"event","event":"estop","active":true|false}`.
2. `{"cmd":"pick_place","orderId":"1234","pick":{"x","y","z","r"},"place":{...},"safeZ":80,"gripWait":0.5}` executed
   **inside the bridge** (atomic, survives a Wi-Fi drop of the headset), with progress events
   `{"type":"event","event":"order_status","orderId":"1234","status":"picking|packing|done|error","msg":""}`.
   Alternative: Unity sequences primitives (12B) — simpler but a headset sleep mid-sequence leaves the arm holding a gem.
3. Truthful state on the real arm: `enabled`, `moving` from `RobotMode()`, `do1` from `GetDO(1)` (2.2).
4. `{"cmd":"get_workspace"}` → `{corners:[...], minRadius, maxRadius, park:[x,y], teleopR:[-90,-55]}` so Unity can draw the
   real limits instead of hard-coding them.
5. `teleop_z_step` (or allow `move_to` Z-only while tracking is paused) for Maintenance Z control.
6. An address/token handshake (2.7).

---

## 4. Our Maintenance teleop on their API (for 12C)

**Frames.** MG400 base frame (pose, mm): X forward, Y lateral, Z up, R tool degrees. Their twin frame (metres): x = MG400 Y,
y = MG400 Z, z = MG400 X (see `MG400Controller.MoveTo`). Our `RobotCalibration` already maps **MG400 mm ↔ Unity tracking
space** if the three reference points in `TeleopConfig` are entered in MG400 coordinates (read them from the bridge pose
while touching P0/P1/P2 with the tool).

**XY teleop (OFFSET control volume stays).**
1. Deadman pressed → `teleop_xy_begin`. Ack `"Preparing controller start pose"` → keep sending `teleop_xy_prepare` every
   ≥ 0.1 s until ack `"Start pose reached. Z … mm, R … deg locked"` (or `"Z … mm, R … deg locked"` straight away).
   Remember `originXY = state.pose[0..1]` at that moment.
2. Each send tick (10 Hz, only after the previous ack — the bridge allows one request in flight):
   `teleop_xy_step {x: targetX − originX, y: targetY − originY}` where target = our control-volume target in MG400 mm.
   The bridge moves ≤ 2 mm per step, trims at the polygon (`"Workspace boundary reached"`), keeps Z/R.
3. Deadman released / tracking lost / focus lost / E-STOP → `teleop_xy_end` (+ `disable` for E-STOP until 3.1 exists).
   Any ack `ok:false` ends the session on the bridge → show `msg` on our HUD, require a new deadman press.
4. Z: **locked at the Z where tracking started.** Our control volume's Z is ignored during tracking. Choose a "safe Z"
   (e.g. 80 mm above the tray) with `move_to` **before** `teleop_xy_begin`.
5. R: must be inside **−90…−55°**; begin rotates it there in 3° steps (clamped 0.5° inside).
6. Speed: SpeedFactor 20 %, SpeedL 5, AccL 5, 2 mm per step → expect **≈ 10–20 mm/s** (much slower than our 100 mm/s cap).
   PRECISION mode becomes unnecessary; ABSOLUTE mode cannot be faster than this either.

**Pick / Place macro (move_to + do).** Tracking must be ended first (motion commands are rejected while it is active):
`teleop_xy_end` → `speed {pct:20}` → `move_to {x,y,z:pickZ,r, type:"L"}` → `sync` → `do {1,true}` → wait 0.5 s →
`move_to {x,y,z:safeZ,r}` → `sync` → (`teleop_xy_begin` again for more manual control). Place: same with `do {1,false}`.
`MG400Controller.MoveCartesianMm(x,y,z,r,speed)` + `SetVacuum(bool)` already do this (each waits for the ack; `wait:true`
adds `sync`). Abort: stop the coroutine + E-STOP path (3.1 / `disable`); the bridge has no "cancel move".

**Workspace for our volume / clamp.** Replace our box (TeleopConfig x 100–400, y −150…150) by their limits:
XY must be inside the convex polygon `CORNERS` **and** `220 ≤ √(x²+y²) ≤ 440` (the polygon ∩ annulus). Corner 1
(191.35, 1.91) is at radius 191.4 < 220, so that corner region is unreachable. Keep our own Z limits (the bridge does not
check Z). Draw the polygon in the control volume and the real overlay instead of the box (better: from `get_workspace`, 3.4).

**Digital twin on the real robot (AR).** Place the `MG400DigitalTwin` root with our calibration:
`origin = cal.RobotToUnity(0,0,z0)`, `forward = cal.RobotToUnity(100,0,z0) − origin` (twin +z = MG400 +X),
`up = cal.RobotToUnity(0,0,z0+100) − origin` (twin +y = MG400 +Z), `rotation = LookRotation(forward, up)`, scale 1.
`z0` = height of the twin's base-top below MG400 Z = 0: measure once by comparing `MG400Geometry.FK(state.joints)` with
`state.pose` on the real arm. Their E2/E3 joint offsets are "to be calibrated before hardware day" (their note), and the
twin x = MG400 Y mapping must be sign-checked with one real Y jog (Unity is left-handed, MG400 right-handed). Until verified,
treat the twin as a visual aid and keep our pose marker (exact TCP from `state.pose` through the calibration).

---

## 5. Safety limits (exact values, current files)

From `motion_workspace.py`:

```
CORNERS    = ((191.35, 1.91), (341.67, 13.57), (315.45, 296.70), (130.25, 348.13))   # mm, perimeter order 1,2,4,3
PARK_XY    = (244.68, 165.0775)
MIN_RADIUS = 220.0, MAX_RADIUS = 440.0                                                # mm, sqrt(x² + y²)
```
From `planar_teleop.py`: `R_MIN = -90.0`, `R_MAX = -55.0` (entry margin 0.5°, R prep step 3°), `ROBOT_SPEED_FACTOR = 20`,
`MAX_STEP = 2.0` mm, `DEADBAND = 0.2` mm, session `TIMEOUT = 5.0` s, Z readback tolerance 0.005 mm.
From `MG400Geometry` / `mg400_driver.GEOMETRY` (twin/sim only): radius 220–440 mm, Z −20…450 mm (sim's vertical axis).
From `MG400Controller`: `maxSpeedPct = 50` cap on `speed`; joint clamp J1 ±140, J2 −30…210, J3 −100…240, J4 ±185 (twin convention).

**Which commands they apply to (REAL backend):**

| Command | Polygon + radius | Z | R | Other |
|---|---|---|---|---|
| `move_to` | **yes**: straight XY segment from the **current** pose to the target; both must be inside the polygon, closest point of the segment to the base ≥ 220, both ends ≤ 440 | no | no (`preserveR:true` keeps current R) | linear only (`type:"L"`); robot must be enabled + idle (`RobotMode()==5`); previous move must be confirmed |
| `home` | yes (moves to PARK_XY at current Z/R) | no | no | |
| `check_packing_workspace` | yes, for the whole path, starting from the current pose | – | – | dry run, no motion |
| `teleop_xy_*` | yes (steps trimmed at the edge; entry route allowed once from outside) | **locked** | **−90…−55** | REAL `cr` profile only |
| `move_joints` | – | – | – | **refused** on the real arm |
| `raw` | – | – | – | real: only `GetPose() GetAngle() RobotMode() GetErrorID()` |
| `do`, `speed`, `enable`, `disable`, `sync` | no checks | | | |

SIM backend: **none of the above**; only sim's own reach check (radius 220–440 in twin x/z, height −20…450) on MovL/MovJ.

**Our real test poses** (X ≈ −114…−196, Y 263…341, Z −77.18; drop (5.36, 32.36, 90.58, −53.26)):

| Pose | radius | inside polygon | bridge (REAL) | bridge (SIM, verified) |
|---|---|---|---|---|
| (−114, 263, −77.18) | 286.6 | **no** (polygon X ≥ 130) | `move_to` → **ok:false** "Workspace limit: XY path starts outside or crosses the four-point area or its reach limits"; no motion | – |
| (−196, 341, −77.18) | 393.3 | **no** | same rejection | – |
| (−150, 300, −77.18, −60) | 335.4 | **no** | same rejection | **ok:false** "1,Target out of workspace" (sim reach check in twin axes) |
| drop (5.36, 32.36, 90.58, −53.26) | **32.8 < 220** | **no** | same rejection | **ok:false** "1,Target out of workspace" |

Z = −77.18 itself is **not** checked by the bridge (only by the Dobot controller). R = −53.26 is fine for `move_to` but is
outside the teleop R range (begin would first rotate it to −55.5°).

**Consequences:**
- With the current `CORNERS`, the order pick sequence **cannot run at all**: every pick and the drop are rejected.
- If the arm is left at one of these poses (e.g. after teaching on the pendant), **every later `move_to`/`home`/packing check
  is rejected** because the *start* is outside; only `teleop_xy_begin`'s entry route (same Z, linear) can bring it back, and
  only if a checked path exists.
- Fix options (teammate's files, not changed here): re-measure `CORNERS` (and `PARK_XY`) for **our** tray and drop
  positions on the negative-X side, and decide whether a drop at radius 33 mm is acceptable (it is inside `MIN_RADIUS`, so
  either move the drop point outward to ≥ 220 mm or make the minimum radius configurable). The polygon must stay convex.

---

## 6. Networking for a standalone Quest

- Bridge runs on the PC. **Unity host = the PC's Wi-Fi LAN IP** (the group router subnet, e.g. 192.168.0.x — check with
  `ipconfig`), **not 127.0.0.1** (that is the headset itself). Port 5555.
- PC has two networks: **Ethernet cable → MG400** (static, same subnet as the robot, e.g. PC 192.168.1.10/24, robot
  192.168.1.6, **no default gateway** on this adapter) and **Wi-Fi → group router** (DHCP). The two subnets must differ.
- Windows Firewall (run PowerShell as admin; set the Wi-Fi network profile to Private):
  ```powershell
  New-NetFirewallRule -DisplayName "MG400 bridge 5555" -Direction Inbound -Protocol TCP -LocalPort 5555 -Action Allow -Profile Private
  ```
  Add 5556 only if the voice relay is used. Also allow python.exe if Windows asks on first start.
- Router: client/AP isolation must be off (headset and PC must reach each other). Test from another Wi-Fi device:
  `Test-NetConnection <PC-WiFi-IP> -Port 5555`.
- Android build: Internet access must be enabled (Player Settings → Internet Access = Require).
- Our in-headset address editor (RobotAddressPanel, PlayerPrefs) can store the bridge IP:5555 once 2.8 / our wrapper exists.

---

## 7. Migration plan

### Prompt 12B — order pick sequence on the bridge
1. Add our `MG400Link` (GemStore.Network): creates/keeps one MG400_Root (BridgeClient + Controller + Twin + SafetyMonitor)
   with DontDestroyOnLoad, host/port from `RobotAddress`, reconnect on address change; exposes `Online`, `IsReal`
   (`!SimMode`), `Pose`, `OnState`. Remove RobotLink/OrderSender from scenes only at the end of this step.
2. Data: a ScriptableObject `RobotStations` with pick pose per gem slot (MG400 mm + R), drop pose, safe Z, speed —
   **values inside the bridge workspace** (needs the teammate's new CORNERS, §5).
3. `RobotOrderRunner` (ours): for each item → `check_packing_workspace` for the XY path → `speed` → `move_to` safe Z above
   pick → down → `do on` → wait → up → over drop → down → `do off` → up; `sync` after each move. Status mapping for the
   receipt: picking (first move) / packing (after last place) / done / error (any ok:false/exception, with `msg`).
4. Keep our order persistence (pending orders file) but only "send" when `IsReal` (or a dev flag for SIM).
5. Abort: E-STOP and staff cancel stop the coroutine and send `disable` (or `estop` when 3.1 exists).
6. Replace `RobotStatusLight`/HUD sources with `MG400Link`. Update `Docs/Robot_TCP_Interface.md` to point to the bridge.
7. Test: SIM (path logic; note 2.4/2.5), then real arm slowly with the teammate.

### Prompt 12C — Maintenance teleop on the bridge
1. `TeleopController` backend switch: instead of `teleop` JSON to RobotLink, a `BridgeTeleopSession` implements §4
   (begin/prepare/step/end, one request in flight, ack handling, HUD messages from `msg`).
2. Control volume: draw polygon ∩ annulus; clamp the target to it before computing offsets; show locked Z and R range.
3. Deadman/E-STOP/auto-stop map to `teleop_xy_end` (+ disable on E-STOP); RobotSafetyMonitor mirrors our E-STOP.
4. Pick/Place macros → `MoveCartesianMm` + `SetVacuum` (§4), only when tracking is ended.
5. Calibration: enter TeleopConfig P0–P2 in MG400 mm (read from `state.pose`), then place the twin (§4).
6. Remove ABSOLUTE/PRECISION speed paths that the bridge cannot honour, or keep them as Unity-side target filters.
7. Retire `Tools/mock_robot_server.py`; development uses `bridge_server.py --sim` (with 2.4/2.5 fixed, sim covers more).

---

## 8. How to run their bridge (Windows)

From `External/MG400_Unity_Control/Python_Automation`:

```powershell
py -3 bridge_server.py --sim --no-audio --no-mqtt
```
On this PC there is no `py` launcher; use:
```powershell
python bridge_server.py --sim --no-audio --no-mqtt
```
Python 3.11+ (tested here with 3.12.10). **No pip packages are needed for SIM.** Optional extras
(`pip install -r requirements.txt`): `paho-mqtt` (staff-call over MQTT), `sounddevice` (voice relay). Real arm:
`.\Start-MG400Bridge.cmd -RobotIp 192.168.1.6` and wait for `robot backend: REAL`.

## 9. Smoke test results (Editor, `Test_MG400Bridge.unity`, bridge `--sim --no-audio --no-mqtt`)

| Check | Result |
|---|---|
| Connect | connected immediately to 127.0.0.1:5555 (bridge log: "Unity client connected") |
| State stream | 59 `state` messages in 2 s (**29 Hz**), `sim=true`, start pose (0, 130, 430, 0) — sim/twin axes |
| `Enable()` | **TimeoutException after 30 s** — bug 2.1 (bridge acked with id 0, controller ignores id 0) |
| `MoveCartesianMm(244.68, 165.08, 50, −60, 20%)` (PARK_XY, inside the current polygon) | **ok**, 4.1 s, pose → (244.7, 165.1, 50.0, −60.0) |
| `MoveCartesianMm` +10 mm X | **ok**, 0.4 s, pose → (254.7, 165.1, 50.0, −60.0) |
| `SetVacuum(true/false)` | ok, state `do1` 1 → 0 |
| Our pick pose (−150, 300, −77.18, −60) | rejected: "1,Target out of workspace" (sim reach check) |
| Our drop pose (5.36, 32.36, 90.58, −53.26) | rejected: "1,Target out of workspace" |
| `teleop_xy_begin` in sim | rejected: "Planar tracking requires the real MG400 TCP/IP profile (cr)" |
| E-STOP via RobotSafetyMonitor | `Halted=true`, next move → OperationCanceledException; reset → `Halted=false` |
| Console | 0 errors (test scene only: XR simulator warns that this test scene has no XR Origin) |

Not tested: real MG400, the polygon on the real backend, teleop_xy on the real arm, Quest over Wi-Fi.

## 10. Workspace config and drop zone (update)

Edited in the teammate's `Python_Automation` on request (review with them):
- `workspace.json` now holds corners, parkXY, min/maxRadius and `dropZone`; `motion_workspace.py` validates it at import (convex, counter-clockwise, radii, park/W inside) and exits with `[workspace] INVALID ...`.
- `make_workspace.py` computes the TEMPORARY corners (smallest enclosing rectangle of the picks + 25 mm). Individual pick poses are not in the repo, so the 4 extremes of the X/Y range stand in (`--picks picks.json` to use real ones).
- Temporary values: corners (-221,238) (-89,238) (-89,366) (-221,366); park (-155,302); W (-96.18, 244.96, Z 152.03); drop zone centre (5.36, 32.36), r 30, minZ 85.58, speed 10 %.
- Drop rules: enter/leave only by `move_to` type `J` between W and (drop x,y,safeZ) (real: `MovJ(...,SpeedJ=10,AccJ=10)`); inside the zone only vertical `L` on the drop axis, Z 85.58..152.03 (`MovL(...,SpeedL=10,AccL=10,CP=0)`); other J moves rejected; MIN_RADIUS unchanged.
- Sim now uses the real frame (Z up), answers `RobotMode`, honours SpeedJ/SpeedL, starts at park, and `move_to`/`home`/`sync`/`check_packing_workspace` go through the same workspace rules as the real arm.
- Reachability (twin model, uncalibrated): drop pose and drop axis at safe Z UNREACHABLE (shoulder distance 40/22 mm, min 50); pick (-196,341,-77.18) UNREACHABLE; W and other picks OK. Check the drop in DobotStudio.
- Sim test: pick move OK, old park rejected, J elsewhere rejected, J into the drop rejected when not at W. Drop route cannot run in sim (twin cannot reach it); rules unit-tested with a fake backend.
- Open: SpeedJ/AccJ/SpeedL/AccL on the real cr profile untested; Unity smoke scene coordinates must move to the new polygon.
