"""BuildAI Revit ribbon icon generator.
Each icon has two hand-tuned grids: G32 (viewBox 32, stroke 2) used for 32px and 64px,
and G16 (viewBox 16, stroke 1) used for 16px. Outline = +1px on every side at 1x.
Elements: (layer, role, mode, tag, attrs). role w=white, a=accent. mode s=stroke, f=fill.
Rendering order per layer: outline of all layer elements, then fills/strokes.
"""
import math, os, io, sys
import cairosvg
from PIL import Image

WHITE = "#FFFFFF"
ACCENT = "#3186F6"          # sampled from current icons / logo
VARIANTS = {"dark": "#2B2F36", "blue": "#3186F6"}

def L(x1, y1, x2, y2, role="w", layer=0):
    return (layer, role, "s", "line", dict(x1=x1, y1=y1, x2=x2, y2=y2))
def P(d, role="w", mode="s", layer=0):
    return (layer, role, mode, "path", dict(d=d))
def R(x, y, w, h, rx=0, role="w", mode="s", layer=0):
    return (layer, role, mode, "rect", dict(x=x, y=y, width=w, height=h, rx=rx))
def C(cx, cy, r, role="w", mode="s", layer=0):
    return (layer, role, mode, "circle", dict(cx=cx, cy=cy, r=r))
def PL(pts, role="w", layer=0):
    return (layer, role, "s", "polyline", dict(points=" ".join(f"{x},{y}" for x, y in pts)))

def gear(cx, cy, ro, ri, n, tw_o, tw_i):
    """gear outline path; tw_* = half-angle (deg) of tooth at outer/inner radius"""
    pts = []
    for k in range(n):
        a = 360 / n * k - 90
        for ang, r in ((a - tw_i, ri), (a - tw_o, ro), (a + tw_o, ro), (a + tw_i, ri)):
            t = math.radians(ang)
            pts.append((cx + r * math.cos(t), cy + r * math.sin(t)))
        # valley arc handled by straight segment to next tooth
    d = "M" + " L".join(f"{x:.2f} {y:.2f}" for x, y in pts) + " Z"
    return d

ICONS = {}
CAP = {32: "round", 16: "square"}
JOIN = {32: "round", 16: "miter"}

def refresh(cx, cy, r, role="w", layer=0, head=2.2):
    """open circular arrow, clockwise, gap at top-right; arrowhead as filled triangle"""
    a0, a1 = math.radians(-75), math.radians(200)
    x0, y0 = cx + r*math.cos(a0), cy + r*math.sin(a0)
    x1, y1 = cx + r*math.cos(a1), cy + r*math.sin(a1)
    arc = P(f"M{x0:.2f} {y0:.2f} A{r} {r} 0 1 1 {x1:.2f} {y1:.2f}", role, "s", layer)
    # head at start point (x0,y0), pointing along -tangent of clockwise motion (i.e. counter-clockwise dir)
    tx, ty = math.sin(a0), -math.cos(a0)          # direction of decreasing angle
    nx, ny = math.cos(a0), math.sin(a0)
    tip = (x0 + tx*head*1.1, y0 + ty*head*1.1)
    b1 = (x0 + nx*head - tx*0.2, y0 + ny*head - ty*0.2)
    b2 = (x0 - nx*head - tx*0.2, y0 - ny*head - ty*0.2)
    tri = P(f"M{tip[0]:.2f} {tip[1]:.2f} L{b1[0]:.2f} {b1[1]:.2f} L{b2[0]:.2f} {b2[1]:.2f} Z", role, "f", layer)
    return [arc, tri]

def PX(rows, x0, y0, role="w", layer=2):
    """pixel mask: rows of '#'/'.' strings -> 1x1 filled rects"""
    out = []
    for j, row in enumerate(rows):
        for i, ch in enumerate(row):
            if ch == "#":
                out.append(R(x0 + i, y0 + j, 1, 1, 0, role, "f", layer))
    return out

def badge32(layer=1):
    return C(23.5, 23.5, 6.5, "a", "f", layer)
def badge16(layer=1):
    return R(8, 8, 7, 7, 2, "a", "f", layer)

# ---------------------------------------------------------------- clash
ICONS["clash"] = {
    32: [C(16, 16, 8), L(16, 3, 16, 8), L(16, 24, 16, 29), L(3, 16, 8, 16), L(24, 16, 29, 16),
         P("M16 11.5 L20.5 16 L16 20.5 L11.5 16 Z", "a", "f")],
    16: [C(7.5, 7.5, 3.5), L(7.5, 1.5, 7.5, 2.5), L(7.5, 12.5, 7.5, 13.5), L(1.5, 7.5, 2.5, 7.5), L(12.5, 7.5, 13.5, 7.5),
         R(6, 6, 3, 3, 0, "a", "f")],
}
# ---------------------------------------------------------------- results
ICONS["results"] = {
    32: [R(4, 4, 24, 24, 3), R(9, 17, 4, 7, 0, "a", "f"), R(14, 13, 4, 11, 0, "a", "f"), R(19, 9, 4, 15, 0, "a", "f")],
    16: [R(1.5, 1.5, 12, 12, 1), R(4, 8, 2, 3, 0, "a", "f"), R(7, 6, 2, 5, 0, "a", "f"), R(10, 4, 2, 7, 0, "a", "f")],
}
# ---------------------------------------------------------------- publish (ACC issues)
ICONS["publish"] = {
    32: [P("M5 19 V27 H27 V19"), L(16, 5, 16, 21, "a"), PL([(10, 11), (16, 5), (22, 11)], "a")],
    16: [P("M1.5 9.5 V13.5 H13.5 V9.5"), L(7.5, 3.5, 7.5, 10.5, "a"), PL([(4.5, 5.5), (7.5, 2.5), (10.5, 5.5)], "a")],
}
# ---------------------------------------------------------------- open in BuildAI (cloud)
CLOUD32 = "M9.5 25 H23 A5.5 5.5 0 0 0 23.6 14.03 A7.5 7.5 0 0 0 9.2 14.6 A5.2 5.2 0 0 0 9.5 25 Z"
CLOUD16 = "M4 12.5 H11 A2.5 2.5 0 0 0 11.5 7.55 A4 4 0 0 0 3.8 7.6 A2.45 2.45 0 0 0 4 12.5 Z"
ICONS["open"] = {
    32: [P(CLOUD32), L(16, 22, 16, 15, "a"), PL([(12.5, 18.5), (16, 15), (19.5, 18.5)], "a")],
    16: [P(CLOUD16), P("M7.5 6 L10 8.5 H5 Z", "a", "f"), R(7, 8.5, 1, 2.5, 0, "a", "f")],
}
# ---------------------------------------------------------------- connect (link)
ICONS["connect"] = {
    32: [R(3, 11, 14, 10, 5), R(15, 11, 14, 10, 5), L(11, 16, 21, 16, "a")],
    16: [R(1.5, 4.5, 7, 6, 3), R(6.5, 4.5, 7, 6, 3), L(4.5, 7.5, 10.5, 7.5, "a")],
}
# ---------------------------------------------------------------- check links
ICONS["checklinks"] = {
    32: [R(3, 5, 13, 10, 5), R(14, 5, 13, 10, 5), L(10, 10, 20, 10, "a"),
         badge32(), PL([(20.5, 23.5), (22.5, 25.5), (26.5, 21.5)], "w", 2)],
    16: [R(1.5, 1.5, 7, 6, 3), R(6.5, 1.5, 7, 6, 3), L(4.5, 4.5, 10.5, 4.5, "a"),
         badge16()] + PX(["....#", "...#.", "#.#..", ".#..."], 9, 9),
}
# ---------------------------------------------------------------- changes (doc + delta)
ICONS["changes"] = {
    32: [P("M6 3 H17 L23 9 V29 H6 Z"), P("M17 3 V9 H23"), L(10, 14, 18, 14), L(10, 19, 14, 19), L(10, 24, 13, 24),
         P("M23.5 16.5 L30 28.5 H17 Z", "a", "f", 1)],
    16: [P("M2.5 1.5 H8.5 L11.5 4.5 V14.5 H2.5 Z"), L(4.5, 6.5, 7.5, 6.5), L(4.5, 9.5, 5.5, 9.5),
         P("M11.5 7 L15 14 H8 Z", "a", "f", 1)],
}
# ---------------------------------------------------------------- compare (AR vs ST)
ICONS["compare"] = {
    32: [R(3, 3, 17, 17, 2.5), R(12, 12, 17, 17, 2.5, "a"), R(14, 14, 4, 4, 0, "a", "f")],
    16: [R(1.5, 1.5, 8, 8, 1), R(5.5, 5.5, 8, 8, 1, "a"), R(6, 6, 3, 3, 0, "a", "f")],
}
# ---------------------------------------------------------------- rooms
ICONS["rooms"] = {
    32: [R(4, 4, 24, 24, 2), L(16, 4, 16, 16), L(11, 16, 28, 16), C(19.5, 22, 2.5, "a", "f")],
    16: [R(1.5, 1.5, 12, 12, 1), L(7.5, 1.5, 7.5, 7.5), L(5.5, 7.5, 13.5, 7.5), R(8, 9, 3, 3, 0, "a", "f")],
}
# ---------------------------------------------------------------- settings
ICONS["settings"] = {
    32: [P(gear(16, 16, 12.5, 9.5, 8, 9, 13)), C(16, 16, 3.5, "a", "f")],
    16: [P(gear(7.5, 7.5, 6.5, 4.9, 8, 11, 16), "w", "f"), C(7.5, 7.5, 2.1, "a", "f", 1)],
}
# ---------------------------------------------------------------- recalculate (db + refresh)
ICONS["recalculate"] = {
    32: [P("M4 7.5 A9 3.5 0 0 1 22 7.5 A9 3.5 0 0 1 4 7.5 Z"),
         P("M4 7.5 V23.5 A9 3.5 0 0 0 13 27"), P("M22 7.5 V13"), P("M4 15.5 A9 3.5 0 0 0 14 18.9"),
         badge32()] + refresh(23.5, 23.5, 3.4, "w", 2, 2.6),
    16: [P("M1.5 3.5 A4.5 2 0 0 1 10.5 3.5 A4.5 2 0 0 1 1.5 3.5 Z"), P("M1.5 3.5 V11.5 A4.5 2 0 0 0 6 13.5"),
         P("M10.5 3.5 V6.5"), badge16()] + PX([".##.#", "#..##", "#....", "#...#", ".###."], 9, 9),
}
# ---------------------------------------------------------------- create views / sheets
ICONS["createview"] = {
    32: [R(3, 4, 22, 18, 2), L(3, 16, 25, 16), L(8, 10, 16, 10), badge32(),
         L(23.5, 20.5, 23.5, 26.5, "w", 2), L(20.5, 23.5, 26.5, 23.5, "w", 2)],
    16: [R(1.5, 2.5, 11, 9, 1), L(1.5, 8.5, 12.5, 8.5), badge16()] + PX([".#.", "###", ".#."], 10, 10),
}
# ---------------------------------------------------------------- NEW: support chat
ICONS["support"] = {
    32: [P("M7 5 H25 A4 4 0 0 1 29 9 V19 A4 4 0 0 1 25 23 H15 L9 28 V23 H7 A4 4 0 0 1 3 19 V9 A4 4 0 0 1 7 5 Z"),
         C(10, 14, 2, "a", "f"), C(16, 14, 2, "a", "f"), C(22, 14, 2, "a", "f")],
    16: [P("M3.5 2.5 H12.5 A1 1 0 0 1 13.5 3.5 V9.5 A1 1 0 0 1 12.5 10.5 H7.5 L4.5 13.5 V10.5 H3.5 A1 1 0 0 1 2.5 9.5 V3.5 A1 1 0 0 1 3.5 2.5 Z"),
         R(4, 5, 2, 2, 0, "a", "f"), R(7, 5, 2, 2, 0, "a", "f"), R(10, 5, 2, 2, 0, "a", "f")],
}
# ---------------------------------------------------------------- NEW: reverse sync (ACC -> Revit)
ICONS["syncback"] = {
    32: [P("M9 18 H22 A5 5 0 0 0 22.6 8.03 A7 7 0 0 0 9.1 8.6 A4.7 4.7 0 0 0 9 18 Z"),
         P("M22 14 V21 A4.5 4.5 0 0 1 17.5 25.5 H8", "a", layer=1), PL([(12, 21.5), (8, 25.5), (12, 29.5)], "a", 1)],
    16: [P("M4.5 9.5 H11 A2.5 2.5 0 0 0 11.5 4.55 A3.6 3.6 0 0 0 4.4 4.6 A2.45 2.45 0 0 0 4.5 9.5 Z"),
         P("M11.5 8.5 V12.5 H5.5", "a", layer=1), PL([(7.5, 10.5), (5.5, 12.5), (7.5, 14.5)], "a", 1)],
}

ORDER = ["clash", "results", "createview", "publish", "settings", "checklinks", "changes",
         "compare", "rooms", "recalculate", "open", "connect", "support", "syncback"]

def svg(name, grid, outline, with_outline=True):
    els = ICONS[name][grid]
    sw = 2 if grid == 32 else 1
    ow = 1 if grid == 32 else 1  # outline per side, in grid units (1px at 1x)
    if grid == 32: ow = 1
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{grid}" height="{grid}" viewBox="0 0 {grid} {grid}" '
           f'fill="none" stroke-linecap="{CAP[grid]}" stroke-linejoin="{JOIN[grid]}">']
    layers = sorted(set(e[0] for e in els))
    for ly in layers:
        le = [e for e in els if e[0] == ly]
        if with_outline and ly < 2:
            out.append(f'<g stroke="{outline}">')
            for _, role, mode, tag, a in le:
                attrs = " ".join(f'{k}="{v}"' for k, v in a.items())
                if mode == "s":
                    out.append(f'<{tag} {attrs} stroke-width="{sw + 2*ow}"/>')
                else:
                    out.append(f'<{tag} {attrs} fill="{outline}" stroke-width="{2*ow}"/>')
            out.append('</g>')
        for _, role, mode, tag, a in le:
            col = WHITE if role == "w" else ACCENT
            attrs = " ".join(f'{k}="{v}"' for k, v in a.items())
            if mode == "s":
                out.append(f'<{tag} {attrs} stroke="{col}" stroke-width="{sw}"/>')
            else:
                out.append(f'<{tag} {attrs} fill="{col}"/>')
    out.append('</svg>')
    return "\n".join(out)

def png(name, size, outline, with_outline=True):
    grid = 16 if size == 16 else 32
    data = cairosvg.svg2png(bytestring=svg(name, grid, outline, with_outline).encode(), output_width=size, output_height=size)
    return Image.open(io.BytesIO(data)).convert("RGBA")

if __name__ == "__main__":
    pass
