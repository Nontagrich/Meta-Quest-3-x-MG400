"""Physical MG400 XY limits for the Unity bridge. Coordinates are in mm.

The limits live in workspace.json (next to this file, or the path in the
MG400_WORKSPACE environment variable) and are validated when this module is
imported. An invalid file stops the bridge with a clear message.

workspace.json:
  corners      4+ [x, y] points, convex, counter-clockwise perimeter order
  parkXY       [x, y] inside the corners, MIN..MAX radius
  minRadius / maxRadius
  dropZone     (optional) small area inside MIN_RADIUS that is only reachable
               through the fixed route described in drop_route() below.
"""

import json
import math
import os
import re
import threading
import time

CONFIG_PATH = os.environ.get("MG400_WORKSPACE") or os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "workspace.json")

# Tolerances (mm) used to decide that the arm is "at" W or above the drop point.
ZONE_ROUTE_TOL = 2.0       # XY and Z distance to W / (drop x, y, safe Z) before a joint move
ZONE_TARGET_TOL = 0.5      # joint-move end point must equal W / (drop x, y, safe Z)
ZONE_VERTICAL_TOL = 0.5    # vertical move must keep x, y
ZONE_CENTER_TOL = 1.0      # arm inside the zone must be on the drop axis


class WorkspaceConfigError(ValueError):
    """workspace.json is missing, unreadable or fails validation."""


def _xy(value, name):
    if (not isinstance(value, (list, tuple)) or len(value) != 2
            or any(type(v) not in (int, float) or not math.isfinite(v) for v in value)):
        raise WorkspaceConfigError(f"{name} must be [x, y] with finite numbers, got {value!r}")
    return (float(value[0]), float(value[1]))


def _number(data, key, where, low=None, high=None):
    value = data.get(key)
    if type(value) not in (int, float) or not math.isfinite(value):
        raise WorkspaceConfigError(f"{where}.{key} must be a finite number, got {value!r}")
    if (low is not None and value < low) or (high is not None and value > high):
        raise WorkspaceConfigError(f"{where}.{key} = {value} is outside {low}..{high}")
    return float(value)


def _cross(o, a, b):
    return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])


def _point_segment_distance(point, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    length_sq = dx * dx + dy * dy
    t = max(0.0, min(1.0, ((point[0] - a[0]) * dx + (point[1] - a[1]) * dy) / length_sq)) if length_sq else 0.0
    return math.hypot(point[0] - (a[0] + t * dx), point[1] - (a[1] + t * dy))


def _inside(corners, point):
    n = len(corners)
    return all(_cross(corners[i], corners[(i + 1) % n], point) >= -1e-8 for i in range(n))


def parse_workspace(data):
    """Validate a decoded workspace.json and return the normalised config dict."""
    if not isinstance(data, dict):
        raise WorkspaceConfigError("top level must be a JSON object")
    raw = data.get("corners")
    if not isinstance(raw, list) or not 3 <= len(raw) <= 16:
        raise WorkspaceConfigError("corners must be a list of 3..16 [x, y] points")
    corners = tuple(_xy(p, f"corners[{i}]") for i, p in enumerate(raw))
    n = len(corners)
    if len(set(corners)) != n:
        raise WorkspaceConfigError("corners contains a duplicate point")
    crosses = [_cross(corners[i], corners[(i + 1) % n], corners[(i + 2) % n]) for i in range(n)]
    if all(c < 0 for c in crosses):
        raise WorkspaceConfigError("corners are clockwise; list them counter-clockwise in perimeter order")
    if not all(c > 1e-6 for c in crosses):
        raise WorkspaceConfigError("corners must form a strictly convex polygon in perimeter order "
                                   "(no crossing edges, no collinear or reflex corner)")
    turning = sum(math.atan2(_cross((0, 0), (corners[i][0] - corners[i - 1][0], corners[i][1] - corners[i - 1][1]),
                                    (corners[(i + 1) % n][0] - corners[i][0], corners[(i + 1) % n][1] - corners[i][1])),
                              (corners[i][0] - corners[i - 1][0]) * (corners[(i + 1) % n][0] - corners[i][0])
                              + (corners[i][1] - corners[i - 1][1]) * (corners[(i + 1) % n][1] - corners[i][1]))
                  for i in range(n))
    if abs(turning - 2 * math.pi) > 1e-6:
        raise WorkspaceConfigError("corners wind around more than once; use perimeter order")

    min_r = _number(data, "minRadius", "workspace", 0.0)
    max_r = _number(data, "maxRadius", "workspace", 0.0)
    if not min_r < max_r:
        raise WorkspaceConfigError(f"minRadius {min_r} must be smaller than maxRadius {max_r}")
    park = _xy(data.get("parkXY"), "parkXY")
    if not _inside(corners, park):
        raise WorkspaceConfigError(f"parkXY {park} is outside the corners polygon")
    if not min_r <= math.hypot(*park) <= max_r:
        raise WorkspaceConfigError(f"parkXY radius {math.hypot(*park):.1f} is outside {min_r}..{max_r}")

    drop = None
    raw_drop = data.get("dropZone")
    if raw_drop is not None:
        if not isinstance(raw_drop, dict):
            raise WorkspaceConfigError("dropZone must be an object")
        center = _xy(raw_drop.get("centerXY"), "dropZone.centerXY")
        radius = _number(raw_drop, "radius", "dropZone", 1.0)
        drop_z = _number(raw_drop, "dropZ", "dropZone", -10000, 10000)
        min_z = _number(raw_drop, "minZ", "dropZone", -10000, 10000)
        safe_z = _number(raw_drop, "safeZ", "dropZone", -10000, 10000)
        speed = _number(raw_drop, "maxSpeedPct", "dropZone", 1, 100)
        waypoint = _xy(raw_drop.get("waypointW"), "dropZone.waypointW")
        if not min_z <= drop_z < safe_z:
            raise WorkspaceConfigError(f"dropZone needs minZ ({min_z}) <= dropZ ({drop_z}) < safeZ ({safe_z})")
        if _inside(corners, center) or min(_point_segment_distance(center, corners[i], corners[(i + 1) % n])
                                           for i in range(n)) <= radius:
            raise WorkspaceConfigError("dropZone circle overlaps the corners polygon; the zone must be outside it")
        if not _inside(corners, waypoint):
            raise WorkspaceConfigError(f"dropZone.waypointW {waypoint} is outside the corners polygon")
        if not min_r <= math.hypot(*waypoint) <= max_r:
            raise WorkspaceConfigError(f"dropZone.waypointW radius {math.hypot(*waypoint):.1f} "
                                       f"is outside {min_r}..{max_r}")
        drop = {"center": center, "radius": radius, "drop_z": drop_z, "min_z": min_z,
                "safe_z": safe_z, "speed": int(round(speed)), "waypoint": waypoint}
    return {"corners": corners, "park": park, "min_radius": min_r, "max_radius": max_r, "drop": drop}


def load_workspace(path=None):
    path = path or CONFIG_PATH
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    except FileNotFoundError:
        raise WorkspaceConfigError(f"workspace file not found: {path}")
    except (OSError, json.JSONDecodeError) as exc:
        raise WorkspaceConfigError(f"cannot read {path}: {exc}")
    return parse_workspace(data)


def apply_workspace(config):
    global CORNERS, PARK_XY, MIN_RADIUS, MAX_RADIUS, EDGES, DROP, CENTROID
    CORNERS = config["corners"]
    PARK_XY = config["park"]
    MIN_RADIUS, MAX_RADIUS = config["min_radius"], config["max_radius"]
    EDGES = tuple(zip(CORNERS, CORNERS[1:] + CORNERS[:1]))
    DROP = config["drop"]
    CENTROID = (sum(c[0] for c in CORNERS) / len(CORNERS), sum(c[1] for c in CORNERS) / len(CORNERS))


try:
    apply_workspace(load_workspace())
except WorkspaceConfigError as _error:
    raise SystemExit(f"[workspace] INVALID workspace config ({CONFIG_PATH}): {_error}")


def _in_polygon(point):
    x, y = point
    return all((bx - ax) * (y - ay) - (by - ay) * (x - ax) >= -1e-8
               for (ax, ay), (bx, by) in EDGES)


def contains_xy(point):
    return _in_polygon(point) and MIN_RADIUS <= math.hypot(*point) <= MAX_RADIUS


def _reachable_segment(start, end):
    x, y = start
    dx, dy = end[0] - x, end[1] - y
    length_sq = dx * dx + dy * dy
    closest = max(0.0, min(1.0, -(x * dx + y * dy) / length_sq)) if length_sq else 0.0
    return (math.hypot(x + closest * dx, y + closest * dy) >= MIN_RADIUS
            and max(math.hypot(*start), math.hypot(*end)) <= MAX_RADIUS)


def allows_segment(start, end, *, entering=False):
    # Convex polygon: if both endpoints are inside, so is the straight XY path.
    # The inner reach circle is non-convex and must be checked along the path.
    return ((entering or _in_polygon(start) and _in_polygon(end))
            and _reachable_segment(start, end))


def entry_route(start):
    """Stop a little inside the boundary along a checked linear entry path."""
    candidates = (PARK_XY, (start[0], PARK_XY[1]), (PARK_XY[0], start[1]), CENTROID)
    entries = []
    for inside in candidates:
        if not contains_xy(inside) or not allows_segment(start, inside, entering=True):
            continue
        dx, dy = inside[0] - start[0], inside[1] - start[1]
        travel = math.hypot(dx, dy)
        low, high = 0.0, 1.0
        for _ in range(32):
            middle = (low + high) / 2
            if contains_xy((start[0] + dx * middle, start[1] + dy * middle)):
                high = middle
            else:
                low = middle
        fraction = min(1.0, high + 2.0 / max(travel, 2.0))
        point = (round(start[0] + dx * fraction, 6), round(start[1] + dy * fraction, 6))
        if (contains_xy(point) and allows_segment(start, point, entering=True)):
            entries.append(point)
    if entries:
        return [min(entries, key=lambda point: math.dist(start, point))]
    return []


def limit_segment(start, end):
    """Trim a requested controller step at the first workspace edge."""
    if allows_segment(start, end):
        return end
    dx, dy = end[0] - start[0], end[1] - start[1]
    low, high = 0.0, 1.0
    for _ in range(32):
        middle = (low + high) / 2
        if allows_segment(start, (start[0] + dx * middle, start[1] + dy * middle)):
            low = middle
        else:
            high = middle
    # Keep the rounded wire coordinate on the inside of the boundary.
    fraction = max(0.0, low - 0.002 / max(math.hypot(dx, dy), 0.002))
    return start[0] + dx * fraction, start[1] + dy * fraction


def read_robot_values(backend, command, count):
    ok, reply = backend.command(command)
    if not ok:
        return None
    payload = reply.partition(",")[2].strip().rstrip(";")
    payload = re.sub(r",\s*[A-Za-z_]\w*\(.*\)$", "", payload).strip()
    try:
        fields = [float(value) for value in payload.strip("{}").rstrip(",").split(",")]
    except ValueError:
        return None
    if len(fields) < count or not all(map(math.isfinite, fields)):
        return None
    return fields[:count]


def in_drop_zone(point):
    return DROP is not None and math.dist(point, DROP["center"]) <= DROP["radius"]


def drop_route(pose, target, move_type):
    """Decide how a move relates to the drop zone.

    Returns None when the move never touches the zone (normal workspace rules apply),
    (False, reason) when it is rejected, or (True, target) with the target snapped to the
    configured route point. Rules:
      * entering or leaving: ONLY a joint move (MovJ) between W and (drop x, y, safe Z);
      * inside: ONLY vertical linear moves (same x, y) between safe Z and minZ;
      * anything else that starts, ends or passes through the zone is rejected.
    """
    if DROP is None:
        return None
    center, radius = DROP["center"], DROP["radius"]
    start_in, end_in = in_drop_zone(pose[:2]), in_drop_zone(target[:2])
    if not (start_in or end_in or _point_segment_distance(center, pose[:2], target[:2]) <= radius):
        return None
    safe_z, waypoint = DROP["safe_z"], DROP["waypoint"]
    if start_in and end_in:
        if move_type != "L":
            return False, "Inside the drop zone only vertical linear moves are allowed"
        if (math.dist(pose[:2], center) > ZONE_CENTER_TOL
                or math.dist(pose[:2], target[:2]) > ZONE_VERTICAL_TOL):
            return False, "Inside the drop zone only vertical moves on the drop axis are allowed (x, y unchanged)"
        if not DROP["min_z"] - 1e-6 <= target[2] <= safe_z + 1e-6:
            return False, (f"Drop zone Z {target[2]:.2f} is outside {DROP['min_z']:.2f}..{safe_z:.2f}")
        return True, (center[0], center[1], target[2], target[3])
    at_w = math.dist(pose[:2], waypoint) <= ZONE_ROUTE_TOL and abs(pose[2] - safe_z) <= ZONE_ROUTE_TOL
    above = math.dist(pose[:2], center) <= ZONE_ROUTE_TOL and abs(pose[2] - safe_z) <= ZONE_ROUTE_TOL
    if move_type == "J" and abs(target[2] - safe_z) <= ZONE_TARGET_TOL:
        if at_w and end_in and math.dist(target[:2], center) <= ZONE_TARGET_TOL:
            return True, (center[0], center[1], safe_z, target[3])
        if above and not end_in and math.dist(target[:2], waypoint) <= ZONE_TARGET_TOL:
            return True, (waypoint[0], waypoint[1], safe_z, target[3])
    return False, ("The drop zone is entered or left only by a joint (J) move between W "
                   f"({waypoint[0]:.2f}, {waypoint[1]:.2f}, Z {safe_z:.2f}) and the drop axis at safe Z; "
                   "the arm must be at the start of that route")


class WorkspaceMoves:
    """Single in-flight move shared by Panel, packing and controller sessions."""

    def __init__(self, backend):
        self.backend = backend
        self.pending = None
        self.abort = threading.Event()   # set by the bridge E-STOP; ends wait() at once

    @staticmethod
    def arrived(pose, target):
        return (pose is not None and math.dist(pose[:3], target[:3]) < 0.2
                and abs(pose[3] - target[3]) <= 0.2)

    def settled(self, pose):
        if self.pending is None:
            return True
        if not self.arrived(pose, self.pending):
            return False
        self.pending = None
        return True

    def ready_pose(self):
        if read_robot_values(self.backend, "RobotMode()", 1) != [5.0]:
            return None, "Robot must be enabled and idle"
        pose = read_robot_values(self.backend, "GetPose()", 4)
        if pose is None:
            return None, "Robot pose unavailable"
        if not self.settled(pose):
            return None, "Previous motion is unconfirmed. Check the robot and reopen Bridge before retrying."
        return pose, ""

    def send(self, pose, target, *, entering=False, options="CP=0", move_type="L"):
        target = tuple(round(value, 6) for value in target)
        route = drop_route(pose, target, move_type)
        if route is not None:
            if not route[0]:
                return False, "Workspace limit: " + route[1]
            target = tuple(round(value, 6) for value in route[1])
            pct = DROP["speed"]
            verb = "MovJ" if move_type == "J" else "MovL"
            options = f"SpeedJ={pct},AccJ={pct}" if move_type == "J" else f"SpeedL={pct},AccL={pct},CP=0"
        elif move_type != "L":
            return False, "Joint (J) moves are only allowed for the drop-zone route between W and the drop axis"
        else:
            verb = "MovL"
            if not allows_segment(pose[:2], target[:2], entering=entering):
                return False, "Workspace limit: XY path starts outside or crosses the four-point area or its reach limits"
        if not self.settled(pose):
            return False, "Previous motion is still unconfirmed"
        x, y, z, r = target
        ok, reply = self.backend.command(f"{verb}({x:.6f},{y:.6f},{z:.6f},{r:.6f},{options})")
        if ok or reply.startswith("conn-error:"):
            # An acknowledgement lost after send is not safe to retry blindly.
            self.pending = target
        return ok, reply

    def move(self, msg, *, park=False):
        move_type = "L" if park else msg.get("type", "L")
        if move_type not in ("L", "J"):
            return False, "Move type must be L (linear) or J (joint)"
        pose, reason = self.ready_pose()
        if pose is None:
            return False, reason
        if park:
            target = (*PARK_XY, *pose[2:])
        else:
            target = tuple(msg.get(key) for key in ("x", "y", "z", "r"))
            if any(type(value) not in (float, int) or not -10000 <= value <= 10000 for value in target):
                return False, "Invalid Cartesian target"
            if msg.get("preserveR") is True:
                target = (*target[:3], pose[3])
        return self.send(pose, target, move_type=move_type)

    def check_path(self, points):
        if not isinstance(points, list) or not 1 <= len(points) <= 64:
            return False, "Invalid packing path"
        pose, reason = self.ready_pose()
        if pose is None:
            return False, reason
        previous = pose[:2]
        if not contains_xy(previous):
            return False, "Current XY is outside the workspace. Use T to enter before packing."
        for index, point in enumerate(points, 1):
            if not isinstance(point, dict):
                return False, f"Invalid packing waypoint {index}"
            target = PARK_XY if point.get("park") is True else (point.get("x"), point.get("y"))
            if any(type(v) not in (float, int) or not -10000 <= v <= 10000 for v in target):
                return False, f"Invalid packing waypoint {index}"
            rounded_target = tuple(round(value, 6) for value in target)
            if not allows_segment(previous, rounded_target):
                x, y = target
                return False, f"Packing waypoint {index} (X {x:.1f}, Y {y:.1f} mm) leaves the workspace"
            previous = rounded_target
        return True, "Packing XY path is inside the configured workspace"

    def wait(self, timeout=18.0):
        target = self.pending
        if target is None:
            return True, "No unconfirmed move"
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.abort.is_set():
                return False, "E-STOP: wait aborted"
            mode = read_robot_values(self.backend, "RobotMode()", 1)
            if mode not in ([5.0], [7.0]):
                return False, "Robot stopped or status unavailable while waiting for arrival"
            if mode == [5.0] and self.arrived(read_robot_values(self.backend, "GetPose()", 4), target):
                return True, "Move reached target"
            time.sleep(0.1)
        return False, "Move arrival not confirmed"
