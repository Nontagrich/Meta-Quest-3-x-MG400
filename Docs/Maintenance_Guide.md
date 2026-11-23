# Maintenance mode — what to do and what you can do

Maintenance is the technician mode of Gem Store AR: calibrate the robot arm against the real table, then steer the arm with your
right hand (hand-follow teleoperation), mark a gem position, and run Pick / Place macros. Everything is in the scene `Maintenance`.

> Status: tested in the Unity Editor (Play Mode) with the mock robot server. Real grip / trigger / A / B / X / Y presses from the
> XR Interaction Simulator were **not** injected in the automatic test (test hooks were used instead). **Not yet tested on a real
> Quest 3 or a real robot.** Test slowly with the robot clear of people and objects first.

## 1. Before you start

1. The robot (or the mock server) must be reachable by TCP.
   - Mock server on the PC: `python Tools/mock_robot_server.py` (port 5005).
   - Default address comes from the `RobotLinkConfig` asset (`127.0.0.1:5005`). On a Quest it must be the LAN IP of the robot PC.
   - You can change the address inside the headset (section 5).
2. Press **Maintenance** in the Mode Select menu (or open `Maintenance.unity` in the Editor).
3. Panels appear in front of you:

| Panel | Where | Purpose |
|---|---|---|
| **Teleoperation** (HUD) | left | live status |
| **Robot Calibration** | centre | calibrate, **Back** to Mode Select |
| **Actions** | below centre | Mode, Move volume, Mark gem, Clear marker, Pick, Place |
| **Robot address** | right-far | IP:port, Edit keypad |
| **E-STOP** | right | big red stop + Reset |

## 2. Steps in order

### Step 1 — Connection
HUD → `Robot link` must be **connected** (green). Otherwise teleop is blocked (`Blocked: robot not connected`).

### Step 2 — Calibrate (required, once per setup)
Three physical reference points on the table tie the headset world to robot coordinates (defaults in `TeleopConfig`:
P0 = 200,0,0, P1 = 300,0,0, P2 = 200,100,0 mm).

1. **Robot Calibration** panel → **Start calibration**.
2. Touch **P0** with the tip of the right controller / fingertip and press the **grip / pinch**.
3. Repeat for **P1** and **P2**.
4. Review: fit error per point in mm. ≤ 10 mm → **Accept**; larger → **Redo**. Collinear points are rejected.
5. Result is saved on the device. Editor shortcut (Play Mode): menu **GemStore → Teleop → Fake Calibration**.

### Step 3 — Control volume (OFFSET mode, default and safe)
After calibration a **floating copy of the robot workspace box** (orange wireframe, 1 m = 1000 mm) appears about 0.5 m in front of you
at waist height, with the same axes as the robot. Your hand inside this box maps 1:1 to the same point in the real workspace, so your
hand is never near the real robot. Inside the volume you see a mini target, the robot pose marker and the gem marker.

- If the volume would be less than 0.4 m from the real workspace it is placed to your side automatically; HUD warns if it is still close.
- **Move volume** button: unlocks a grab handle. Grab it (grip) and drag to reposition, then press **Lock volume**.
  Teleop is blocked while unlocked.

### Step 4 — Steer the arm
| Control | Action |
|---|---|
| Hold **right grip / pinch** (deadman) | the robot follows your hand inside the volume |
| Release | robot stops at once (`enable:false`) |
| **Left grip** or **A** (right controller) held | **PRECISION**: hand motion x0.3 around where it started (HUD `PRECISION`). Keyboard `V` in the simulator |
| **Right or left trigger** | gripper / suction on ↔ off |
| **B** (right controller) | E-STOP |

**ABSOLUTE mode** (Actions → Mode button): the robot goes to the real position of your hand. Speed is capped at **30 mm/s**
and the HUD shows **"Keep hands clear of the robot"**. Use only when needed; switch back to OFFSET afterwards.

Speed limits (Unity side): OFFSET 100 mm/s, ABSOLUTE 30 mm/s. The commanded target moves toward your hand at most that fast, starting
from the robot's last reported pose.

### Step 5 — Mark a gem (position helper)
1. Press **Mark gem** (or it toggles to **Cancel marking**).
2. Aim the **right ray** at the calibrated table; a white reticle shows the hit point. Pull the **trigger** to place the marker.
3. A gold diamond appears at the real location and in the control volume. HUD shows `Gem mm: x y z` in robot coordinates
   (z = table height). It turns a warning if the point is outside the workspace.
4. **Clear marker** removes it; press **Mark gem** again to re-mark.

Use it to move the hand to the gem: put your hand on the volume marker, hold the grip, the arm goes above the real gem.

### Step 6 — Pick / Place macros
Hold the grip (deadman), then press **Pick** / **Place** (poke or ray), or controller **X = Pick**, **Y = Place** (keys `P` / `L` in the simulator).
If the grip is not held the request waits up to 10 s for it. Needs fresh `robot_pose` data.

| Macro | Sequence (x, y stay at the current position) |
|---|---|
| **Pick** | descend to `pickZ` → suction **ON** → wait 0.5 s → lift to `safeZ` |
| **Place** | descend to `placeZ` → suction **OFF** → wait 0.5 s → lift to `safeZ` |

Each step waits until the robot reports it is within 3 mm (6 s timeout). HUD shows the step. At the end Unity sends `enable:false`;
press the grip again to continue manual control.

**Abort:** release the deadman (robot holds) or press **E-STOP** (B or red button). The sequence stops and is not resumed.

## 3. What the HUD shows

`Robot link` · `Calibrated` · `Mode` · `Deadman` · `Precision` · `Target mm` · `Robot mm` · `Gripper` · `Gem mm` · `E-STOP` ·
macro status / marking message · warnings (absolute mode, volume too close, target limited) · red `Blocked: ...` line:

| Blocked reason | Meaning / fix |
|---|---|
| not calibrated | do Step 2 |
| calibration in progress | accept or cancel the calibration |
| robot not connected | check server / network / address |
| hand tracking lost | bring the controller / hand into view |
| E-STOP active | press **Reset** |
| no control volume | calibrate first |
| control volume unlocked (lock it) | press **Lock volume** |

## 4. Safety behaviour

- Nothing is sent before calibration is done.
- The robot only moves while the deadman is held.
- **Automatic stop** (`enable:false`) on: tracking lost, link drop, app focus lost. Afterwards release and press the grip again.
- **E-STOP**: sends `estop`, aborts any macro, locks teleop. Make the area safe → **Reset** → release and press grip again.
- Targets are slew-limited, smoothed (One Euro) and clamped to the workspace box. The robot must still enforce its own limits.
- Keep hands and objects out of the real workspace whenever possible; prefer OFFSET mode.
- The calibration is stored relative to the headset's tracking space (not Unity world space), so it survives an app restart and any
  movement of the XR Origin. Maintenance never recenters by itself. A **system recenter** (long-press the Meta button) moves the tracking
  space: the Calibration panel then says "Headset was recentered: check the axes"; check the overlay against the table and recalibrate
  if it is off. Avoid recentering in Maintenance.

## 5. Robot address (IP:port) in the headset

The **Robot address** panel (Maintenance, and on Mode Select) shows `IP:port` and the link state.
1. **Edit** → numeric keypad (digits, `.`, `:`, backspace).
2. **Apply** (invalid IPv4 or port outside 1–65535 shows an error and nothing changes) → saved to PlayerPrefs on the device and the
   link reconnects at once. If the port is omitted the config default is used.
3. **Cancel** leaves it unchanged. A saved address overrides the `RobotLinkConfig` asset. On Mode Select there is no live link,
   so the address is only saved and used when Maintenance / Storefront starts.

## 6. Settings (asset `Assets/_Project/Data/TeleopConfig.asset`)

| Setting | Default | Meaning |
|---|---|---|
| p0 / p1 / p2 | (200,0,0) (300,0,0) (200,100,0) | calibration reference points, mm |
| xMin..zMax | 100..400, −150..150, 0..150 | workspace box, mm |
| sendRateHz / minMoveMm | 10 / 5 | send rate / minimum move |
| smoothingTime / Beta / DerivativeCutoff | 0.15 / 0.02 / 1 | One Euro filter |
| maxSpeedMmPerSec | 100 | OFFSET speed cap |
| absoluteMaxSpeedMmPerSec | 30 | ABSOLUTE speed cap |
| precisionScale | 0.3 | precision hand-motion scale |
| controlVolumeDistance | 0.5 | m in front of the user |
| tableZ | 0 | table height in robot z, mm |
| pickZ / placeZ / safeZ | 5 / 5 / 80 | macro heights, mm |
| gripWaitSeconds | 0.5 | wait after grip change |

## 7. Testing without the robot or headset

1. `python Tools/mock_robot_server.py` (`--max-speed 100`, `--pose-hz 10`). It prints `[TELEOP]`, `[GRIP] ... ON/OFF`, `[ESTOP]`.
2. Play `Maintenance.unity`, then **GemStore → Teleop → Fake Calibration**.
3. In the XR Interaction Simulator: hold the right-controller key + grip and move; `B` = E-STOP, `V` precision, `P`/`L` macros.
4. Change the address with the Edit keypad; run a second mock on another port to see the reconnect.

Protocol details: `Docs/Robot_TCP_Interface.md`.
