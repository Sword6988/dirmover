"""运行时渲染的线性图标系统（纯标准库，无图片资产依赖）。

为什么不用 Unicode 字形（☑/☐ 等）：它们来自字体回退，光学大小、基线、
笔画粗细都不可控，不同机器观感不一致。这里在内存里按 16 设计单位
画好、4x 超采样抗锯齿，再编码为 PNG 交给 ``tk.PhotoImage``。

规格（与 Fluent 图标网格对齐）：
    - 画板 16 × 16 设计单位，活动区 2 … 14，四周留视觉留白；
    - 线宽 1.6 单位，端点圆头；
    - 勾选态：主色实心填充 + 白色对勾；未选态：仅描边，内部透出列表底色。
"""

from __future__ import annotations

import base64
import math
import struct
import tkinter as tk
import zlib
from typing import Callable, Dict, Optional, Tuple

# 与 theme.COLORS 保持语义一致（icons.py 不反向 import，避免循环依赖）
_RING_ON = (0x0F, 0x6C, 0xBD)      # accent
_RING_ON_PRESSED = (0x0C, 0x3B, 0x5E)  # accent_pressed（按下态：accent 向深一档）
_RING_OFF = (0x8F, 0x92, 0xA0)     # border（白底 3.1:1）
_RING_OFF_HOVER = (0x7F, 0x83, 0x91)  # border_strong（未选中悬停：描边加深一档）
_RING_ON_DIS = (0xCF, 0xE4, 0xF7)  # accent_dim
_RING_OFF_DIS = (0xCF, 0xD2, 0xDA)
_CHECK = (0xFF, 0xFF, 0xFF)

_SS = 4  # 超采样倍率


def _encode_png(size: int, rgba: bytes) -> bytes:
    """把原始 RGBA 像素编码为 PNG（color type 6, 8bit）。"""
    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    stride = size * 4
    raw = b"".join(b"\x00" + rgba[y * stride:(y + 1) * stride] for y in range(size))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def _seg_dist(px: float, py: float, ax: float, ay: float,
              bx: float, by: float) -> float:
    """点到线段距离（自然覆盖圆头端点）。"""
    abx, aby = bx - ax, by - ay
    length2 = abx * abx + aby * aby
    if length2 == 0:
        return math.hypot(px - ax, py - ay)
    t = ((px - ax) * abx + (py - ay) * aby) / length2
    t = max(0.0, min(1.0, t))
    return math.hypot(px - (ax + t * abx), py - (ay + t * aby))


def _inside_round_rect(x: float, y: float, x0: float, y0: float,
                       x1: float, y1: float, r: float) -> bool:
    if not (x0 <= x <= x1 and y0 <= y <= y1):
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def _checkbox_painter(checked: bool, disabled: bool,
                      hover: bool = False, pressed: bool = False) -> Callable:
    if checked:
        if disabled:
            ring = _RING_ON_DIS
        elif pressed:
            ring = _RING_ON_PRESSED
        else:
            ring = _RING_ON
    else:
        ring = (_RING_OFF_DIS if disabled
                else (_RING_OFF_HOVER if hover else _RING_OFF))
    # 外框：rect(1.6..14.4)，圆角 4.5（Fluent 圆角观感），线宽 1.6
    x0, y0, x1, y1 = 1.6, 1.6, 14.4, 14.4
    r_out = 4.5
    inset = 1.6
    # 对勾折线（勾选态）：圆头 2.1 粗
    check_pts = ((4.3, 8.4), (6.9, 11.0), (11.9, 5.2))
    half_w = 1.05

    def paint(u: float, v: float) -> Tuple[int, int, int, int]:
        inside_outer = _inside_round_rect(u, v, x0, y0, x1, y1, r_out)
        if not inside_outer:
            return (0, 0, 0, 0)
        inside_inner = _inside_round_rect(u, v, x0 + inset, y0 + inset,
                                          x1 - inset, y1 - inset, max(0.5, r_out - inset))
        on_check = any(
            _seg_dist(u, v, ax, ay, bx, by) <= half_w
            for (ax, ay), (bx, by) in zip(check_pts, check_pts[1:])
        )
        if checked:
            # 勾选态：主色实心填充 + 白色对勾（Fluent 规范）。
            # 内部若透出白底，白色对勾在 16px 下完全不可读（实测缺陷）。
            if on_check:
                return (*_CHECK, 255)
            return (*ring, 255)
        # 未选态：仅描边，内部透出列表底色
        if inside_inner:
            return (0, 0, 0, 0)
        return (*ring, 255)

    return paint


def _chevron_painter(color: Tuple[int, int, int]) -> Callable:
    """下拉框用的圆头 chevron-down（16 单位画板，居中，线宽 2.1）。"""
    pts = ((4.7, 6.3), (8.0, 9.6), (11.3, 6.3))
    half_w = 1.05

    def paint(u: float, v: float) -> Tuple[int, int, int, int]:
        for (ax, ay), (bx, by) in zip(pts, pts[1:]):
            if _seg_dist(u, v, ax, ay, bx, by) <= half_w:
                return (*color, 255)
        return (0, 0, 0, 0)

    return paint


# --------------------------------------------------------- 语义状态图标
# 弹窗用（dialogs.py）：软色圆底 + 语义色符号，颜色与 theme.COLORS 同源。
_STATUS_SOFT = {
    "info":     (0xE7, 0xF1, 0xFB),   # accent_soft
    "question": (0xE7, 0xF1, 0xFB),
    "warning":  (0xFD, 0xF6, 0xE8),   # warning_bg
    "error":    (0xFD, 0xF1, 0xEF),   # danger_soft
    "success":  (0xE6, 0xF4, 0xEC),   # success_soft
}
_STATUS_FG = {
    "info":     (0x0F, 0x6C, 0xBD),   # accent
    "question": (0x0F, 0x6C, 0xBD),
    "warning":  (0x8A, 0x52, 0x00),   # warning
    "error":    (0xC0, 0x2A, 0x1A),   # danger
    "success":  (0x0F, 0x7B, 0x3F),   # success
}


def _status_painter(kind: str) -> Callable:
    fg = _STATUS_FG[kind]
    soft = _STATUS_SOFT[kind]
    R = 7.3          # 圆底半径

    def paint(u: float, v: float) -> Tuple[int, int, int, int]:
        d = math.hypot(u - 8.0, v - 8.0)
        if d > R + 0.5:
            return (0, 0, 0, 0)
        # 圆底（AA 由边缘半覆盖自然形成）
        if d <= R - 0.4:
            base = (*soft, 255)
        else:
            t = (R + 0.4 - d) / 0.9
            base = (*soft, max(0, min(255, round(255 * t))))
        glyph = _status_glyph(u, v, kind)
        if glyph is not None:
            return (*fg, glyph)
        return base

    return paint


def _status_glyph(u: float, v: float, kind: str) -> Optional[int]:
    """返回该点的符号覆盖度(0-255)；不属于符号则 None。"""
    if kind in ("info", "warning"):
        # 「i」「!」：竖条 + 圆点
        if kind == "info":
            bar_y0, bar_y1 = 7.6, 11.3
            dot_y = 4.9
        else:
            bar_y0, bar_y1 = 4.4, 9.7
            dot_y = 12.3
        if abs(u - 8.0) <= 0.85 and bar_y0 <= v <= bar_y1:
            return 255
        d = math.hypot(u - 8.0, v - dot_y)
        if d <= 0.95:
            return 255
        return None
    if kind == "error":
        # 「×」两条对角线
        for ax, ay, bx, by in ((5.4, 5.4, 10.6, 10.6), (10.6, 5.4, 5.4, 10.6)):
            if _seg_dist(u, v, ax, ay, bx, by) <= 0.9:
                return 255
        return None
    if kind == "success":
        for ax, ay, bx, by in ((4.7, 8.4, 7.0, 10.7), (7.0, 10.7, 11.3, 5.9)):
            if _seg_dist(u, v, ax, ay, bx, by) <= 1.0:
                return 255
        return None
    # question「?」：上弧 + 竖尾 + 圆点
    cx, cy = 8.0, 6.5
    d = math.hypot(u - cx, v - cy)
    ang = math.degrees(math.atan2(cy - v, u - cx))   # y 向下 → 取上弧用 cy-v
    if 1.6 <= d <= 3.1 and 15.0 <= ang <= 165.0:
        return 255
    # 弧端点圆头
    for a_end in (15.0, 165.0):
        ex = cx + 2.35 * math.cos(math.radians(a_end))
        ey = cy - 2.35 * math.sin(math.radians(a_end))
        if math.hypot(u - ex, v - ey) <= 0.75:
            return 255
    if abs(u - 8.0) <= 0.8 and 8.8 <= v <= 10.1:
        return 255
    if math.hypot(u - 8.0, v - 12.3) <= 0.95:
        return 255
    return None


def _render(size_px: int, painter: Callable) -> bytes:
    """超采样渲染 → RGBA 字节。painter 接收 16 单位坐标，返回 (r,g,b,a)。"""
    n = size_px * _SS
    buf = bytearray(size_px * size_px * 4)
    for py in range(size_px):
        for px in range(size_px):
            r = g = b = a = 0
            for sy in range(_SS):
                for sx in range(_SS):
                    u = (px * _SS + sx + 0.5) * 16.0 / n
                    v = (py * _SS + sy + 0.5) * 16.0 / n
                    cr, cg, cb, ca = painter(u, v)
                    if ca:
                        # 颜色必须按覆盖度预乘（premultiplied alpha）：最终
                        # 颜色 = Σ(cr·ca) / Σ(ca)。若直接 Σ(cr)/Σ(ca)，深色
                        # 会被压黑——accent 蓝 (15,108,189) 全覆盖像素算出
                        # 240//4080 = 0，图标描边整体变黑（实测缺陷：勾选框
                        # 一直是黑色而非蓝色，白色对勾因 255 恰好幸存）。
                        r += cr * ca
                        g += cg * ca
                        b += cb * ca
                        a += ca
            total = _SS * _SS
            idx = (py * size_px + px) * 4
            if a:
                buf[idx] = min(255, r // a)
                buf[idx + 1] = min(255, g // a)
                buf[idx + 2] = min(255, b // a)
                # a 是各采样 alpha(0-255) 之和，除以采样数即平均覆盖度
                buf[idx + 3] = min(255, round(a / total))
    return bytes(buf)


class IconFactory:
    """按 (图标名, 尺寸) 缓存的 PhotoImage 工厂。

    Tk 的 PhotoImage 一旦被垃圾回收，图就没了——缓存本身负责持引用。
    """

    def __init__(self, master: tk.Misc, scale: float = 1.0):
        self.master = master
        self.scale = max(1.0, scale)
        self._cache: Dict[Tuple[str, int], tk.PhotoImage] = {}

    def _make(self, key: str, units: int, painter: Callable) -> tk.PhotoImage:
        size = max(12, round(units * self.scale))
        if (key, size) in self._cache:
            return self._cache[(key, size)]
        png = _encode_png(size, _render(size, painter))
        img = tk.PhotoImage(master=self.master, data=base64.b64encode(png))
        self._cache[(key, size)] = img
        return img

    def checkbox(self, checked: bool, disabled: bool = False,
                 units: int = 16, hover: bool = False,
                 pressed: bool = False) -> tk.PhotoImage:
        key = (f"check_{('on' if checked else 'off')}"
               f"{('_dis' if disabled else '')}{('_hov' if hover else '')}"
               f"{('_prs' if pressed else '')}")
        return self._make(key, units,
                          _checkbox_painter(checked, disabled, hover, pressed))

    def chevron_down(self, units: int = 12,
                     color: Tuple[int, int, int] = (0x54, 0x56, 0x63)) -> tk.PhotoImage:
        """下拉框箭头（圆头 chevron-down），color 为 (r,g,b)。"""
        key = "chevron_%02x%02x%02x" % color
        return self._make(key, units, _chevron_painter(color))

    def status(self, kind: str, units: int = 40) -> tk.PhotoImage:
        """弹窗语义图标：info / question / warning / error / success。"""
        if kind not in _STATUS_FG:
            kind = "info"
        return self._make(f"status_{kind}", units, _status_painter(kind))
