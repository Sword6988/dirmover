"""生成程序图标 ``assets/app.ico``（本项目**原创绘制**，非派生自任何既有图标）。

设计（唯一真源就在本文件里，改图标改这里再跑一遍即可）：

* **底盘**：圆角方块 + 自上而下的主色渐变（沿用界面主题的 Accent 蓝，浅→深）。
* **字形**：纯白「数据块 + 右向箭头」——左侧一张圆角卡片代表"数据"，右侧一支
  粗箭头指向右，合起来表达"把数据搬到新位置"，即本工具的核心动作。
* **尺寸**：16/20/24/32/48/64/128/256 八帧**各自单独绘制**（不是把 256 缩下去），
  小尺寸会按比例加粗、加大留白，保证任务栏与资源管理器里的可读性。

实现要点：

* 每帧先按 ``SUPERSAMPLE`` 倍画到临时画布，再用 LANCZOS 降采样 —— 得到平滑抗锯齿
  边缘（直接在小画布上画圆角会出锯齿）。
* 完全确定性：几何全由尺寸比例算出，不含随机数、不含时间戳；同样的 Pillow 版本
  重复运行产出一致的 .ico。
* 只依赖 Pillow（**仅构建期**，不进运行时依赖；exe 内不含 Pillow）。

用法：
    <python> tools/make_icon.py            # 覆盖写 assets/app.ico
    <python> tools/make_icon.py --check    # 只校验现有文件是否为八帧、不写盘
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import List, Sequence, Tuple

ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "assets" / "app.ico"

# 八帧：覆盖 Windows 任务栏/资源管理器/Alt-Tab/大图标视图会用到的全部档位
SIZES: Tuple[int, ...] = (16, 20, 24, 32, 48, 64, 128, 256)

# 降采样倍率：小尺寸需要更高的相对倍率才能把圆角与斜边磨平
SUPERSAMPLE = 16

# 主色渐变（与 docmover/ui/theme.py 的 accent 同色系：浅色端 → 主题 Accent）
GRADIENT_TOP = (0x46, 0x96, 0xE6)
GRADIENT_BOTTOM = (0x0F, 0x6C, 0xBD)

# 圆角半径（占整幅边长比例）
TILE_RADIUS_RATIO = 0.20


def _glyph(size: int) -> Tuple[
    List[Tuple[float, float, float, float, float]],
    Tuple[float, float, float, float],
    List[Tuple[float, float]],
]:
    """按尺寸给出字形几何（**小尺寸自适应**，不是等比缩下去）。

    原因：16px 的像素栅格只装得下两个"特征"。若在大尺寸几何上等比缩小，
    左侧卡片与箭杆之间的缝隙会塌到 0.3px，两块白色连成一片糊掉。故按档位调整：

    * ``>= 48``：完整字形——卡片 + 缝隙 + 箭头（箭头最挺拔）。
    * ``20–47``：收紧缝隙、加粗箭杆、放大箭头，卡片略放大（与大连尺寸同构）。
    * ``<= 16``：**只留加粗箭头**（卡片与箭头在 16px 栅格上无法并存而不糊），
      箭杆加粗、箭头放大并整体居中。

    返回 ``(卡片, 箭杆, 箭头多边形)`` 三组归一化坐标。
    """
    if size >= 48:
        cards = [(0.150, 0.372, 0.418, 0.628, 0.062)]
        shaft = (0.470, 0.440, 0.612, 0.560)
        head = [(0.598, 0.310), (0.855, 0.500), (0.598, 0.690)]
    elif size >= 20:
        cards = [(0.125, 0.345, 0.400, 0.655, 0.075)]
        shaft = (0.435, 0.415, 0.590, 0.585)
        head = [(0.570, 0.265), (0.880, 0.500), (0.570, 0.735)]
    else:
        cards = []
        shaft = (0.155, 0.408, 0.535, 0.592)
        head = [(0.495, 0.255), (0.845, 0.500), (0.495, 0.745)]
    return cards, shaft, head


def _render(size: int) -> "object":
    """画出一帧 ``size x size`` 的 RGBA 图像。"""
    from PIL import Image, ImageDraw

    canvas = size * SUPERSAMPLE
    img = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))

    # ----------------------------------------------------------- 圆角底盘
    radius = max(1, round(canvas * TILE_RADIUS_RATIO))
    grad = Image.new("RGBA", (1, canvas), (0, 0, 0, 255))
    for y in range(canvas):
        t = y / max(1, canvas - 1)
        grad.putpixel((0, y), (
            round(GRADIENT_TOP[0] + (GRADIENT_BOTTOM[0] - GRADIENT_TOP[0]) * t),
            round(GRADIENT_TOP[1] + (GRADIENT_BOTTOM[1] - GRADIENT_TOP[1]) * t),
            round(GRADIENT_TOP[2] + (GRADIENT_BOTTOM[2] - GRADIENT_TOP[2]) * t),
            255,
        ))
    grad = grad.resize((canvas, canvas), Image.NEAREST)

    tile_mask = Image.new("L", (canvas, canvas), 0)
    ImageDraw.Draw(tile_mask).rounded_rectangle(
        (0, 0, canvas - 1, canvas - 1), radius=radius, fill=255
    )
    img.paste(grad, (0, 0), tile_mask)

    # ------------------------------------------------- 白色「卡片 + 箭头」字形
    def ux(v: float) -> int:
        return round(canvas * v)

    draw = ImageDraw.Draw(img)
    cards, shaft, head = _glyph(size)

    for x0, y0, x1, y1, r in cards:
        draw.rounded_rectangle((ux(x0), ux(y0), ux(x1), ux(y1)),
                               radius=ux(r), fill=(255, 255, 255, 255))

    # 箭杆 + 三角头（两者在 x 上重叠，合成一支完整箭头）
    draw.rectangle((ux(shaft[0]), ux(shaft[1]), ux(shaft[2]), ux(shaft[3])),
                   fill=(255, 255, 255, 255))
    draw.polygon([(ux(x), ux(y)) for x, y in head], fill=(255, 255, 255, 255))

    return img.resize((size, size), Image.LANCZOS)


def build(sizes: Sequence[int] = SIZES) -> "object":
    """按尺寸列表绘制各帧，返回按尺寸升序排列的图像列表。"""
    return [_render(s) for s in sorted(sizes)]


def write_ico(target: Path = TARGET, sizes: Sequence[int] = SIZES) -> Path:
    """把各帧写成一个多尺寸 .ico。"""
    frames = build(sizes)
    base = frames[-1]                      # 最大帧作为容器基准
    target.parent.mkdir(parents=True, exist_ok=True)
    base.save(
        target,
        format="ICO",
        sizes=[(s, s) for s in sorted(sizes)],
        append_images=frames,
        bitmap_format="bmp",
    )
    return target


def check(target: Path = TARGET, sizes: Sequence[int] = SIZES) -> List[str]:
    """校验现有 .ico 的帧尺寸集合是否与期望一致。"""
    problems: List[str] = []
    if not target.exists():
        return [f"缺少 {target}"]
    from PIL import Image

    with Image.open(target) as im:
        got = sorted(im.info.get("sizes", []))
    want = sorted((s, s) for s in sizes)
    if got != want:
        problems.append(f"帧尺寸不符：期望 {want}，实际 {got}")
    return problems


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="生成原创程序图标 assets/app.ico")
    parser.add_argument("--check", action="store_true", help="只校验，不写盘")
    args = parser.parse_args(argv)

    try:
        import PIL  # noqa: F401
    except ImportError:
        print("[FAIL] 需要 Pillow（仅构建期依赖）：pip install pillow")
        return 2

    if args.check:
        problems = check()
        if problems:
            for item in problems:
                print(f"[FAIL] {item}")
            return 1
        print(f"[OK] {TARGET.relative_to(ROOT)} 帧尺寸与预期一致（{len(SIZES)} 帧）")
        return 0

    path = write_ico()
    print(f"[OK] 已写入 {path.relative_to(ROOT)}（{len(SIZES)} 帧："
          f"{', '.join(f'{s}x{s}' for s in SIZES)}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
