# -*- coding: utf-8 -*-
"""TavernVault 应用图标生成器 —— 单一几何源,同时产出 .ico / favicon.svg / favicon.png

设计(v0.7.9,明亮简约风):浅灰白瓷片 + 翠绿文件夹(资源管理) + 内叠家卡片探出
(角色卡/世界书/预设三类核心资源)。翠绿四层阶:深 #0ca678 后板 → 主 #10b981 前板 →
白卡片 + 浅绿 #a7f3d0 卡片。小尺寸(<=48)自动省略后卡与文本行,保证 16px 可读。

用法:  python tools/gen_icon.py
输出:  src/TavernVault.App/Assets/app.ico        (256/128/64/48/32/24/16,PNG 帧)
       src/TavernVault.App/wwwroot/favicon.svg
       src/TavernVault.App/wwwroot/favicon.png   (32px)
预览:  %TEMP%/tv_icon_preview.png                (深浅底多尺寸拼图,仅自检用)
"""
import io
import os
import struct

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = 512   # 设计坐标系
SS = 4    # 超采样倍率(Pillow 无矢量抗锯齿,先放大绘制再 LANCZOS 缩小)

# ---- 调色板(明亮简约·翠绿层阶) ----
TILE_TOP = (247, 248, 250)   # 瓷片渐变顶 #f7f8fa
TILE_BOT = (238, 241, 245)   # 瓷片渐变底 #eef1f5
G_BACK = (12, 166, 120)      # 文件夹后板/标签耳/文本行 #0ca678
G_FRONT = (16, 185, 129)     # 文件夹前板(主色) #10b981
G_PALE = (167, 243, 208)     # 后卡片 #a7f3d0
WHITE = (255, 255, 255)      # 前卡片

# ---- 几何(512 坐标系) ----
TILE = (16, 16, 496, 496, 116)       # 圆角瓷片 (x0,y0,x1,y1,r)
TAB = (112, 150, 232, 212, 22)       # 文件夹标签耳
BACK = (112, 176, 400, 380, 28)      # 文件夹后板
CARD_A = (158, 118, 318, 262, 20)    # 后卡片(浅绿,左上错位)
CARD_B = (206, 142, 366, 286, 20)    # 前卡片(白,右下错位)
LINE_1 = (238, 170, 338, 188, 9)     # 卡片文本行 1
LINE_2 = (238, 202, 318, 220, 9)     # 卡片文本行 2
FRONT = (112, 224, 400, 392, 26)     # 文件夹前板(盖住卡片下部)

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
    """按设计坐标渲染一帧,返回 size x size RGBA。detail=False 为小尺寸简化档。"""
    k = size * SS / S
    W = size * SS
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def sc(*vals):
        return [v * k for v in vals]

    def rrect(geo, fill):
        d.rounded_rectangle(sc(*geo[:4]), radius=geo[4] * k, fill=fill)

    # 瓷片
    tile_mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(tile_mask).rounded_rectangle(sc(*TILE[:4]), radius=TILE[4] * k, fill=255)
    img.paste(vgrad(W, W, TILE_TOP, TILE_BOT), (0, 0), tile_mask)

    # 文件夹后板 + 标签耳(同色连体)
    rrect(TAB, G_BACK)
    rrect(BACK, G_BACK)

    # 内叠卡片:后卡(浅绿) → 前卡(白) + 文本行
    if detail:
        rrect(CARD_A, G_PALE)
    rrect(CARD_B, WHITE)
    if detail:
        rrect(LINE_1, G_BACK)
        rrect(LINE_2, G_BACK)

    # 文件夹前板
    rrect(FRONT, G_FRONT)

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


def rr(geo):
    return (f'<rect x="{geo[0]}" y="{geo[1]}" width="{geo[2] - geo[0]}" '
            f'height="{geo[3] - geo[1]}" rx="{geo[4]}"/>')


def emit_svg(detail=True):
    """与 render() 同一套几何的矢量版,供网页 favicon 与 README 使用。"""
    cards = ""
    if detail:
        cards += rr(CARD_A)
    cards += rr(CARD_B)
    lines = rr(LINE_1) + rr(LINE_2) if detail else ""
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#f7f8fa"/><stop offset="1" stop-color="#eef1f5"/>
    </linearGradient>
  </defs>
  <rect x="{TILE[0]}" y="{TILE[1]}" width="{TILE[2] - TILE[0]}" height="{TILE[3] - TILE[1]}" rx="{TILE[4]}" fill="url(#tile)"/>
  <g fill="#0ca678">{rr(TAB)}{rr(BACK)}</g>
  <g fill="#a7f3d0">{rr(CARD_A) if detail else ""}</g>
  <rect x="{CARD_B[0]}" y="{CARD_B[1]}" width="{CARD_B[2] - CARD_B[0]}" height="{CARD_B[3] - CARD_B[1]}" rx="{CARD_B[4]}" fill="#ffffff"/>
  <g fill="#0ca678">{lines}</g>
  <g fill="#10b981">{rr(FRONT)}</g>
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

    # 自检拼图:深/浅两种底色,覆盖各关键尺寸
    sheet = Image.new("RGB", (900, 620), (245, 246, 248))
    dark = Image.new("RGB", (900, 300), (16, 18, 20))
    sheet.paste(dark, (0, 320))
    x = 30
    for size, _ in ICO_SIZES:
        im = next(i for s, i in frames if s == size)
        sheet.paste(im, (x, 310 - size - 10), im)
        sheet.paste(im, (x, 620 - size - 10), im)
        x += size + 28
    sheet.save(os.path.join(os.environ.get("TEMP", "/tmp"), "tv_icon_preview.png"), "PNG")
    print("icon assets written:", ico_path, svg_path, png_path, sep="\n  ")


if __name__ == "__main__":
    main()
