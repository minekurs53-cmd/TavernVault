# -*- coding: utf-8 -*-
"""TavernVault 应用图标生成器 —— 单一几何源,同时产出 .ico / favicon.svg / favicon.png

设计:暗色圆角瓷片 + 圆底烧瓶(酒馆·药剂意象) + 靛蓝药液(主题色 #4c6ef5 系) + 锁孔(保险库意象)。
小尺寸(<=48)自动省略锁孔与气泡,保证 16px 依然可读。

用法:  python tools/gen_icon.py
输出:  src/TavernVault.App/Assets/app.ico        (256/128/64/48/32/24/16,PNG 帧)
       src/TavernVault.App/wwwroot/favicon.svg
       src/TavernVault.App/wwwroot/favicon.png   (32px)
预览:  %TEMP%/tv_icon_preview.png                (浅色/深色底上的多尺寸拼图,仅自检用)
"""
import io
import os
import struct

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = 512   # 设计坐标系
SS = 4    # 超采样倍率(Pillow 无矢量抗锯齿,先放大绘制再 LANCZOS 缩小)

# ---- 调色板(与应用前端主题对齐) ----
TILE_TOP = (35, 40, 52)      # 瓷片渐变顶 #232834
TILE_BOT = (20, 23, 30)      # 瓷片渐变底 #14171e
GLASS = (43, 48, 62)         # 玻璃瓶身 #2b303e
OUTLINE = (154, 166, 196)    # 轮廓 #9aa6c4
LIQ_TOP = (111, 138, 255)    # 药液渐变顶 #6f8aff
LIQ_BOT = (58, 85, 214)      # 药液渐变底 #3a55d6
BUBBLE = (185, 198, 255)     # 气泡 #b9c6ff
CORK_COL = (201, 154, 99)    # 软木塞 #c99a63
KEYHOLE = (18, 21, 28)       # 锁孔(瓷片底色,镂空感) #12151c

# ---- 几何(512 坐标系) ----
TILE = (16, 16, 496, 496, 116)          # 圆角瓷片 (x0,y0,x1,y1,r)
LIP = (214, 84, 298, 108, 12)           # 瓶口沿 (x0,y0,x1,y1,r)
NECK = (226, 100, 286, 340)             # 瓶颈 (x0,y0,x1,y1),下端没入瓶身
BODY = (256, 320, 132)                  # 瓶身圆 (cx,cy,r)
CORK = (224, 54, 288, 90, 10)           # 软木塞 (x0,y0,x1,y1,r)
LIQ_Y = 310                             # 液面高度
KEY_R = 23                              # 锁孔圆半径
KEY_CY = 338                            # 锁孔圆心 y
KEY_W = ((248, 350), (264, 350), (259, 390), (253, 390))  # 锁孔楔形
BUBBLES = ((227, 370, 11), (290, 345, 7))
GROW = 4                                # 轮廓宽度:轮廓层按此外扩量绘制,填充层盖住内半圈

ICO_SIZES = ((256, True), (128, True), (64, True), (48, False), (32, False), (24, False), (16, False))


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def vgrad(w, h, top, bot):
    """竖向线性渐变图。"""
    col = Image.new("RGB", (1, h))
    px = col.load()
    for y in range(h):
        px[0, y] = lerp(top, bot, y / (h - 1))
    return col.resize((w, h))


def render(size, detail=True):
    """按设计坐标渲染一帧,返回 size x size RGBA。"""
    k = size * SS / S
    W = size * SS
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def sc(*vals):
        return [v * k for v in vals]

    # 瓷片
    tile_mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(tile_mask).rounded_rectangle(sc(*TILE[:4]), radius=TILE[4] * k, fill=255)
    img.paste(vgrad(W, W, TILE_TOP, TILE_BOT), (0, 0), tile_mask)

    # 统一轮廓的技巧:先画外扩 GROW 的实心轮廓层,再画正常尺寸填充层盖掉内半圈
    def glass(draw, grow, color):
        cx, cy, r = BODY
        draw.ellipse(sc(cx - r - grow, cy - r - grow, cx + r + grow, cy + r + grow), fill=color)
        draw.rectangle(sc(NECK[0] - grow, NECK[1] - grow, NECK[2] + grow, NECK[3] + grow), fill=color)
        draw.rounded_rectangle(sc(LIP[0] - grow, LIP[1] - grow, LIP[2] + grow, LIP[3] + grow),
                               radius=(LIP[4] + grow) * k, fill=color)

    glass(d, GROW, OUTLINE)
    glass(d, 0, GLASS)

    # 药液:瓶身圆裁去液面以上部分,填竖向渐变
    lm = Image.new("L", (W, W), 0)
    dl = ImageDraw.Draw(lm)
    cx, cy, r = BODY
    dl.ellipse(sc(cx - r, cy - r, cx + r, cy + r), fill=255)
    dl.rectangle(sc(0, 0, S, LIQ_Y), fill=0)
    img.paste(vgrad(W, W, LIQ_TOP, LIQ_BOT), (0, 0), lm)

    # 锁孔与气泡:小尺寸省略
    if detail:
        d.ellipse(sc(256 - KEY_R, KEY_CY - KEY_R, 256 + KEY_R, KEY_CY + KEY_R), fill=KEYHOLE)
        d.polygon([sc(x, y)[0:2] for x, y in KEY_W], fill=KEYHOLE)
        for bx, by, br in BUBBLES:
            d.ellipse(sc(bx - br, by - br, bx + br, by + br), fill=BUBBLE)

    # 软木塞(自带一圈轮廓,盖在瓶口沿上方)
    d.rounded_rectangle(sc(CORK[0] - GROW, CORK[1] - GROW, CORK[2] + GROW, CORK[3] + GROW),
                        radius=(CORK[4] + GROW) * k, fill=OUTLINE)
    d.rounded_rectangle(sc(*CORK[:4]), radius=CORK[4] * k, fill=CORK_COL)

    return img.resize((size, size), Image.Resampling.LANCZOS)


def build_ico(frames):
    """手工组装 ICO 容器,内嵌 PNG 帧(Vista+ 标准做法)。"""
    blobs, entries, offset = [], [], 6 + 16 * len(frames)
    for size, im in frames:
        b = io.BytesIO()
        im.save(b, "PNG")
        data = b.getvalue()
        entries.append((size if size < 256 else 0, len(data), offset))
        blobs.append(data)
        offset += len(data)
    out = struct.pack("<HHH", 0, 1, len(frames))
    for w, n, off in entries:
        out += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, n, off)
    return out + b"".join(blobs)


def emit_svg():
    """与 render() 同一套几何的矢量版,供网页 favicon 与 README 使用。"""
    cx, cy, r = BODY
    g = GROW
    ring = (
        f'<circle cx="{cx}" cy="{cy}" r="{r + g}"/>'
        f'<rect x="{NECK[0] - g}" y="{NECK[1] - g}" width="{NECK[2] - NECK[0] + 2 * g}" height="{NECK[3] - NECK[1] + 2 * g}"/>'
        f'<rect x="{LIP[0] - g}" y="{LIP[1] - g}" width="{LIP[2] - LIP[0] + 2 * g}" height="{LIP[3] - LIP[1] + 2 * g}" rx="{LIP[4] + g}"/>'
    )
    fill = (
        f'<circle cx="{cx}" cy="{cy}" r="{r}"/>'
        f'<rect x="{NECK[0]}" y="{NECK[1]}" width="{NECK[2] - NECK[0]}" height="{NECK[3] - NECK[1]}"/>'
        f'<rect x="{LIP[0]}" y="{LIP[1]}" width="{LIP[2] - LIP[0]}" height="{LIP[3] - LIP[1]}" rx="{LIP[4]}"/>'
    )
    kw = " ".join(f"{x},{y}" for x, y in KEY_W)
    bubbles = "".join(f'<circle cx="{x}" cy="{y}" r="{rr}"/>' for x, y, rr in BUBBLES)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#232834"/><stop offset="1" stop-color="#14171e"/>
    </linearGradient>
    <linearGradient id="liq" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#6f8aff"/><stop offset="1" stop-color="#3a55d6"/>
    </linearGradient>
    <clipPath id="liqclip"><rect x="{cx - r - 2}" y="{LIQ_Y}" width="{2 * r + 4}" height="{LIQ_Y + 160}"/></clipPath>
  </defs>
  <rect x="{TILE[0]}" y="{TILE[1]}" width="{TILE[2] - TILE[0]}" height="{TILE[3] - TILE[1]}" rx="{TILE[4]}" fill="url(#tile)"/>
  <g fill="#9aa6c4">{ring}</g>
  <g fill="#2b303e">{fill}</g>
  <g clip-path="url(#liqclip)"><circle cx="{cx}" cy="{cy}" r="{r}" fill="url(#liq)"/></g>
  <circle cx="256" cy="{KEY_CY}" r="{KEY_R}" fill="#12151c"/>
  <polygon points="{kw}" fill="#12151c"/>
  <g fill="#b9c6ff">{bubbles}</g>
  <rect x="{CORK[0] - g}" y="{CORK[1] - g}" width="{CORK[2] - CORK[0] + 2 * g}" height="{CORK[3] - CORK[1] + 2 * g}" rx="{CORK[4] + g}" fill="#9aa6c4"/>
  <rect x="{CORK[0]}" y="{CORK[1]}" width="{CORK[2] - CORK[0]}" height="{CORK[3] - CORK[1]}" rx="{CORK[4]}" fill="#c99a63"/>
</svg>
'''


def main():
    ico_path = os.path.join(ROOT, "src", "TavernVault.App", "Assets", "app.ico")
    svg_path = os.path.join(ROOT, "src", "TavernVault.App", "wwwroot", "favicon.svg")
    png_path = os.path.join(ROOT, "src", "TavernVault.App", "wwwroot", "favicon.png")

    os.makedirs(os.path.dirname(ico_path), exist_ok=True)
    frames = [(s, render(s, detail)) for s, detail in ICO_SIZES]
    with open(ico_path, "wb") as f:
        f.write(build_ico(frames))
    with open(svg_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(emit_svg())
    render(32, detail=False).save(png_path, "PNG")

    # 自检拼图:浅色/深色两种底色,覆盖各关键尺寸
    sheet = Image.new("RGB", (900, 620), (245, 246, 248))
    dark = Image.new("RGB", (900, 300), (16, 18, 20))
    sheet.paste(dark, (0, 320))
    x = 30
    for size, _ in ICO_SIZES:
        im = next(i for s, i in frames if s == size)
        y = 310 - size - 10
        sheet.paste(im, (x, y), im)
        sheet.paste(im, (x, 620 - size - 10), im)
        x += size + 28
    sheet.save(os.path.join(os.environ.get("TEMP", "/tmp"), "tv_icon_preview.png"), "PNG")
    print("icon assets written:", ico_path, svg_path, png_path, sep="\n  ")


if __name__ == "__main__":
    main()
