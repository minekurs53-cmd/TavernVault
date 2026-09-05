# -*- coding: utf-8 -*-
"""TavernVault 图标候选生成器 —— 10 个明亮简约风设计,输出对比拼图供挑选。

每个候选 = 同一套明亮瓷片(浅灰白圆角方) + 单一主色(应用主题靛蓝 #4c6ef5 系) + 少量点缀色,
全部扁平几何、无描边无渐变,16px 依然可读。挑选胜者后再并入 tools/gen_icon.py 正式产线。

用法: python tools/icon_candidates.py
输出: %TEMP%/tv_icons/candidates.png
"""
import os

from PIL import Image, ImageDraw, ImageFont

OUT_DIR = os.path.join(os.environ.get("TEMP", "/tmp"), "tv_icons")
S = 512              # 设计坐标系
CANVAS = 1024        # 每个候选的超采样画布

# ---- 明亮简约调色板 ----
TILE = (244, 246, 249)      # 瓷片 #f4f6f9
INK = (76, 110, 245)        # 主靛蓝(应用主题色) #4c6ef5
INK_D = (59, 91, 219)       # 深靛蓝 #3b5bdb
IND1 = (116, 143, 252)      # 亮靛蓝 #748ffc
IND2 = (186, 200, 255)      # 浅靛蓝 #bac8ff
SOFT = (219, 228, 255)      # 极浅靛蓝 #dbe4ff
WARM = (250, 176, 5)        # 琥珀点缀 #fab005
SLATE = (52, 58, 74)        # 深石板 #343a4a
WHITE = (255, 255, 255)


def font_path():
    for p in ("C:/Windows/Fonts/msyhbd.ttc", "C:/Windows/Fonts/msyh.ttc",
              "C:/Windows/Fonts/simhei.ttf"):
        if os.path.exists(p):
            return p
    return None


# ---- 坐标缩放绘制助手 ----
def R(d, k, x0, y0, x1, y1, r, fill):
    d.rounded_rectangle([x0 * k, y0 * k, x1 * k, y1 * k], radius=r * k, fill=fill)


def E(d, k, cx, cy, r, fill):
    d.ellipse([(cx - r) * k, (cy - r) * k, (cx + r) * k, (cy + r) * k], fill=fill)


def P(d, k, pts, fill):
    d.polygon([(x * k, y * k) for x, y in pts], fill=fill)


def quad(p0, p1, p2, n=20):
    """二次贝塞尔离散点。"""
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
            for t in (i / n for i in range(n + 1))]


# ---- 10 个候选(512 坐标系) ----

def c01_guan(d, k):
    """「馆」字标 —— 应用侧栏品牌位同源。"""
    fp = font_path()
    if fp:
        d.text((256 * k, 262 * k), "馆", font=ImageFont.truetype(fp, int(272 * k)),
               fill=INK, anchor="mm")


def c02_chest(d, k):
    """宝箱 —— Vault 收藏 + 保险库。"""
    R(d, k, 132, 240, 380, 388, 26, SOFT)   # 箱体
    R(d, k, 116, 164, 396, 252, 30, INK)    # 箱盖
    R(d, k, 238, 226, 274, 308, 12, WARM)   # 锁扣
    E(d, k, 256, 264, 11, INK_D)            # 锁孔点


def c03_folder(d, k):
    """文件夹 + 书签 —— 资源管理器本体 + 收藏。"""
    R(d, k, 112, 148, 236, 210, 22, INK_D)  # 标签耳
    R(d, k, 112, 174, 400, 380, 28, INK_D)  # 夹身(后)
    R(d, k, 112, 210, 400, 392, 26, INK)    # 夹身(前)
    P(d, k, [(298, 210), (350, 210), (350, 334), (324, 302), (298, 334)], WARM)  # 书签


def c04_cards(d, k):
    """卡片叠层 —— 角色卡/世界书/预设三类资源。"""
    R(d, k, 172, 122, 420, 304, 30, SOFT)
    R(d, k, 146, 154, 394, 336, 30, IND2)
    R(d, k, 120, 186, 368, 396, 32, INK)    # 前卡
    R(d, k, 156, 252, 332, 272, 10, WHITE)  # 文本行
    R(d, k, 156, 292, 296, 312, 10, WHITE)
    E(d, k, 180, 226, 14, WARM)             # 头像点


def c05_books(d, k):
    """书堆 —— 库/馆藏。"""
    R(d, k, 148, 214, 378, 270, 18, IND1)
    R(d, k, 118, 274, 368, 330, 18, INK)
    R(d, k, 140, 334, 388, 390, 18, INK_D)


def c06_shield(d, k):
    """盾牌 + 勾 —— 备份/还原/安全模型。"""
    pts = [(148, 160), (364, 160), (372, 172), (372, 264)]
    pts += quad((372, 264), (366, 352), (256, 412))
    pts += quad((256, 412), (146, 352), (140, 268))
    pts += [(140, 172)]
    P(d, k, pts, INK)
    d.line([(200 * k, 268 * k), (240 * k, 312 * k), (320 * k, 218 * k)],
           fill=WHITE, width=int(30 * k), joint="curve")
    E(d, k, 200, 268, 15, WHITE)
    E(d, k, 320, 218, 15, WHITE)


def c07_grid(d, k):
    """收纳格 —— 「散乱 → 有序」收纳入库。"""
    cells = [(127, 148, INK), (267, 148, IND1), (127, 288, IND2), (267, 288, WARM)]
    for x, y, c in cells:
        R(d, k, x, y, x + 118, y + 118, 26, c)


def c08_bottle(d, k):
    """简约药瓶 —— 酒馆意象的明亮重制(无描边无锁孔)。"""
    R(d, k, 232, 124, 280, 240, 0, IND2)    # 瓶颈
    E(d, k, 256, 318, 104, IND2)            # 瓶身
    lm = Image.new("L", d._image.size, 0)   # 药液 = 瓶身圆裁去液面以上
    ImageDraw.Draw(lm).ellipse([(256 - 104) * k, (318 - 104) * k, (256 + 104) * k, (318 + 104) * k], fill=255)
    ImageDraw.Draw(lm).rectangle([0, 0, d._image.size[0], 312 * k], fill=0)
    d._image.paste(Image.new("RGB", d._image.size, INK), (0, 0), lm)
    R(d, k, 238, 92, 274, 136, 10, WARM)    # 瓶塞


def c09_vault(d, k):
    """保险库锁孔 —— Vault 直译(圆窗 + 镂空锁孔)。"""
    E(d, k, 256, 256, 134, INK)
    E(d, k, 256, 234, 34, TILE)             # 锁孔圆
    P(d, k, [(238, 252), (274, 252), (264, 338), (248, 338)], TILE)  # 锁孔楔


def c10_goblet(d, k):
    """鸡尾酒杯 —— 酒馆直译。"""
    P(d, k, [(150, 152), (362, 152), (256, 286)], INK)
    E(d, k, 150, 152, 9, INK)               # 圆角
    E(d, k, 362, 152, 9, INK)
    E(d, k, 256, 286, 9, INK)
    R(d, k, 246, 284, 266, 362, 0, INK)     # 杯梗
    R(d, k, 192, 366, 320, 394, 14, INK)    # 底座
    E(d, k, 298, 200, 18, WARM)             # 橄榄


CANDIDATES = [
    (c01_guan, "馆字标"),
    (c02_chest, "宝箱"),
    (c03_folder, "文件夹+书签"),
    (c04_cards, "卡片叠层"),
    (c05_books, "书堆"),
    (c06_shield, "盾牌+勾"),
    (c07_grid, "收纳格"),
    (c08_bottle, "简约药瓶"),
    (c09_vault, "保险库门"),
    (c10_goblet, "鸡尾酒杯"),
]


def render(fn):
    img = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = CANVAS / S
    R(d, k, 16, 16, 496, 496, 116, TILE + (255,))
    fn(d, k)
    return img.resize((256, 256), Image.Resampling.LANCZOS)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    icons = [render(fn) for fn, _ in CANDIDATES]

    # 拼图:每候选一格 = 深底卡 + 浅底卡 + 标号 + 名称
    icon_s, gap, pad = 190, 12, 15
    cell_w = icon_s * 2 + gap + pad * 2          # 422
    cell_h = icon_s + 78
    cols, rows = 5, 2
    sheet = Image.new("RGB", (cols * cell_w + 40, rows * cell_h + 40), (233, 236, 242))
    sd = ImageDraw.Draw(sheet)
    num_font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 34)
    cn_font_path = font_path()
    cn_font = ImageFont.truetype(cn_font_path, 26) if cn_font_path else None

    for i, im in enumerate(icons):
        col, row = i % cols, i // cols
        x0 = 20 + col * cell_w
        y0 = 20 + row * cell_h
        for j, bg in enumerate(((20, 22, 28), (255, 255, 255))):
            cx = x0 + pad + j * (icon_s + gap)
            sd.rounded_rectangle([cx, y0, cx + icon_s, y0 + icon_s], radius=18, fill=bg)
            ic = im.resize((icon_s - 20, icon_s - 20), Image.Resampling.LANCZOS)
            sheet.paste(ic, (cx + 10, y0 + 10), ic)
        label_x = x0 + cell_w // 2
        sd.text((label_x, y0 + icon_s + 40), f"{i + 1}  {CANDIDATES[i][1]}",
                font=cn_font or num_font, fill=(52, 58, 74), anchor="mm")

    out = os.path.join(OUT_DIR, "candidates.png")
    sheet.save(out, "PNG")

    # 小尺寸退化对比:每候选 [32px 实际大小] + [32px 的 3 倍放大(最近邻,如实呈现像素糊化)]
    small = Image.new("RGB", (10 * 150 + 40, 170), (233, 236, 242))
    fd = ImageDraw.Draw(small)
    for i, im in enumerate(icons):
        x0 = 20 + i * 150
        tiny = im.resize((32, 32), Image.Resampling.LANCZOS)
        fd.rounded_rectangle([x0, 15, x0 + 48, 63], radius=10, fill=(20, 22, 28))
        small.paste(tiny, (x0 + 8, 23), tiny)
        zoom = tiny.resize((96, 96), Image.NEAREST)
        fd.rounded_rectangle([x0 + 52, 15, x0 + 148, 111], radius=10, fill=(20, 22, 28))
        small.paste(zoom, (x0 + 52, 15), zoom)
        fd.text((x0 + 75, 135), f"{i + 1}", font=num_font, fill=(52, 58, 74), anchor="mm")
    out2 = os.path.join(OUT_DIR, "candidates_small.png")
    small.save(out2, "PNG")
    print("sheet:", out, "\nsmall:", out2)


if __name__ == "__main__":
    main()
