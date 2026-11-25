"""Compute a TEMPORARY workspace.json from taught pick poses (stdlib only).

    python make_workspace.py                       # uses the built-in pick range (4 extreme poses)
    python make_workspace.py --picks picks.json    # picks.json = [[x, y], ...] or [[x, y, z, r], ...]
    python make_workspace.py --margin 25 --out workspace.json

Corners = smallest-area ENCLOSING RECTANGLE (a convex quadrilateral) of the picks grown by --margin.
The values are computed, NOT measured: replace them with measured corners when available.
Also prints the reachability report (see reachability_report) for the drop pose, W and the picks.
"""

import argparse
import json
import math
import os

from mg400_driver import ik

# Pick range reported for poses #1..#9 (X -114..-196, Y 263..341). The individual poses are not in the
# repository, so the four extreme combinations stand in for them: the enclosing rectangle is the same.
DEFAULT_PICKS = [(-114.0, 263.0), (-196.0, 263.0), (-196.0, 341.0), (-114.0, 341.0)]
PICK_Z = -77.18
DROP_POSE = (5.36, 32.36, 90.58, -53.26)
SAFE_Z = 152.03
DROP_RADIUS = 30.0
DROP_MAX_SPEED = 10
MIN_RADIUS, MAX_RADIUS = 220.0, 440.0


def convex_hull(points):
    pts = sorted(set(points))
    if len(pts) < 3:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def grown_points(points, margin, steps=32):
    return [(x + margin * math.cos(2 * math.pi * i / steps), y + margin * math.sin(2 * math.pi * i / steps))
            for x, y in points for i in range(steps)]


def min_area_rectangle(points):
    """Smallest-area enclosing rectangle; one side is flush with a hull edge. CCW corners."""
    hull = convex_hull(points)
    best = None
    for i in range(len(hull)):
        ax, ay = hull[i]
        bx, by = hull[(i + 1) % len(hull)]
        length = math.hypot(bx - ax, by - ay)
        ux, uy = (bx - ax) / length, (by - ay) / length
        vx, vy = -uy, ux
        us = [(px - ax) * ux + (py - ay) * uy for px, py in hull]
        vs = [(px - ax) * vx + (py - ay) * vy for px, py in hull]
        area = (max(us) - min(us)) * (max(vs) - min(vs))
        if best is None or area < best[0] - 1e-9:
            best = (area, ax, ay, ux, uy, vx, vy, min(us), max(us), min(vs), max(vs))
    _, ax, ay, ux, uy, vx, vy, u0, u1, v0, v1 = best
    return [(ax + u * ux + v * vx, ay + u * uy + v * vy) for u, v in ((u0, v0), (u1, v0), (u1, v1), (u0, v1))]


def closest_on_polygon(poly, point):
    best = None
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        dx, dy = b[0] - a[0], b[1] - a[1]
        t = max(0.0, min(1.0, ((point[0] - a[0]) * dx + (point[1] - a[1]) * dy) / (dx * dx + dy * dy)))
        c = (a[0] + t * dx, a[1] + t * dy)
        d = math.dist(c, point)
        if best is None or d < best[0]:
            best = (d, c)
    return best[1]


def inset_point(poly, point, depth):
    """Move a boundary point `depth` mm toward the polygon centroid."""
    cx = sum(p[0] for p in poly) / len(poly)
    cy = sum(p[1] for p in poly) / len(poly)
    d = math.dist(point, (cx, cy))
    return (point[0] + (cx - point[0]) * depth / d, point[1] + (cy - point[1]) * depth / d)


def real_ik(x, y, z):
    """Reachability with the twin model of mg400_driver: twin x = real Y, twin up = real Z, twin z = real X."""
    return ik(y, z, x)


def reachability_report(drop, safe_z, waypoint, picks, pick_z):
    lines = []
    probes = [("drop pose", (drop[0], drop[1], drop[2])), ("drop axis at safe Z", (drop[0], drop[1], safe_z)),
              ("W at safe Z", (waypoint[0], waypoint[1], safe_z))]
    probes += [(f"pick ({p[0]:.0f}, {p[1]:.0f}) at Z {pick_z}", (p[0], p[1], pick_z)) for p in picks]
    for name, (x, y, z) in probes:
        r = math.hypot(x, y)
        try:
            j1, j2, j3 = real_ik(x, y, z)
            lines.append(f"  {name:36s} r={r:6.1f}  OK  j1={j1:7.1f} j2={j2:7.1f} j3={j3:7.1f}")
        except ValueError as exc:
            lines.append(f"  {name:36s} r={r:6.1f}  UNREACHABLE in twin model: {exc}")
    return lines


def build(picks, margin, drop, safe_z, drop_radius, speed):
    rect = min_area_rectangle(grown_points(picks, margin))
    rect = [(round(x, 2), round(y, 2)) for x, y in rect]
    # counter-clockwise check
    area2 = sum(rect[i][0] * rect[(i + 1) % 4][1] - rect[(i + 1) % 4][0] * rect[i][1] for i in range(4))
    if area2 < 0:
        rect.reverse()
    nearest = closest_on_polygon(rect, drop[:2])
    waypoint = inset_point(rect, nearest, 10.0)
    waypoint = (round(waypoint[0], 2), round(waypoint[1], 2))
    park = (round(sum(p[0] for p in rect) / 4, 2), round(sum(p[1] for p in rect) / 4, 2))
    return {
        "_status": "TEMPORARY - computed, not measured. Replace corners/parkXY/dropZone with measured values.",
        "_corners_note": f"TEMPORARY - computed, not measured: smallest enclosing rectangle of the pick poses + {margin} mm margin.",
        "corners": [list(p) for p in rect],
        "_park_note": "TEMPORARY - computed, not measured: polygon centre.",
        "parkXY": list(park),
        "minRadius": MIN_RADIUS,
        "maxRadius": MAX_RADIUS,
        "dropZone": {
            "_note": "TEMPORARY - computed, not measured. Drop pose is fixed (cannot be moved); W is computed.",
            "centerXY": [drop[0], drop[1]],
            "radius": drop_radius,
            "dropZ": drop[2],
            "minZ": round(drop[2] - 5.0, 2),
            "safeZ": safe_z,
            "maxSpeedPct": speed,
            "waypointW": list(waypoint),
        },
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--picks", help="JSON list of [x, y(, z, r)] pick poses")
    parser.add_argument("--margin", type=float, default=25.0)
    parser.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "workspace.json"))
    parser.add_argument("--dry-run", action="store_true", help="print only, do not write the file")
    args = parser.parse_args()
    picks = DEFAULT_PICKS
    if args.picks:
        with open(args.picks, "r", encoding="utf-8") as handle:
            picks = [tuple(float(v) for v in p[:2]) for p in json.load(handle)]
    config = build(picks, args.margin, DROP_POSE, SAFE_Z, DROP_RADIUS, DROP_MAX_SPEED)
    text = json.dumps(config, indent=2)
    print(text)
    print("\nReachability (twin model, NOT calibrated to the real arm):")
    print("\n".join(reachability_report(DROP_POSE, SAFE_Z, tuple(config["dropZone"]["waypointW"]), picks, PICK_Z)))
    if not args.dry_run:
        with open(args.out, "w", encoding="utf-8") as handle:
            handle.write(text + "\n")
        print(f"\nwrote {args.out}")


if __name__ == "__main__":
    main()
