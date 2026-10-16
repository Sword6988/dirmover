"""表格列宽自适应：先量内容，再分视口。

改造前的问题：``ttk.Treeview`` 的 ``stretch=True`` 把「列宽」交给 Tk，而 Tk 只会在
窗口变宽/变窄时把差值**平均**分给每个可伸缩列——列宽与内容完全无关。实测
（窗口 975×800 物理像素，DPI 缩放 1.25）：

    ==========  ==========  ==========  ====================
    列          内容需要    改造前列宽  结果
    ==========  ==========  ==========  ====================
    名称         84px       179px       多出 95px 空白（4 个字的名字占 179px）
    当前位置    288px       241px       路径尾部被硬裁
    转移目标    317px       239px       路径尾部被硬裁
    状态        210px       203px       最长状态文案被裁
    ==========  ==========  ==========  ====================

本模块把列宽变成**可计算的分配问题**，两个步骤：

1. **量内容**：用真实字体量出「表头 + 每一行文字」的宽度，取最大值再加留白，
   得到每列的内容需要宽度 ``need``——它就是该列不被裁切所需的最小宽度。
2. **分视口**：把可用宽度按固定优先级分下去。

   - 总和 == 可用宽（不裁也不空）；
   - 每列不低于自己的 ``minimum``（内容需要 or 硬下限）；
   - 有余量时按角色配额 ``quota`` 分配——路径列拿大头（长路径受益），名称列
     只留极少余量（短名字不该占大块空白）；
   - 视口不足时按「可让出的余量」等比压缩，余量最大的列先让（路径列），
     余量为 0 的列（名称、状态）**不参与压缩**——状态文案完整可见是既有契约。
     （唯一的例外是连所有下限之和都放不下的病态宽度，见 ``fit_columns`` 末尾
     的兜底分支；正常受 ``root.minsize`` 保护不会出现。）

分配结果是纯函数输出，不依赖 Tk，可直接离线测试（见 ``tests/test_columns.py``）。
"""

from __future__ import annotations

from tkinter import TclError
from typing import Dict, List, Sequence, Tuple

# ---------------------------------------------------------------- 列定义
# (key, 表头文案, 文字锚点, 余量配额, 是否保护)
#
# ``余量配额``：窗口变宽时该列最多额外拿多少逻辑像素。名称列给最少——它承载
# 2~5 个汉字的固定短文本，多出来的宽度只会变成空白。
# ``保护``：True 表示该列永不因视口不足而低于「内容需要宽度」，且不低于 ``MIN_COL``
#   （字体度量偏小的机器上，按 need 取下限会退化出几十像素的窄列，故加硬下限兜底）。
#   - 名称：列名必须整读，且它是行标识；
#   - 状态：状态文案完整可见是既有产品契约（历史缺陷：150px 列宽把文案裁掉了）。
COLUMNS: Tuple[Tuple[str, str, str, int, bool], ...] = (
    ("name", "文件夹名称", "w", 24, True),
    ("current", "当前位置", "w", 140, False),
    ("target", "转移目标", "w", 140, False),
    ("status", "状态", "w", 72, True),
)

COLUMN_KEYS: Tuple[str, ...] = tuple(col[0] for col in COLUMNS)
HEADERS: Dict[str, str] = {col[0]: col[1] for col in COLUMNS}

# 值列文字左右留白（逻辑像素）：含 ttk 原生 cell 边距（约 4px）与呼吸空间
CELL_PAD = 12
# 表头文字左右留白（逻辑像素）：Treeview.Heading 自身 padding 为 px(5)×2
HEAD_PAD = 14
# 文本列硬下限（逻辑像素）：非保护列在前者让无可让时不得低于此值
MIN_COL = 90
# 极端窄窗下的绝对下限（逻辑像素）：仅当连所有硬下限都放不下时才会用到，
# 正常受 ``root.minsize`` 保护不会出现——保留是为了函数在任何输入下都有解。
FLOOR_COL = 56
# 未映射窗口（构建期 / 无图形会话的测试）的保守可用宽（逻辑像素）。
# 取最小窗口下的实测值：宁可排得紧凑，映射后 ``<Configure>`` 会立刻用真实宽度重排。
FALLBACK_VIEWPORT = 720


def measure_text(font, text: str) -> int:
    """用真实字体量文字宽度（物理像素）；量不出来时返回 0，绝不让布局崩掉。"""
    if not text:
        return 0
    try:
        return int(font.measure(text))
    except (TclError, AttributeError, TypeError):
        return 0


def _largest_remainder(amount: int, weights: Sequence[int]) -> List[int]:
    """把 ``amount`` 按权重拆成整数份，总和**恰为** ``amount``。

    用最大余数法而不是四舍五入：保证分配可复现（同输入同输出），也让
    「各列宽度之和 == 可用宽」这条断言在测试里成立。
    """
    n = len(weights)
    if n == 0:
        return []
    if amount <= 0:
        return [0] * n
    total = sum(weights)
    if total <= 0:
        base, rest = divmod(amount, n)
        return [base + (1 if i < rest else 0) for i in range(n)]
    raw = [amount * w / total for w in weights]
    out = [int(x) for x in raw]
    rest = amount - sum(out)
    order = sorted(range(n), key=lambda i: (raw[i] - out[i], weights[i], -i),
                   reverse=True)
    i = 0
    while rest > 0:
        out[order[i % n]] += 1
        rest -= 1
        i += 1
    return out


def fit_columns(
    needs: Sequence[int],
    minimums: Sequence[int],
    quotas: Sequence[int],
    available: int,
) -> List[int]:
    """把 ``available`` 物理像素分给各列，返回每列宽度（总和恰为 available）。

    :param needs: 各列「完整显示内容」所需宽度（含留白），物理像素。
    :param minimums: 各列下限，物理像素。保护列应传入 ``needs`` 本身。
    :param quotas: 各列「变宽时最多多拿多少」，物理像素，仅用于分配余量。
    :param available: 文本区可用宽度（不含行首图标列），物理像素。
    """
    n = len(needs)
    if n == 0:
        return []
    avail = max(int(available), 0)
    soft = [max(int(needs[i]), int(minimums[i])) for i in range(n)]
    total = sum(soft)

    if total == avail:
        return soft

    if total < avail:
        # ---- 余量：先按角色配额分（路径列优先），配额用尽后再按内容比例摊
        widths = list(soft)
        surplus = avail - total
        quota = [max(0, int(q)) for q in quotas]
        room = min(surplus, sum(quota))
        if room > 0:
            add = _largest_remainder(room, quota)
            widths = [w + a for w, a in zip(widths, add)]
            surplus -= room
        if surplus > 0:
            add = _largest_remainder(surplus, soft)
            widths = [w + a for w, a in zip(widths, add)]
        return widths

    # ---- 不足：先让「有余量可让」的列按余量等比让步（保护列余量为 0，不动）
    deficit = total - avail
    slack = [soft[i] - int(minimums[i]) for i in range(n)]
    if sum(slack) >= deficit:
        cut = _largest_remainder(deficit, slack)
        return [soft[i] - cut[i] for i in range(n)]

    # ---- 连下限都放不下（被 minsize 挡住的病态输入）：按下限比例整体压缩
    widths = [int(minimums[i]) for i in range(n)]
    base = sum(widths)
    if base <= 0:
        return _largest_remainder(avail, [1] * n)
    widths = [max(1, avail * m // base) for m in (int(minimums[i]) for i in range(n))]
    diff = avail - sum(widths)
    order = sorted(range(n), key=lambda i: (int(minimums[i]), -i), reverse=True)
    i = 0
    while diff != 0 and i < 16 * n:
        idx = order[i % n]
        step = 1 if diff > 0 else -1
        if widths[idx] + step >= 1:
            widths[idx] += step
            diff -= step
        i += 1
    return widths
