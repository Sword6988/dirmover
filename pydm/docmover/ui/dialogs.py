"""主题化模态弹窗（替代 ``tkinter.messagebox``）。

为什么不用原生 messagebox：它的布局由 Tk 内建实现——Windows 上正文**居中**
显示、标题与正文无层级区分、按钮与内容的间距固定，且全部不可定制，
与本主题的设计令牌（间距刻度、字号层级、按钮样式）完全脱节。

这里用 ``Toplevel`` 自绘，层级结构：

    ┌─ 1px hairline 描边（全尺寸 Frame 的 highlight 边）─┐
    │ (图标)  标题（h2 加粗，左对齐）                      │
    │         正文段落 1（主文字色）                       │
    │         正文段落 2 …（次级文字色，按空行分段）        │
    │                              [次要按钮] [主按钮]    │
    └──────────────────────────────────────────────────┘

- 底色为 surface（白色卡片感），外圈 1px hairline 描边；
- 图标按 kind 渲染（``icons.IconFactory.status``）：软色圆底 + 语义色符号；
- 正文左对齐、按空行分段、段间留白统一，首段主色、后续段落降一档；
- 按钮行右对齐、统一宽度，主按钮用 Accent 样式锚定视觉终点；
- Return 触发默认按钮，Escape / 关闭按钮等同「取消」语义；
- 模态：``transient`` + ``grab_set`` + ``wait_window``。

**父窗口不可见时的退化**：Tk 的 ``wm transient`` 有个硬约束——master 未映射时，
设了 transient 的子窗口**永远不会被映射**（实测 ``winfo_ismapped()`` 恒为 0），
弹窗会「不出现」而不是「显示异常」。因此父窗口不可见（隐藏根窗口、启动早期）
时自动跳过 ``transient``，并改为**按屏幕居中**（``_center_on_parent`` 的退化分支），
供「没有主窗口也要弹提示」的场景复用。

对外 API 与 ``messagebox`` 一一对应（main_window 全部 15 处调用点）：
``show_info`` / ``show_warning`` / ``show_error`` / ``ask_yes_no`` /
``ask_yes_no_cancel``。
"""

from __future__ import annotations

import tkinter as tk
import tkinter.font as tkfont
from tkinter import ttk
from typing import Optional, Sequence, Tuple

from .icons import IconFactory
from .theme import COLORS, SP_LG, SP_MD, SP_SM, SP_XL

# 按钮宽度（文本字符数）：统一宽度让「是 / 否 / 取消」大小一致
_BTN_WIDTH = 9
# 正文折行宽度（逻辑像素）
_WRAP_UNITS = 420


def _scale_of(parent: tk.Misc) -> float:
    try:
        return max(1.0, parent.winfo_fpixels("1i") / 96.0)
    except tk.TclError:
        return 1.0


def _root_of(widget: tk.Misc) -> tk.Misc:
    """沿 ``master`` 上溯到 Tk 根窗口（不用 ``winfo_toplevel``：弹窗的父是
    Toplevel 时它返回的是那个 Toplevel，缓存会退化成"每个弹窗一份"）。"""
    root = widget
    while root.master is not None:
        root = root.master
    return root


def _icon_factory(parent: tk.Misc) -> IconFactory:
    """按 **Tk 根窗口**缓存的图标工厂（审计 C12）。

    原先每个弹窗都 ``IconFactory(self.tl, scale)``，实例用完即弃 → 同一张 44px
    状态图标每次弹窗都要重跑一遍纯 Python 超采样渲染（实测 17.7ms；150% DPI 下
    66px 为 39.4ms），加上 PNG 编码与 PhotoImage 创建，弹窗因此多花 30–40ms 的
    主线程时间。

    缓存挂在根窗口的私有属性上：``PhotoImage`` 与 Tk 解释器绑定，生命周期必须
    跟随 root，**不能跨解释器复用**；scale 变化（理论上只有换显示器才会发生）即
    重建。缓存键仍是 ``(图标名, 像素尺寸)``，不会串图。
    """
    root = _root_of(parent)
    scale = _scale_of(parent)
    cached = getattr(root, "_docmover_icon_factory", None)
    if cached is not None and cached[0] == scale:
        return cached[1]
    factory = IconFactory(root, scale)
    root._docmover_icon_factory = (scale, factory)
    return factory


def _base_font(parent: tk.Misc) -> object:
    """取主题注册的基础字体（样式全局注册，从 ttk 读回）。

    返回 ``tkfont.Font`` 对象（而非元组），供标题派生放大加粗版；
    主题未注册（如纯测试环境）时退回系统默认字体。
    """
    raw = ""
    try:
        looked = ttk.Style(parent).lookup("TButton", "font")
        raw = str(looked) if looked else ""
    except tk.TclError:
        pass
    try:
        return tkfont.Font(root=parent, font=raw or "TkDefaultFont")
    except tk.TclError:
        return tkfont.nametofont(parent, "TkDefaultFont")


def _title_font(parent: tk.Misc) -> tkfont.Font:
    """基础字体的放大加粗版（弹窗标题层级）。

    **单位说明（审计 C9）**：``tkfont.Font.actual("size")`` 返回的是**点值**，不是
    配置时用的像素——base 配成 ``-14``（14 逻辑像素）时这里读出 ``11``。因此下面
    按点值放大后用**正数（点）**写回，并不沿用主题"负数 = 像素"的约定（原注释
    声称"保持同一约定"，与事实不符）。倍率 1.21 对齐主题 ``h2/base`` 的视觉比例
    （h2 目标 17 逻辑像素 / base 14 逻辑像素）：1.0 缩放下 13pt ≈ 17.3px，与 h2 的
    17px 相差 0.33px，肉眼不可辨，故保持算式不变、只把语义写清楚。

    主题未注册（纯测试环境）时退回系统默认字体的同比例放大。
    """
    base = _base_font(parent)
    title = tkfont.Font(root=parent, font=str(base))
    base_size_pt = title.actual("size")   # 点值：base 的 -14px 在这里读成 11pt
    title.configure(size=round(base_size_pt * 1.21), weight="bold")
    return title


class ModalDialog:
    """模态对话框。按钮以 ``(文字, 样式, 返回值)`` 三元组描述。"""

    def __init__(
        self,
        parent: tk.Misc,
        title: str,
        message: str,
        *,
        kind: str = "info",
        buttons: Sequence[Tuple[str, str, object]] = (("确定", "primary", None),),
        cancel_value: object = None,
        transient: bool = True,
    ):
        self._parent_toplevel = parent.winfo_toplevel()
        self._kind = kind
        self._buttons = tuple(buttons)
        self._cancel_value = cancel_value
        self.result: object = None

        px = lambda v: max(1, round(v * _scale_of(parent)))  # noqa: E731

        self.tl = tk.Toplevel(self._parent_toplevel)
        self.tl.title(title)
        # 父窗口不可见时不设 transient：Tk 规定 master 未映射的 transient 子窗口
        # 不会被映射，设了反而让弹窗彻底不出现（见模块 docstring）。
        if transient and self._parent_toplevel.winfo_ismapped():
            self.tl.transient(self._parent_toplevel)
        self.tl.resizable(False, False)
        # 白色卡片感：底色 surface；Toplevel 的窗口边框不可定制，外圈
        # 1px hairline 用一个全尺寸 Frame 的 highlight 边实现。
        self.tl.configure(background=COLORS["border_hair"])
        body = tk.Frame(
            self.tl, background=COLORS["surface"],
            highlightthickness=px(1),
            highlightbackground=COLORS["border_hair"],
            highlightcolor=COLORS["border_hair"],
        )
        body.pack(fill="both", expand=True)
        body.columnconfigure(1, weight=1)
        pad_out = px(SP_XL)   # 外框留白（逻辑 24）

        # ---- 左：语义图标（与标题首行对齐）
        # 走按根窗口缓存的工厂（C12）：同一张图标不重复渲染
        icon = _icon_factory(parent).status(kind, units=44)
        tk.Label(body, image=icon, background=COLORS["surface"]).grid(
            row=0, column=0, sticky="nw",
            padx=(pad_out, px(SP_LG)), pady=(pad_out, 0),
        )
        self._keep_icon = icon   # 防垃圾回收

        # ---- 右：标题 + 正文段落
        tk.Label(
            body, text=title, background=COLORS["surface"],
            foreground=COLORS["text"],
            font=_title_font(parent), anchor="w", justify="left",
        ).grid(row=0, column=1, sticky="ew", padx=(0, pad_out), pady=(pad_out, 0))

        paragraphs = [p for p in message.split("\n\n") if p] or [message]
        row = 1
        for i, para in enumerate(paragraphs):
            # 层级：首段用主文字色，后续段落降一档，视线自然聚焦首段
            tk.Label(
                body, text=para, background=COLORS["surface"],
                foreground=COLORS["text"] if i == 0 else COLORS["text_dim"],
                font=_base_font(parent), anchor="w", justify="left",
                wraplength=px(_WRAP_UNITS),
            ).grid(row=row, column=1, sticky="ew", padx=(0, pad_out),
                   pady=(px(SP_MD) if i == 0 else px(SP_SM), 0))
            row += 1

        # ---- 按钮行：右对齐、统一宽度（跨两列贴右缘，与正文同外框留白）
        bar = tk.Frame(body, background=COLORS["surface"])
        bar.grid(row=row, column=0, columnspan=2, sticky="ew",
                 padx=pad_out, pady=(px(SP_LG), px(SP_XL)))
        self._btn_widgets = []
        for text, style_key, value in self._buttons:
            style = {"primary": "Accent.TButton", "danger": "Danger.TButton"}.get(
                style_key, "TButton"
            )
            btn = ttk.Button(bar, text=text, style=style, width=_BTN_WIDTH,
                             command=lambda v=value: self._finish(v))
            btn.pack(side="right", padx=(px(SP_SM), 0))
            self._btn_widgets.append(btn)

        self._default_btn = self._btn_widgets[0]
        # Escape / 窗口关闭 = 取消语义（askyesno → 否；yesnocancel → 取消）
        self.tl.protocol("WM_DELETE_WINDOW", self._on_close)
        self.tl.bind("<Escape>", lambda _e: self._on_close())
        self.tl.bind("<Return>", lambda _e: self._default_btn.invoke())

    # ----------------------------------------------------------------
    def _finish(self, value: object) -> None:
        self.result = value
        self.tl.destroy()

    def _on_close(self) -> None:
        self._finish(self._cancel_value)

    def show(self) -> object:
        """构建完成并阻塞等待；返回被点击按钮的返回值。

        **抓取归还（审计 C2）**：弹窗可能被"套着"打开——例如用户点窗口关闭按钮
        弹出确认框（A，持 grab）时，周期性 ``after`` 在 A 的嵌套事件循环里跑完
        队列，又弹出一个结果框（B）。B 会接管 grab，而 Tk 在 B 被销毁时**不会**
        把抓取还给 A（实测 ``grab_current()`` 变成 None）——结果是 A 还留在屏幕上
        却不再模态：用户能点主窗口，甚至在 A 底下另起一轮转移。

        因此这里在夺取抓取前记下当时的持有者，关闭后（若它仍存在）还回去。
        """
        self.tl.deiconify()
        self._center_on_parent()
        prev = None
        try:
            prev = self.tl.grab_current()
        except tk.TclError:
            prev = None
        self.tl.grab_set()
        try:
            self._default_btn.focus_set()
        except tk.TclError:
            pass
        self.tl.wait_window()
        if prev is not None:
            try:
                if prev.winfo_exists():
                    prev.grab_set()
            except (tk.TclError, AttributeError):
                # 抢抓取的窗口已经不可用（或拿到的是窗口名而非控件）——放弃归还
                pass
        return self.result

    def _center_on_parent(self) -> None:
        self.tl.update_idletasks()
        try:
            px_root = self._parent_toplevel.winfo_rootx()
            py_root = self._parent_toplevel.winfo_rooty()
            pw = self._parent_toplevel.winfo_width()
            ph = self._parent_toplevel.winfo_height()
            w, h = self.tl.winfo_width(), self.tl.winfo_height()
        except tk.TclError:
            return
        # 父窗口不可见（未映射、尺寸未确定）时没有可用的参照矩形，
        # 退回按屏幕居中，否则会贴到屏幕角落甚至屏幕外。
        if pw <= 1 or ph <= 1 or not self._parent_toplevel.winfo_ismapped():
            sw = self.tl.winfo_screenwidth()
            sh = self.tl.winfo_screenheight()
            self.tl.geometry(f"+{max(0, (sw - w) // 2)}+{max(0, (sh - h) // 3)}")
            return
        x = px_root + max(0, (pw - w) // 2)
        y = py_root + max(0, (ph - h) // 3)   # 略偏上，符合视觉重心
        self.tl.geometry(f"+{x}+{y}")


# ---------------------------------------------------------------- 便捷 API
# 与原 messagebox 调用点一一对应；message 里用空行分段，弹窗会自动拉开段距。

def show_info(parent: tk.Misc, title: str, message: str) -> None:
    ModalDialog(parent, title, message, kind="info").show()


def show_success(parent: tk.Misc, title: str, message: str) -> None:
    ModalDialog(parent, title, message, kind="success").show()


def show_warning(parent: tk.Misc, title: str, message: str) -> None:
    ModalDialog(parent, title, message, kind="warning").show()


def show_error(parent: tk.Misc, title: str, message: str) -> None:
    ModalDialog(parent, title, message, kind="error").show()


def ask_yes_no(parent: tk.Misc, title: str, message: str,
               kind: str = "question") -> bool:
    result = ModalDialog(
        parent, title, message, kind=kind,
        buttons=(("是", "primary", True), ("否", "normal", False)),
        cancel_value=False,
    ).show()
    return bool(result)


def ask_yes_no_cancel(parent: tk.Misc, title: str, message: str,
                      kind: str = "warning") -> Optional[bool]:
    """返回 True（是）/ False（否）/ None（取消或直接关闭）。"""
    return ModalDialog(
        parent, title, message, kind=kind,
        buttons=(("是", "primary", True), ("否", "normal", False),
                 ("取消", "normal", None)),
        cancel_value=None,
    ).show()
