"""Fluent 浅色主题（Win11 观感）。

以 ``clam`` 为底（唯一能完全自定义配色的内置主题），按一套实测对比度的
设计令牌重绘：冷调中性色阶做表面层级（壳 → 卡片 → 次级面）、单一 Fluent 蓝
做强调色、悬停/按下状态一律向深色变化（浅底上「调亮」没有反馈感）。

全部令牌集中在本文件，界面代码不得出现字面色值。
字号一律返回**负的整数像素**（正数是「点」，换算后落在非整数像素会让
笔画重采样发虚），并随系统 DPI 缩放。
"""

from __future__ import annotations

import tkinter as tk
from tkinter import font as tkfont
from tkinter import ttk
from typing import Callable, Dict, Optional

from .icons import IconFactory

# ---------------------------------------------------------------- 令牌表
# 每个边界/文字色的对比度都是对「它自己的背景」实算的，改动请重算。
COLORS: Dict[str, str] = {
    # 表面（三层：壳 → 卡片 → 次级面；禁止卡片套卡片）
    "bg": "#f4f5f8",              # 窗口壳底（冷调中性，比卡片暗一档）
    "surface": "#ffffff",         # 卡片 / 输入控件底
    "surface_alt": "#fafbfd",     # 次级面：斑马纹 / 表头
    "surface_sunken": "#f1f2f6",  # 禁用底 / 下沉面
    # 边界
    "border": "#8f92a0",          # 控件边界·硬档（白底 3.1:1；勾选框描边语义锚点，见 icons._RING_OFF）
    "border_control": "#c9ccd6",  # 次按钮 / 下拉框描边（柔和一档，降低生硬感；控件身份由底色与文字承担，描边为装饰性分层）
    "border_strong": "#7f8391",   # 壳底/画布上的边界（对壳底 3.5:1）
    "border_soft": "#e3e5ea",     # 装饰性分隔线（不承担唯一线索）
    "border_hair": "#eceef2",     # 卡片外框 / 栅格线 / 弹窗外圈
    "divider": "#e3e5ea",
    # 文本
    "text": "#1a1a1f",            # 主文字（白底 17.3:1）
    "text_dim": "#545663",        # 次要文字（白底 7.3:1）
    "text_muted": "#6b6e7d",      # 说明文字（白底 5.1:1）
    "text_disabled": "#8d90a0",   # 禁用文字（白底 3.2:1，inactive 豁免）
    "text_on_accent": "#ffffff",
    # 强调（Fluent 蓝；浅底上悬停/按下都向深色变化）
    "accent": "#0f6cbd",
    "accent_hover": "#115ea3",
    "accent_pressed": "#0c3b5e",
    "accent_soft": "#e7f1fb",
    "accent_dim": "#cfe4f7",      # 主色淡化（禁用主色 / 焦点弱化）
    # 语义
    "danger": "#c02a1a",
    "danger_hover": "#a62416",
    "danger_pressed": "#8c1d11",
    "danger_soft": "#fdf1ef",
    "danger_border": "#e5b3ac",
    "warning": "#8a5200",
    "warning_bg": "#fdf6e8",
    "warning_border": "#f0dcae",
    "success": "#0f7b3f",
    "info": "#0f6cbd",
    # 列表
    "row_selected": "#dbeafc",    # 选中行（比斑马纹明显一档）
    "row_alt": "#fafbfd",
    "row_hover": "#f1f3f7",
    # 控件状态（浅底：白 → 深一档 → 再深一档）
    "btn_hover": "#f2f3f7",
    "btn_pressed": "#e6e7ee",
    "track": "#d5d7de",           # 进度条底槽 / 未填充段
    # 滚动条（拇指比槽底只深一档，悬停加深一档保证可辨识）
    "scroll_thumb": "#d0d3db",    # 滚动条拇指（装饰性，比 border_hair 深一档）
    "scroll_thumb_hover": "#b3b8c3",  # 滚动条悬停（对 surface 约 2.2:1）
    # 浮层（唯一的深色面：tooltip 靠实底表达「我在最上面」）
    "tooltip_bg": "#2f2f36",
    "tooltip_fg": "#f5f5f7",
}

# 语义间距刻度：所有 pady/padx 只允许从这几档取
# 2026-10-14 视觉现代化：整体上调一档（原 4,6,8,12,16），让界面更透气
SP_XS, SP_SM, SP_MD, SP_LG, SP_XL = 4, 8, 12, 16, 24

FONT_FAMILY_CANDIDATES = (
    "HarmonyOS Sans SC", "Microsoft YaHei UI", "Microsoft YaHei",
    "PingFang SC", "Source Han Sans SC", "Noto Sans SC", "Segoe UI", "Tahoma",
)
MONO_FAMILY_CANDIDATES = ("Cascadia Mono", "Consolas", "Courier New")


def _hex2rgb(h: str) -> tuple:
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def mix(c1: str, c2: str, t: float) -> str:
    """在 c1 与 c2 之间线性插值：t=0 返回 c1，t=1 返回 c2。"""
    a, b = _hex2rgb(c1), _hex2rgb(c2)
    return "#%02x%02x%02x" % tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def pick_font(root: tk.Misc, candidates, fallback: str = "TkDefaultFont") -> str:
    """挑选系统上第一个可用字体。"""
    try:
        available = set(tkfont.families(root))
    except tk.TclError:
        return fallback
    for name in candidates:
        if name in available:
            return name
    return fallback


class Theme:
    """集中管理字体、图标与 ttk 样式。"""

    def __init__(self, root: tk.Tk):
        self.root = root
        self.scale = self._detect_scale(root)
        self.style = ttk.Style(root)
        try:
            self.style.theme_use("clam")
        except tk.TclError:
            pass

        self.ui_font = pick_font(root, FONT_FAMILY_CANDIDATES)
        self.mono_font = pick_font(root, MONO_FAMILY_CANDIDATES, "TkFixedFont")

        # 字号层级（2026-10-14 整体上调一档，拉开字号层级）
        self.fonts: Dict[str, tuple] = {
            "base": (self.ui_font, -self.px(14)),
            "small": (self.ui_font, -self.px(13)),
            "strong": (self.ui_font, -self.px(14), "bold"),
            "h1": (self.ui_font, -self.px(22), "bold"),
            "h2": (self.ui_font, -self.px(17), "bold"),
            "h3": (self.ui_font, -self.px(13), "bold"),
            "mono": (self.mono_font, -self.px(12)),
            "mono_small": (self.mono_font, -self.px(12)),
        }

        # 图标工厂：运行时渲染的线性图标（勾选框等）
        self._icons = IconFactory(root, self.scale)
        self._keep_refs: list = []   # 注册进 ttk 的 PhotoImage 必须持引用

        self._apply_root()
        self._configure_styles()
        self._install_checkbutton_indicator()
        self._install_combobox_chevron()

    # ------------------------------------------------------------- 缩放
    @staticmethod
    def _detect_scale(root: tk.Tk) -> float:
        try:
            dpi = root.winfo_fpixels("1i")
        except tk.TclError:
            dpi = 96.0
        return max(1.0, dpi / 96.0)

    def px(self, v: float) -> int:
        """逻辑像素 → 物理像素。所有 1px 线、间距、尺寸一律过这里。"""
        return max(1, round(v * self.scale))

    def checkbox(self, checked: bool, disabled: bool = False,
                 hover: bool = False, pressed: bool = False) -> Optional[tk.PhotoImage]:
        """勾选框图标（与表格行首、ttk 勾选框共用同一套视觉）。

        ``hover=True`` 仅用于未选中态的悬停加深（ttk 勾选框指示器）；
        ``pressed=True`` 仅用于选中态按下（勾色用 ``accent_pressed``，比选中态更深）。
        """
        return self._icons.checkbox(
            checked, disabled=disabled, hover=hover, pressed=pressed)

    def icon(self, kind: str, units: int = 16) -> tk.PhotoImage:
        """语义状态图标（info/question/warning/error/success），供小尺寸场景。"""
        return self._icons.status(kind, units=units)

    # ------------------------------------------------------------- 基础
    def _apply_root(self) -> None:
        root = self.root
        root.configure(bg=COLORS["bg"])
        try:
            root.option_add("*Font", self.fonts["base"])
            root.option_add("*TCombobox*Listbox.font", self.fonts["base"])
            root.option_add("*TCombobox*Listbox.selectBackground", COLORS["accent"])
            root.option_add("*TCombobox*Listbox.selectForeground", COLORS["text_on_accent"])
            root.option_add("*Text.font", self.fonts["mono"])
        except tk.TclError:
            pass

    # ------------------------------------------------------------- 样式
    def _configure_styles(self) -> None:
        style = self.style
        colors = COLORS

        # ---------------------------------------------------- 容器与文本
        style.configure(".", background=colors["bg"], foreground=colors["text"],
                        font=self.fonts["base"], borderwidth=0, focuscolor=colors["accent"])
        style.configure("TFrame", background=colors["bg"])
        style.configure("Card.TFrame", background=colors["surface"],
                        borderwidth=self.px(1), relief="solid",
                        bordercolor=colors["border_hair"])

        style.configure("TLabel", background=colors["bg"], foreground=colors["text"])
        style.configure("Muted.TLabel", background=colors["bg"],
                        foreground=colors["text_muted"], font=self.fonts["small"])
        style.configure("Warning.TLabel", foreground=colors["warning"],
                        background=colors["warning_bg"])
        # 摘要文本：强调色 + 加粗，落在 bg 底上（控制区）
        style.configure("Summary.TLabel", background=colors["bg"],
                        foreground=colors["accent"], font=self.fonts["strong"])

        style.configure("TSeparator", background=colors["border_soft"])

        # ------------------------------------------------------------ 按钮
        # 浅色主题铁律：白底按钮靠「描边 + 向深色变化」表达层级与反馈；
        # 描边用柔和的 border_control（硬档 border 只留给必须 3:1 的场景）
        base_button = dict(
            font=self.fonts["base"], padding=(self.px(16), self.px(9)),
            borderwidth=self.px(1), focusthickness=self.px(1), relief="flat",
        )
        style.configure(
            "TButton",
            background=colors["surface"],
            foreground=colors["text"],
            bordercolor=colors["border_control"],
            lightcolor=colors["surface"],
            darkcolor=colors["surface"],
            **base_button,
        )
        style.map(
            "TButton",
            background=[
                ("disabled", mix(colors["btn_hover"], colors["surface"], 0.5)),
                ("pressed", colors["btn_pressed"]),
                ("active", colors["btn_hover"]),
            ],
            foreground=[("disabled", colors["text_disabled"])],
            bordercolor=[
                ("disabled", colors["border_hair"]),
                ("pressed", colors["border_strong"]),
                ("active", colors["border"]),
            ],
        )

        # 主操作按钮：唯一的高视觉权重控件，锚定视觉动线终点
        style.configure(
            "Accent.TButton",
            background=colors["accent"],
            foreground=colors["text_on_accent"],
            bordercolor=colors["accent"],
            lightcolor=colors["accent"],
            darkcolor=colors["accent"],
            focuscolor=colors["accent_pressed"],
            padding=(self.px(20), self.px(10)),
            font=self.fonts["strong"],
            borderwidth=self.px(1), focusthickness=self.px(1), relief="flat",
        )
        style.map(
            "Accent.TButton",
            background=[
                ("disabled", colors["accent_dim"]),
                ("pressed", colors["accent_pressed"]),
                ("active", colors["accent_hover"]),   # 向深色变化（浅底铁律）
            ],
            foreground=[("disabled", colors["text_on_accent"])],
            bordercolor=[
                ("disabled", colors["accent_dim"]),
                ("pressed", colors["accent_pressed"]),
                ("active", colors["accent_hover"]),
            ],
        )

        # 危险操作按钮：白底 + 危险色文字 + 淡红描边，悬停浮现淡红底
        style.configure(
            "Danger.TButton",
            background=colors["surface"],
            foreground=colors["danger"],
            bordercolor=colors["danger_border"],
            lightcolor=colors["surface"],
            darkcolor=colors["surface"],
            **base_button,
        )
        style.map(
            "Danger.TButton",
            background=[
                ("disabled", mix(colors["btn_hover"], colors["surface"], 0.5)),
                ("pressed", colors["danger_soft"]),
                ("active", colors["danger_soft"]),
            ],
            foreground=[
                ("disabled", colors["text_disabled"]),
                ("active", colors["danger_hover"]),
            ],
            bordercolor=[
                ("active", colors["danger"]),
                ("disabled", colors["border_hair"]),
            ],
        )

        # 链接式按钮：状态栏「打开日志目录」等次要出口（bg 底上的文字按钮）
        style.configure(
            "Link.TButton",
            background=colors["bg"],
            foreground=colors["accent"],
            bordercolor=colors["bg"],
            lightcolor=colors["bg"],
            darkcolor=colors["bg"],
            padding=(self.px(SP_XS), self.px(2)),
            font=self.fonts["small"],
        )
        style.map(
            "Link.TButton",
            background=[("active", colors["accent_soft"])],
            foreground=[("active", colors["accent_pressed"])],
            bordercolor=[("active", colors["accent_soft"])],
        )

        # -------------------------------------------------- 单屏区域样式
        # 状态栏壳（状态文字 / 进度条 / 模式徽标）
        style.configure("Status.TFrame", background=colors["bg"])
        # Banner 提权入口：链接风格（警示底上，避免出现第二种按钮实现）
        style.configure(
            "WarningLink.TButton",
            background=colors["warning_bg"],
            foreground=colors["accent"],
            bordercolor=colors["warning_bg"],
            lightcolor=colors["warning_bg"],
            darkcolor=colors["warning_bg"],
            focuscolor=colors["warning_bg"],
            padding=(self.px(SP_XS), self.px(SP_XS)),
            font=self.fonts["small"],
        )
        style.map(
            "WarningLink.TButton",
            background=[("active", colors["accent_soft"])],
            foreground=[("active", colors["accent_pressed"])],
            bordercolor=[("active", colors["accent_soft"])],
        )

        # ------------------------------------------------------ 勾选框
        # 指示器在 _install_checkbutton_indicator 里替换为渲染图标；
        # 若替换失败则退回 clam 原生方块（仅换色）。
        style.configure(
            "TCheckbutton",
            background=colors["bg"],
            foreground=colors["text"],
            focuscolor=colors["bg"],
            indicatorcolor=colors["surface"],
            padding=(self.px(2), self.px(4)),
            font=self.fonts["base"],
        )
        style.map(
            "TCheckbutton",
            background=[("active", colors["bg"])],
            foreground=[("disabled", colors["text_disabled"])],
            indicatorcolor=[
                ("selected", colors["accent"]),
                ("pressed", colors["accent_hover"]),
                ("disabled", colors["surface_sunken"]),
            ],
        )

        # ---------------------------------------------------------- 输入
        # 只有只读下拉（目标根目录）；正文输入框按需求整体移除，故不再定义 TEntry。
        style.configure(
            "TCombobox",
            fieldbackground=colors["surface"],
            background=colors["surface"],
            foreground=colors["text"],
            bordercolor=colors["border_control"],
            lightcolor=colors["border_control"],
            darkcolor=colors["border_control"],
            arrowcolor=colors["text_dim"],
            padding=(self.px(10), self.px(7)),
        )
        style.map(
            "TCombobox",
            # ⚠ 映射了只读态**底色**，就必须同时映射只读态**文字色**（用户实测缺陷）。
            #
            # 只读下拉框一获得焦点（点开下拉再点空白处、或 Tab 进来）文字会整格
            # 消失，看起来像"值被清空了"，其实 `get()` 与 `textvariable` 都还在。
            # 根因：clam 的 TCombobox 把文字色放在 `-selectforeground`（默认白）
            # 上，而 `-selectforeground` 的映射只在 `!focus` 时命中；焦点态于是
            # 回落到基值白色 —— 一旦本主题又把只读底映射成 surface（白），
            # 就成了**白字画在白底上**。反过来说，不映射底色时 clam 的只读底是
            # 深色，白字反而看得见，缺陷才被掩盖。
            # 修法：把只读/禁用态的文字色显式钉在令牌上，不再依赖 clam 的
            # select* 回落（`disabled` 必须排在 `readonly` 之前，ttk 取首个命中项）。
            foreground=[
                ("disabled", colors["text_disabled"]),
                ("readonly", colors["text"]),
            ],
            fieldbackground=[("readonly", colors["surface"]), ("disabled", colors["surface_sunken"])],
            bordercolor=[
                ("focus", colors["accent"]),
                ("disabled", colors["border_hair"]),
            ],
            lightcolor=[
                ("focus", colors["accent"]),
                ("disabled", colors["border_hair"]),
            ],
            darkcolor=[
                ("focus", colors["accent"]),
                ("disabled", colors["border_hair"]),
            ],
            arrowcolor=[
                ("disabled", colors["text_disabled"]),
                ("active", colors["text"]),
            ],
        )

        # ------------------------------------------------ 表格 (Treeview)
        # 树列布局去掉 indicator（展开三角）：本项目全部为根级项、无子节点，
        # 而 clam 会为它保留 ~20px 位宽，把行首勾选框推出窄列被整格裁掉
        # （实测缺陷：#0 列 < 46px 时图标不可见）。移除后图标位置只由
        # Treeview.Item padding 决定，完全确定。
        style.layout(
            "Treeview.Item",
            [("Treeitem.padding", {"sticky": "nswe", "children": [
                ("Treeitem.image", {"side": "left", "sticky": ""}),
                ("Treeitem.text", {"sticky": "nswe"}),
            ]})],
        )
        # 勾选框与列边界的左右留白（值列文字有 ~4px 原生边距，取 6 对齐；
        # main_window 的 #0 列宽 28 = 6 + 图标 16 + 6，与此处必须同步）
        style.configure("Treeview.Item", padding=(self.px(6), 0))
        style.configure(
            "Treeview",
            background=colors["surface"],
            fieldbackground=colors["surface"],
            foreground=colors["text"],
            bordercolor=colors["border_hair"],
            lightcolor=colors["border_hair"],
            darkcolor=colors["border_hair"],
            rowheight=self.px(40),
            font=self.fonts["base"],
            relief="flat",
        )
        style.map(
            "Treeview",
            background=[("selected", colors["row_selected"])],
            foreground=[("selected", colors["text"])],
        )
        style.configure(
            "Treeview.Heading",
            background=colors["surface_alt"],
            foreground=colors["text_muted"],
            bordercolor=colors["border_hair"],
            lightcolor=colors["surface_alt"],
            darkcolor=colors["surface_alt"],
            relief="flat",
            # 水平 padding 与值列文字的原生边距(~4px)对齐，表头与正文同线
            padding=(self.px(5), self.px(10)),
            font=self.fonts["h3"],
        )
        style.map(
            "Treeview.Heading",
            background=[("active", colors["surface_sunken"])],
            relief=[("active", "flat")],
        )

        # ------------------------------------------------------ 进度条
        style.configure(
            "Horizontal.TProgressbar",
            background=colors["accent"],
            troughcolor=colors["surface_sunken"],
            bordercolor=colors["surface_sunken"],
            lightcolor=colors["accent"],
            darkcolor=colors["accent"],
            thickness=self.px(4),
        )
        # -------------------------------------------------- 滚动条
        # 现代细滚动条：去箭头、圆角观感靠窄拇指 + 平淡底槽；拇指用专门的
        # scroll_thumb 令牌（比 border_hair 深一档），悬停加深保证可辨识
        for orient, sticky in (("Vertical", "ns"), ("Horizontal", "we")):
            style.layout(
                f"{orient}.TScrollbar",
                [(f"{orient}.Scrollbarsbe.trough",
                  {"children": [(f"{orient}.Scrollbarsbe.thumb",
                                 {"expand": "1", "sticky": sticky})],
                   "sticky": sticky})],
            )
            style.configure(
                f"{orient}.TScrollbar",
                background=colors["scroll_thumb"],
                troughcolor=colors["surface"],
                bordercolor=colors["surface"],
                lightcolor=colors["scroll_thumb"],
                darkcolor=colors["scroll_thumb"],
                relief="flat",
                width=self.px(8),
                arrowsize=self.px(10),
            )
            style.map(
                f"{orient}.TScrollbar",
                background=[
                    ("active", colors["scroll_thumb_hover"]),
                    ("pressed", colors["border"]),
                    ("disabled", colors["surface_sunken"]),
                ],
            )

    # ------------------------------------------------- 勾选框渲染指示器
    def _install_checkbutton_indicator(self) -> None:
        """把 ttk 勾选框的原生方块指示器换成与表格行首一致的渲染图标。"""
        style, colors = self.style, COLORS
        try:
            on = self.checkbox(True)
            off = self.checkbox(False)
            on_dis = self.checkbox(True, disabled=True)
            off_dis = self.checkbox(False, disabled=True)
            on_down = self.checkbox(True, pressed=True)  # 按下态：勾色用 accent_pressed，比选中态更深（浅底铁律：向深色变化）
            # 未选中悬停：描边加深一档（浅底铁律：反馈一律向深色变化）
            off_hover = self.checkbox(False, hover=True)
            self._keep_refs += [on, off, on_dis, off_dis, on_down, off_hover]
            style.element_create(
                "Check.indicator", "image",
                off,
                ("pressed selected", on_down),
                ("selected disabled", on_dis),
                ("selected", on),
                ("disabled", off_dis),
                ("active", off_hover),
            )
            style.layout(
                "TCheckbutton",
                [("Checkbutton.padding", {"sticky": "nswe", "children": [
                    ("Checkbutton.focus", {"side": "left", "sticky": "", "children": [
                        ("Check.indicator", {"side": "left", "sticky": ""}),
                        ("Checkbutton.label", {"side": "left", "sticky": ""}),
                    ]}),
                ]})],
            )
        except tk.TclError:
            # 回退：保留 clam 原生方块指示器（配色已在上面 map 里定义）
            pass

    # ------------------------------------------------- 下拉框渲染箭头
    def _install_combobox_chevron(self) -> None:
        """把 clam 下拉框的原生下箭头换成渲染的圆头 chevron（icons 管线）。

        原生 ``Combobox.downarrow`` 是位图化的老式箭头，且 clam 把它排在
        ``Combobox.field`` 外侧（视觉上脱框）。这里以 ``element_create`` +
        ``layout`` 重组：丢弃外侧箭头，把渲染图作为 field 的子元素贴右居中。
        任何一步 TclError 都整体回退为原生箭头（仅换色），不引入脆弱 hack。
        """
        style, colors = self.style, COLORS
        try:
            layout = style.layout("TCombobox")
            if "Combobox.downarrow" not in str(layout):
                return
            states = (
                colors["text_dim"],      # 常态
                colors["text"],          # 悬停加深
                colors["text_disabled"], # 禁用
            )
            imgs = [
                # units 是「16 单位画板」的设计单位，IconFactory._make() 内部已按
                # scale 缩放（size = max(12, round(units * scale))）。此处**不要**
                # 再过 px()，否则高 DPI 下二次缩放：150% 时 px(12)=18 → 内部再乘
                # 1.5 → 实渲染 27px，与设计意图（12 逻辑 px）差一倍多。
                self._icons.chevron_down(units=12, color=_hex2rgb(c))
                for c in states
            ]
            self._keep_refs += imgs
            style.element_create(
                "Combo.chevron", "image",
                imgs[0],
                ("active", imgs[1]),
                ("disabled", imgs[2]),
            )

            def rebuild(nodes: list) -> list:
                out = []
                for name, opts in nodes:
                    opts = dict(opts)
                    children = opts.get("children")
                    if children:
                        opts["children"] = rebuild(children)
                    if name == "Combobox.downarrow":
                        continue          # 丢弃外侧的原生箭头
                    if name == "Combobox.field":
                        arrow = ("Combo.chevron", {"side": "right", "sticky": "e"})
                        opts["children"] = [arrow] + list(children or [])
                    out.append((name, opts))
                return out

            style.layout("TCombobox", rebuild(layout))
        except tk.TclError:
            # 回退：保留原生箭头（arrowcolor 已在上面 map 里定义）
            pass

    # ---------------------------------------------------------- 便捷方法
    def apply_treeview_row_tags(self, tree: ttk.Treeview) -> None:
        # ⚠️ 优先级规则（实测）：ttk 标签冲突时**先定义的赢**，与 item 的 tags
        # 列表顺序无关（tags=('zebra','mark') 仍以先 tag_configure 的那个生效）。
        # 因此「本次将转移」的 pending 高亮必须**先于**斑马纹 odd/even 定义，
        # 否则永远被斑马纹盖掉——用户看到的就是「说好的浅蓝底没出现」。
        tree.tag_configure("pending", background=COLORS["accent_soft"])
        tree.tag_configure("odd", background=COLORS["row_alt"])
        tree.tag_configure("even", background=COLORS["surface"])
        tree.tag_configure("disabled", foreground=COLORS["text_disabled"])
        tree.tag_configure("active", foreground=COLORS["accent"])
        tree.tag_configure("done", foreground=COLORS["success"])
        tree.tag_configure("failed", foreground=COLORS["danger"])


class ToolTip:
    """深色悬浮提示：悬停约 600ms 后出现，移开即消失。

    纯标准库实现（Toplevel + overrideredirect）。深色实底是整个界面里
    唯一的「浮层」用色，靠色调差表达「我在最上面」，不画多余描边。

    两种用法：
    - **固定文案**（按钮）：构造时给 ``text``，浮层贴在控件下缘。
    - **动态文案**（表格单元格）：给 ``provider(event)``，返回 ``None`` 表示此处
      不需要提示；文案随鼠标所在格子变化，浮层跟随指针，避免跑出窗口外。
    """

    def __init__(self, widget: tk.Widget, text: str = "", delay: int = 600,
                 theme: Optional[Theme] = None,
                 provider: Optional[Callable[[object], Optional[str]]] = None,
                 at_pointer: bool = False):
        self.widget = widget
        self.text = text
        self.delay = delay
        self.provider = provider
        self.at_pointer = at_pointer
        # 可选主题：传入则像素与字号走设计令牌（与主界面同一套缩放/字号层级）。
        self.theme = theme
        self._after_id: Optional[str] = None
        self._tip: Optional[tk.Toplevel] = None
        self._label: Optional[tk.Label] = None
        self._motion_text: Optional[str] = None
        widget.bind("<Enter>", self._schedule, add="+")
        widget.bind("<Leave>", self._hide, add="+")
        widget.bind("<ButtonPress>", self._hide, add="+")
        widget.bind("<Destroy>", self._hide, add="+")
        if provider is not None:
            widget.bind("<Motion>", self._on_motion, add="+")

    def set_text(self, text: str) -> None:
        """更新文案（用于随状态的可用性原因：未勾选 / 无目标盘 / 正在执行）。"""
        self.text = text

    def _on_motion(self, event) -> None:
        """鼠标在控件内移动：按 provider 解析当前应有的提示文案。"""
        try:
            text = self.provider(event) if self.provider is not None else None
        except tk.TclError:
            text = None
        if text == self._motion_text:
            # 同一格内继续移动：不重置计时，否则提示永远等不到那一刻
            return
        self._motion_text = text
        if not text:
            self._hide()
            return
        self.text = text
        if self._tip is not None:
            self._show()        # 浮层已显示：立刻换文案，不必再等一个 delay
        else:
            self._schedule()

    def _schedule(self, _event=None) -> None:
        self._cancel()
        self._after_id = self.widget.after(self.delay, self._show)

    def _cancel(self) -> None:
        if self._after_id is not None:
            try:
                self.widget.after_cancel(self._after_id)
            except tk.TclError:
                pass
            self._after_id = None

    def _px_fn(self):
        """像素缩放函数：有主题则用主题的 DPI 缩放，否则从控件自身推导等价实现。"""
        if self.theme is not None:
            return self.theme.px
        try:
            scale = max(1.0, self.widget.winfo_fpixels("1i") / 96.0)
        except tk.TclError:
            scale = 1.0

        def px(v: float, _s: float = scale) -> int:
            return max(1, round(v * _s))

        return px

    def _font(self):
        """浮层字号：走主题字号层级，或按控件 DPI 折算等价字号。"""
        if self.theme is not None:
            return self.theme.fonts["small"]
        px = self._px_fn()
        family = pick_font(self.widget, FONT_FAMILY_CANDIDATES)
        return (family, -max(1, px(13)))

    def _geometry(self):
        """浮层位置（屏幕坐标）：跟随指针或贴在控件下缘；返回 None 表示不可用。"""
        px = self._px_fn()
        try:
            if self.at_pointer:
                x = self.widget.winfo_pointerx() + px(14)
                y = self.widget.winfo_pointery() + px(20)
            else:
                x = self.widget.winfo_rootx()
                y = self.widget.winfo_rooty() + self.widget.winfo_height() + px(6)
            screen_w = self.widget.winfo_screenwidth()
            screen_h = self.widget.winfo_screenheight()
        except tk.TclError:
            return None
        # 贴到屏幕边缘时收回可视区（浮层尺寸未知，留保守余量）
        return max(0, min(x, screen_w - px(120))), max(0, min(y, screen_h - px(60)))

    def _show(self) -> None:
        geometry = self._geometry()
        if geometry is None:
            return
        x, y = geometry
        px = self._px_fn()
        font = self._font()
        if self._tip is not None:
            # 浮层已存在：换文案 + 挪位置即可（表格里换格时不该重开窗口，否则闪）
            if self._label is not None and self._label.cget("text") != self.text:
                self._label.configure(text=self.text)
            try:
                self._tip.wm_geometry(f"+{x}+{y}")
            except tk.TclError:
                pass
            return
        self._tip = tk.Toplevel(self.widget)
        self._tip.wm_overrideredirect(True)
        self._tip.wm_geometry(f"+{x}+{y}")
        self._label = tk.Label(
            self._tip, text=self.text, justify="left",
            background=COLORS["tooltip_bg"], foreground=COLORS["tooltip_fg"],
            relief="flat", borderwidth=0,
            font=font,
            padx=px(10), pady=px(6), wraplength=px(280),
        )
        self._label.pack()

    def _hide(self, _event=None) -> None:
        self._cancel()
        self._motion_text = None
        if self._tip is not None:
            try:
                self._tip.destroy()
            except tk.TclError:
                pass
            self._tip = None
            self._label = None
