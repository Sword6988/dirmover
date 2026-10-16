"""主窗口：单屏布局（零导航）+ 后台执行 + 进度与结果。

界面结构（单列 7 行网格，见 docs/ui-skeleton.md §3.3）：

    行0 AdminBanner     权限提示条（非管理员时显示）
    行1 DestinationBar  目标根目录
    行2 分隔线
    行3 FolderTable     项目列表（唯一伸缩区，weight=1）
    行4 ActionBar       勾选控制 + 摘要 + 操作
    行5 分隔线
    行6 StatusBar       状态文字 + 进度 + 模式徽标

导航层级为「零导航（单屏）」：所有操作常驻首屏，不切页、不弹新窗口。
过程详情只写文件日志（``logs\\``），界面不提供日志查看区域；仅在出错/有失败项时，
状态栏出现一个「打开日志目录」的次要链接作为可操作出口。
"""

from __future__ import annotations

import os
import queue
import threading
import tkinter as tk
import traceback
from pathlib import Path
from tkinter import font as tkfont
from tkinter import ttk
from typing import Callable, Dict, List, Optional, Sequence

from .. import fsops, logging_setup, shell
from ..config import AppConfig, app_dir
from ..constants import (
    APP_TITLE,
    BACKUP_DIRNAME,
    DEFAULT_TARGET_LEAF,
    FOLDERS,
    FOLDERS_BY_KEY,
    LOG_DIRNAME,
    RESTART_EXPLORER_TIMEOUT,
    RESTART_RELAUNCH_WAIT,
    RESTART_TERM_WAIT,
)
from ..registry import RegistryStore
from ..transfer import (
    ST_FAILED,
    ST_OK,
    IntentJournal,
    PlanItem,
    STAGE_LABELS,
    TransferEngine,
    TransferReport,
    load_snapshot,
    restrict_registry_snapshot,
)
from . import columns, dialogs
from .theme import COLORS, SP_LG, SP_MD, SP_SM, SP_XL, SP_XS, ToolTip, Theme

# ---------------------------------------------------------------- 网格行号
# 单屏 7 行网格（结构性常量）。
_ROW_BANNER = 0        # AdminBanner（条件显示）
_ROW_DESTINATION = 1   # DestinationBar
_ROW_SEP_TOP = 2       # 分隔线
_ROW_TABLE = 3         # FolderTable（唯一 weight=1）
_ROW_ACTION = 4        # ActionBar
_ROW_SEP_BOTTOM = 5    # 分隔线
_ROW_STATUS = 6        # StatusBar
_ROW_COUNT = 7

# 退出时等待工作线程收尾的轮询参数（关窗不再立刻销毁窗口，见 _on_exit）
_EXIT_POLL_MS = 200
_EXIT_TIMEOUT_MS = 60000

# 重启资源管理器这一步在最坏情况下的阻塞时长（A-7/B-6）：由 shell.py 的同批
# 常量算出，界面文案不得再写死数字——否则改了等待时长，提示还在复述旧值。
_RESTART_TOTAL_WAIT = RESTART_EXPLORER_TIMEOUT + RESTART_TERM_WAIT + RESTART_RELAUNCH_WAIT

# 无可用目标盘时的状态栏提示。抽成常量是为了能"认出自己设过它"并在磁盘恢复
# 可用后撤回（审计 C7）——否则摘要说"有目标盘"、状态栏说"没有"，自相矛盾。
_NO_DESTINATION_HINT = "未检测到可用的目标磁盘（非系统固定盘），无法开始转移。"

# ---------------------------------------------------------------- 列宽自适应
# 参见 docmover/ui/columns.py 的模块说明（为什么不能让 Tk 的 stretch 管列宽）。
# 行首图标列宽（逻辑像素）= Item padding 6 + 图标 16 + 右留白 6，位置完全确定，
# 不参与自适应。
_COL0_WIDTH = 28
# 首帧占位列宽（逻辑像素）：自适应第一次计算完成前先顶上，避免出现 0 宽列。
_COL_PLACEHOLDER = 120
# 重排防抖（毫秒）。拖动窗口会连续触发 <Configure>，留一个合并窗口，
# 使一次拖动只重算一次列宽（每轮测量约 40 条文字，本就是毫秒级）。
_FIT_DEBOUNCE_MS = 40


class MainWindow:
    """应用主窗口。"""

    def __init__(
        self,
        root: tk.Tk,
        config: AppConfig,
        logger=None,
        dry_run: bool = False,
        log_dir: Optional[Path] = None,
        on_before_relaunch: Optional[Callable[[], None]] = None,
    ):
        self.root = root
        self.config = config
        self.log = logger or logging_setup.get_logger()
        self.dry_run = dry_run
        # 审计 C5：提权重启前要释放单实例互斥体，而互斥体句柄在 run.py 里。
        # 这里用**注入回调**而不是 ``import run``——脚本方式启动时 ``__main__``
        # 与 ``run`` 是两个模块对象，导入到的 ``_SINGLE_INSTANCE_HANDLE`` 不是
        # 同一个全局，释放动作会静默空转；注入既避免这个坑也避免循环导入。
        self._on_before_relaunch = on_before_relaunch
        # 审计 P2-22：日志目录可注入（默认仍为程序目录下的 logs\，生产行为不变）。
        # 测试传入临时目录即可避免把日志落到仓库 pydm\logs\。
        self.log_dir = Path(log_dir) if log_dir is not None else (app_dir() / LOG_DIRNAME)

        self.store = RegistryStore()
        self.backup_dir = app_dir() / BACKUP_DIRNAME
        self.engine = TransferEngine(
            store=self.store,
            backup_dir=self.backup_dir,
            dry_run=dry_run,
            logger=self.log,
        )
        # 直接实例化（未经 run.py）时保证日志可用；重复调用是安全的
        logging_setup.setup_logging(self.log_dir)

        self._checked: set[str] = set(config.selected)
        self._known_keys: set[str] = set()
        self._plans: Dict[str, PlanItem] = {}
        self._last_outcomes: Dict[str, str] = {}     # 上次执行结果（key → 状态）
        self._pending: set[str] = set()              # 确认弹窗期间高亮的待转移项
        self._has_destination = True                 # 是否存在可用的目标磁盘
        self._busy = False
        self._busy_label = "处理中…"                  # 不可中断任务的按钮文案
        self._cancelable = False                     # 当前任务是否可中断
        self._exit_requested = False                 # 用户已请求退出，等待收尾
        self._exit_waited = 0
        # 审计 R1：``_busy`` 在 UI 收尾的**第一行**就被复位，而之后还要弹结果窗、
        # 写状态栏、可能再问「是否重启资源管理器」。``_poll_exit`` 若只看 ``_busy``
        # 就会在这段 UI 序列**中间**销毁窗口（实测：弹窗按钮点下去时窗口已没，
        # 回调抛 TclError，用户一次也看不到总结）。本标志覆盖整段收尾 UI。
        self._finishing_ui = False
        self._cancel = threading.Event()
        self._queue: "queue.Queue" = queue.Queue()

        # 列宽自适应状态（见 ui/columns.py）
        self._col_fonts: Dict[str, object] = {}      # 量文字用的 Font 缓存（必须持引用）
        self._need_hwm: Dict[str, int] = {}          # 内容需要宽度的高水位：只涨不落，
        #   避免执行期间状态文案变短导致列宽抖动；数据刷新时清零重算。
        self._fit_after_id: Optional[str] = None     # 待触发的重排回调（防抖合并）
        self._last_tree_width: Optional[int] = None  # 上次重排时的表格宽度
        self._closing = False                        # 表格已销毁 → 不再排期重排

        # 审计 R3：Tk 回调异常必须有落点，否则 ``--windowed`` 下彻底无声。
        # 必须在 ``_build()`` 之前装好，构建期之后的任何回调都在覆盖范围内。
        root.report_callback_exception = self._on_tk_exception

        self.theme = Theme(root)
        self._build()
        self._post(self._refresh_rows)
        self._drain_queue()

        # 兜底分支，非死代码：交付产物因 build.py 的 --uac-admin（exe manifest 为
        # requireAdministrator）必然已提权，shell.is_admin() 恒为 True，故本分支
        # **在本 exe 内恒不显示**；它保留给「无清单构建 / 源码开发运行」这类未提权
        # 宿主——一旦去掉 --uac-admin，本分支立即恢复生效。
        if not shell.is_admin():
            self._show_admin_banner()

        # 上次若被中断（进程被杀/断电），启动后明确告知并提供回滚入口（P0-4）
        try:
            self.root.after(400, self._check_pending_intent)
        except tk.TclError:
            pass

    # ==================================================================
    # 界面构建
    # ==================================================================
    def _build(self) -> None:
        root = self.root
        root.title(APP_TITLE)
        px = self.theme.px
        root.minsize(px(780), px(640))
        root.geometry(f"{px(900)}x{px(700)}")
        try:
            root.iconbitmap(default=str(self._icon_path()))
        except (tk.TclError, OSError):
            pass

        root.columnconfigure(0, weight=1)
        # 单列 7 行网格：仅表格行 weight=1，其余为 0（窗口缩放时只有表格变高）
        for row in range(_ROW_COUNT):
            root.rowconfigure(row, weight=0)
        root.rowconfigure(_ROW_TABLE, weight=1)

        self._build_destination_bar()
        ttk.Separator(root, orient="horizontal").grid(
            row=_ROW_SEP_TOP, column=0, sticky="ew"
        )
        self._build_folder_table()
        self._build_action_bar()
        ttk.Separator(root, orient="horizontal").grid(
            row=_ROW_SEP_BOTTOM, column=0, sticky="ew"
        )
        self._build_statusbar()

        # 键盘通道：Ctrl+Return 触发主操作（表格内回车仍是切换勾选）
        root.bind("<Control-Return>", self._on_ctrl_enter)
        # 审计 P0-3：窗口右上角 X 必须与「退出程序」按钮走同一条路径，
        # 否则会绕过确认与取消，daemon 工作线程可能在 rmtree 中途被强杀。
        root.protocol("WM_DELETE_WINDOW", self._on_exit)

    def _icon_path(self) -> Path:
        import sys

        if getattr(sys, "frozen", False):
            base = Path(getattr(sys, "_MEIPASS", Path(sys.executable).parent))
        else:
            base = Path(__file__).resolve().parent.parent.parent
        return base / "assets" / "app.ico"

    # -------------------------------------------------- ② DestinationBar
    def _build_destination_bar(self) -> None:
        px = self.theme.px
        bar = ttk.Frame(self.root)
        bar.grid(
            row=_ROW_DESTINATION, column=0, sticky="ew",
            padx=px(SP_XL), pady=(px(SP_LG), px(SP_MD)),
        )
        bar.columnconfigure(1, weight=1)

        ttk.Label(bar, text="目标根目录").grid(
            row=0, column=0, sticky="w", padx=(0, px(SP_MD))
        )
        self.dest_var = tk.StringVar(value=self.config.destination)
        # 目标根目录只提供固定选项 <非系统固定盘>:\UserData
        self.dest_combo = ttk.Combobox(
            bar, textvariable=self.dest_var, height=12, state="readonly", width=30
        )
        self.dest_combo.grid(row=0, column=1, sticky="w")
        self.dest_combo.bind("<<ComboboxSelected>>", lambda _e: self._on_destination_changed())

        # 当前目标盘的可用空间：DriveInfo.label() 的数据原本只有 CLI 在用，
        # 界面补上只读回显（不占用有限的横向空间——贴右对齐，且选项文本保持不变）。
        self.lbl_space = ttk.Label(bar, text="", style="Muted.TLabel")
        self.lbl_space.grid(row=0, column=2, sticky="e")

        self._tip_dest = ToolTip(
            self.dest_combo,
            "目标根目录固定为各非系统固定盘的 UserData 文件夹。",
            theme=self.theme,
        )

    # ------------------------------------------------------ ③ FolderTable
    def _build_folder_table(self) -> None:
        px = self.theme.px
        table_card = ttk.Frame(self.root, style="Card.TFrame")
        table_card.grid(
            row=_ROW_TABLE, column=0, sticky="nsew",
            padx=px(SP_XL), pady=(0, px(SP_MD)),
        )
        table_card.columnconfigure(0, weight=1)
        table_card.rowconfigure(0, weight=1)

        columns_keys = columns.COLUMN_KEYS
        self.tree = ttk.Treeview(
            table_card,
            columns=columns_keys,
            show="tree headings",   # tree 列 = 行首勾选框图标列
            selectmode="browse",
            height=8,
        )
        # #0 列只承载勾选框图标，不显示文字。28 = 左留白 6（Treeview.Item
        # padding，theme 中已把 indicator 移除、位置完全确定）+ 图标 16 + 右 6；
        # anchor 用 w，保证图标左缘与名称列文字同一起线。
        self.tree.heading("#0", text="")
        self.tree.column("#0", width=px(_COL0_WIDTH), minwidth=px(_COL0_WIDTH),
                         anchor="w", stretch=False)
        # 列宽**不交给 Tk 的 stretch**：它只会把窗口差值平均分给每个可伸缩列，
        # 与内容无关（名称列白占一大块、路径列仍被裁）。宽度全部由
        # ``_apply_column_fit()`` 按「内容需要 + 角色配额」算出。占位列宽先顶上，
        # 首次重排发生在本方法返回后的第一个事件循环（构建期表格还没有数据）。
        for key, header, anchor, _quota, _locked in columns.COLUMNS:
            self.tree.heading(key, text=header, anchor=anchor)
            self.tree.column(key, width=px(_COL_PLACEHOLDER),
                             minwidth=px(columns.FLOOR_COL), anchor=anchor,
                             stretch=False)

        self.tree.grid(row=0, column=0, sticky="nsew",
                       padx=(px(1), 0), pady=(px(1), 0))
        scroll = ttk.Scrollbar(table_card, orient="vertical", command=self.tree.yview)
        scroll.grid(row=0, column=1, sticky="ns", pady=px(1), padx=(0, px(1)))
        self.tree.configure(yscrollcommand=scroll.set)
        self.theme.apply_treeview_row_tags(self.tree)

        # 交互绑定（左键 / 空格 / 回车切换勾选；双击打开位置的功能已按产品决策移除）
        self.tree.bind("<Button-1>", self._on_tree_click)
        self.tree.bind("<space>", self._on_tree_space)
        self.tree.bind("<Return>", self._on_tree_space)
        # 窗口 / 滚动条出现导致的宽度变化 → 重排列宽（防抖合并）
        self.tree.bind("<Configure>", self._on_tree_configure, add="+")
        self.tree.bind("<Destroy>", self._on_tree_destroy, add="+")
        # 内容比列宽长时，悬停给出完整文本（表格里显示值不变，只是补一条可读出口）
        self._tip_cell = ToolTip(self.tree, "", theme=self.theme, delay=450,
                                 provider=self._cell_tip_text, at_pointer=True)
        # 先按表头量一次：避免窗口第一帧出现「四列等宽占位」的闪烁
        # （数据行要到 _drain_queue 之后才填，那时会再按内容重排一次）
        self._apply_column_fit()

    # --------------------------------------------------- ③bis 列宽自适应
    # 模型：内容需要宽度决定下限（不裁切），视口宽度按角色配额分配（不留白）。
    # 触发点有三个——数据刷新、行内文案变化、表格宽度变化；三者都走防抖合并。
    def _tk_font(self, name: str):
        """量文字用的 Font 对象（缓存持有：measure 是高频调用）。"""
        font = self._col_fonts.get(name)
        if font is None:
            try:
                font = tkfont.Font(root=self.root, font=self.theme.fonts[name])
            except tk.TclError:
                return None
            self._col_fonts[name] = font
        return font

    def _cell_tip_text(self, event) -> Optional[str]:
        """悬停单元格的完整文本；内容未超出列宽时返回 ``None``（不打扰）。

        表格里的显示值**不做任何改写**（路径该是原样就原样），截断的内容通过
        这条悬浮提示补全——窄窗口下路径放不下时，信息仍然可达。
        """
        try:
            row = self.tree.identify_row(event.y)
            col_id = self.tree.identify_column(event.x)
        except tk.TclError:
            return None
        if not row or not col_id:
            return None
        try:
            index = int(col_id[1:]) - 1          # "#3" → 2
        except ValueError:
            return None
        if index < 0 or index >= len(columns.COLUMN_KEYS):
            return None
        key = columns.COLUMN_KEYS[index]
        values = self.tree.item(row, "values")
        if index >= len(values):
            return None
        text = str(values[index])
        try:
            width = int(self.tree.column(key, "width"))
        except tk.TclError:
            return None
        need = (columns.measure_text(self._tk_font("base"), text)
                + self.theme.px(columns.CELL_PAD))
        if need <= width:
            return None
        return f"{columns.HEADERS[key]}：{text}"

    def _on_tree_configure(self, event=None) -> None:
        """表格宽度变化（窗口缩放 / 滚动条出现）→ 重排列宽。"""
        width = getattr(event, "width", None)
        if width is not None and width == self._last_tree_width:
            return          # 高度变化不改变横向分配，跳过
        self._last_tree_width = width
        self._schedule_column_fit(_FIT_DEBOUNCE_MS)

    def _schedule_column_fit(self, delay: int = 0) -> None:
        """把连续触发合并成一次重排（拖动窗口时不逐帧重算）。"""
        if self._fit_after_id is not None or self._closing:
            return
        try:
            self._fit_after_id = self.root.after(delay, self._run_column_fit)
        except tk.TclError:
            self._fit_after_id = None

    def _on_tree_destroy(self, event=None) -> None:
        """表格销毁时撤掉待触发的重排。

        销毁过程中控件仍会发 ``<Configure>``，若不收回已排期的回调，Tcl 会在
        解释器收尾时打印 ``invalid command name ..._run_column_fit``——这种噪声
        会掩盖真正的错误（退出路径上的日志是排查失败的唯一依据）。
        """
        if event is not None and getattr(event, "widget", None) is not self.tree:
            return
        self._closing = True
        if self._fit_after_id is not None:
            try:
                self.root.after_cancel(self._fit_after_id)
            except tk.TclError:
                pass
            self._fit_after_id = None

    def _run_column_fit(self) -> None:
        self._fit_after_id = None
        try:
            self._apply_column_fit()
        except tk.TclError:
            # 窗口已在退出路径上销毁：重排没有意义，也不该污染退出流程
            pass

    def _table_viewport(self) -> int:
        """文本区可用宽度（物理像素）= 表格宽度 − 行首图标列 − 边框。"""
        px = self.theme.px
        try:
            col0 = int(self.tree.column("#0", "width"))
            width = int(self.tree.winfo_width())
        except tk.TclError:
            col0, width = px(_COL0_WIDTH), 0
        if width <= 1:
            # 未映射（构建期 / 无图形会话的测试）：按最小窗口下的可用宽保守排布。
            # 一旦映射，<Configure> 会立刻用真实宽度重排，不会停留在保守值上。
            width = px(columns.FALLBACK_VIEWPORT) + col0
        return max(width - col0 - px(2), 0)

    def _column_needs(self) -> Dict[str, int]:
        """每列的内容需要宽度（物理像素）：表头与所有行取最大，且只涨不落。

        「只涨不落」让执行期间状态文案变短（「处理中…」）时列宽不抖动；数据刷新
        时由 ``_refresh_rows`` 清零重算，避免换目标盘后旧的长路径把列撑住不放。
        """
        px = self.theme.px
        base_font = self._tk_font("base")
        head_font = self._tk_font("h3")
        needs: Dict[str, int] = {}
        for key, header, _anchor, _quota, _locked in columns.COLUMNS:
            needs[key] = (columns.measure_text(head_font, header)
                          + px(columns.HEAD_PAD))
        for iid in self.tree.get_children():
            values = self.tree.item(iid, "values")
            for index, (key, *_rest) in enumerate(columns.COLUMNS):
                if index >= len(values):
                    continue
                need = (columns.measure_text(base_font, str(values[index]))
                        + px(columns.CELL_PAD))
                if need > needs[key]:
                    needs[key] = need
        for key, need in needs.items():
            self._need_hwm[key] = max(self._need_hwm.get(key, 0), need)
        return {key: need for key, need in self._need_hwm.items()}

    def _column_widths(self, viewport: int) -> Dict[str, int]:
        """把可用宽度分给各列：保护列不低于内容需要，路径列按配额吃掉余量。"""
        px = self.theme.px
        needs = self._column_needs()
        keys = list(columns.COLUMN_KEYS)
        minimums: List[int] = []
        quotas: List[int] = []
        for key, _header, _anchor, quota, locked in columns.COLUMNS:
            # 保护列（名称 / 状态）下限 = 内容需要宽度：窗口再小也不裁字。
            # 路径列下限为硬下限：可以被压缩，完整内容由悬停提示兜底。
            hard = px(columns.MIN_COL)
            # 保护列还要再加一道硬下限兜底：字体度量偏小的机器上（例如缺中文首选
            # 字体、只能回退到西文字体的环境），measure 会把中文字形量得过窄，
            # 只按 need 取下限会退化出几十像素的窄列——此时测量与渲染所用的
            # 字体并不一致，硬下限保证列宽不低于「一行可读文本」的常识下限。
            minimums.append(max(needs.get(key, 0), hard) if locked else hard)
            quotas.append(px(quota))
        fit = columns.fit_columns([needs.get(k, 0) for k in keys],
                                  minimums, quotas, viewport)
        return dict(zip(keys, fit))

    def _apply_column_fit(self, viewport: Optional[int] = None) -> None:
        """重排一次列宽。``viewport`` 可注入，便于测试在未映射窗口上验证分配。"""
        try:
            if not self.tree.winfo_exists():
                return
        except tk.TclError:
            return
        if viewport is None:
            viewport = self._table_viewport()
        for key, width in self._column_widths(viewport).items():
            try:
                if int(self.tree.column(key, "width")) != int(width):
                    self.tree.column(key, width=int(width))
            except tk.TclError:
                return

    # -------------------------------------------------------- ④ ActionBar
    def _build_action_bar(self) -> None:
        px = self.theme.px
        bar = ttk.Frame(self.root)
        bar.grid(
            row=_ROW_ACTION, column=0, sticky="ew",
            padx=px(SP_XL), pady=(0, px(SP_MD)),
        )
        bar.columnconfigure(1, weight=1)

        # ---- 行 A：全选 / 提示 + 摘要 + 主操作（动线从左到右）
        self.select_all_var = tk.BooleanVar(value=False)
        self.chk_select_all = ttk.Checkbutton(
            bar, text="全选 / 全不选", variable=self.select_all_var,
            command=self._on_select_all,
        )
        self.chk_select_all.grid(row=0, column=0, sticky="w")

        # （操作提示标签已按产品决策移除：界面元素自明，无需常驻说明文字）

        self.lbl_summary = ttk.Label(bar, text="", style="Summary.TLabel")
        self.lbl_summary.grid(row=0, column=2, sticky="e", padx=(px(SP_XL), 0))

        self.btn_start = ttk.Button(
            bar, text="开始转移", style="Accent.TButton", command=self._on_start
        )
        self.btn_start.grid(row=0, column=3, sticky="e", padx=(px(SP_LG), 0))

        # ---- 行 B：选项 + 危险操作 + 退出（与主按钮拉开横向距离）
        # 注意：variable 必须保存实例引用，否则 BooleanVar 被垃圾回收后
        # Tcl 变量被注销，勾选切换将静默失效（保证勾选语义生效的关键修复）。
        self.create_user_dir_var = tk.BooleanVar(value=self.config.create_user_dir)
        self.chk_create_user_dir = ttk.Checkbutton(
            bar, text="创建当前用户目录",
            variable=self.create_user_dir_var,
            command=self._on_create_user_dir,
        )
        self.chk_create_user_dir.grid(
            row=1, column=0, columnspan=2, sticky="w", pady=(px(SP_SM), 0)
        )

        self.btn_restore = ttk.Button(
            bar, text="恢复系统默认值", style="Danger.TButton",
            command=self._on_restore_defaults,
        )
        self.btn_restore.grid(row=1, column=2, sticky="e", pady=(px(SP_SM), 0))

        self.btn_exit = ttk.Button(bar, text="退出程序", command=self._on_exit)
        self.btn_exit.grid(
            row=1, column=3, sticky="e", padx=(px(SP_MD), 0), pady=(px(SP_SM), 0)
        )

        # ---- 悬浮提示：补充界面正文放不下的安全说明
        # 主按钮提示随状态变化（未勾选 / 无目标盘 / 执行中 / 停止），
        # 让「为什么不能点」永远有解释，而不是一个沉默的灰按钮。
        self._tip_start = ToolTip(self.btn_start, self._primary_tip_text(),
                                  theme=self.theme)
        ToolTip(self.btn_restore, "完整恢复为系统默认：先把内容搬回系统默认目录，\n"
                                  "再把注册表指回该目录。\n"
                                  "复制并全量校验通过后才删除原目录；改动前自动留存快照。",
                theme=self.theme)

    # -------------------------------------------------------- ⑤ StatusBar
    def _build_statusbar(self) -> None:
        px = self.theme.px
        bar = ttk.Frame(
            self.root, style="Status.TFrame",
            padding=(px(SP_XL), px(SP_SM), px(SP_XL), px(SP_MD)),
        )
        bar.grid(row=_ROW_STATUS, column=0, sticky="ew")
        bar.columnconfigure(0, weight=1)

        self.status_var = tk.StringVar(value="就绪")
        ttk.Label(bar, textvariable=self.status_var, style="Muted.TLabel").grid(
            row=0, column=0, sticky="w"
        )

        # 失败/异常后的可操作出口：仅在需要时出现（默认收起，不占常驻空间）
        self.btn_open_logs = ttk.Button(
            bar, text="打开日志目录", style="Link.TButton", command=self._open_logs_dir
        )
        self.btn_open_logs.grid(row=0, column=1, sticky="w", padx=(px(SP_MD), 0))
        self.btn_open_logs.grid_remove()
        ToolTip(self.btn_open_logs, "失败项目的完整原因记录在日志文件里。",
                theme=self.theme)

        self.progress = ttk.Progressbar(bar, mode="determinate", length=px(220))
        self.progress.grid(row=0, column=2, sticky="e", padx=(px(SP_LG), 0))

        # 原「试运行模式」徽标：随头部一起移到状态栏
        self.badge = ttk.Label(bar, text="", style="Muted.TLabel")
        self.badge.grid(row=0, column=3, sticky="e", padx=(px(SP_LG), 0))
        if self.dry_run:
            self.badge.configure(text="试运行模式", style="Warning.TLabel")

    # ==================================================================
    # 数据刷新
    # ==================================================================
    def _current_destination(self) -> str:
        return self.dest_var.get().strip()

    def _create_user_dir_enabled(self) -> bool:
        return bool(self.create_user_dir_var.get())

    def _refresh_rows(self) -> None:
        """重新读取当前位置并刷新表格（不扫描目录规模，保证流畅）。"""
        dest = self._current_destination()
        plans = self.engine.build_plan(
            [spec.key for spec in FOLDERS],
            dest,
            self._create_user_dir_enabled(),
            scan=False,
        )
        self._plans = {item.key: item for item in plans}

        self.tree.delete(*self.tree.get_children())
        for index, item in enumerate(plans):
            self.tree.insert("", "end", iid=item.key, image=self._checkbox_image(item.key),
                             values=self._row_values(item),
                             tags=self._row_tags(item, index))

        self._known_keys = {item.key for item in plans}
        self._checked &= self._known_keys
        # 内容整体换了一批（路径 / 目标都变了）：内容需要宽度的高水位必须清零，
        # 否则上一次的长路径会把列宽一直撑住（见 _column_needs）。
        self._need_hwm.clear()
        # 先判定目标盘可用性：摘要文案与主按钮可用态都依赖它
        self._refresh_destination_choices()
        self._render_marks()
        self._update_summary()
        self._schedule_column_fit()

    def _checkbox_image(self, key: str) -> object:
        """行首勾选框图标：与 ttk 勾选框共用同一套渲染图标。"""
        return self.theme.checkbox(key in self._checked)

    def _row_values(self, item: PlanItem) -> tuple:
        return (
            item.display,
            item.current or "（未确定）",
            item.target or "（未设置目标）",
            item.describe(),
        )

    def _row_tags(self, item: PlanItem, index: int) -> tuple:
        tags = ["odd" if index % 2 else "even"]
        if item.key not in self._checked:
            tags.append("disabled")
        # 上一次执行的结果着色（完成=绿 / 失败=红），优先级最高故放在末尾
        last = self._last_outcomes.get(item.key)
        if last == ST_OK:
            tags.append("done")
        elif last == ST_FAILED:
            tags.append("failed")
        # 确认弹窗期间标出本次将被转移的行
        if item.key in self._pending:
            tags.append("pending")
        return tuple(tags)

    def _render_marks(self) -> None:
        """只更新勾选图标与状态列，避免整表重建造成闪烁。"""
        for key, item in self._plans.items():
            if not self.tree.exists(key):
                continue
            self.tree.item(key, image=self._checkbox_image(key))
            self.tree.set(key, "target", item.target or "（未设置目标）")
            self.tree.set(key, "status", item.describe())
            index = self.tree.index(key)
            self.tree.item(key, tags=self._row_tags(item, index))
        # 状态列文案随勾选 / 执行进度变化：宽度重新算（高水位保证只涨不抖）
        self._schedule_column_fit()

    def _refresh_destination_choices(self) -> None:
        """固定选项：每个非系统固定磁盘的 ``X:\\UserData``。

        选项文本必须保持 ``X:\\UserData`` 形态（测试与界面契约都依赖它），
        可用空间改为旁挂只读标签 + 悬浮提示，既补上信息又不改契约。
        """
        drives = fsops.list_fixed_drives()
        system = fsops.system_drive()
        candidates = [d for d in drives if d.letter.upper() != system]
        options = [f"{d.letter.upper()}\\{DEFAULT_TARGET_LEAF}" for d in candidates]
        self._has_destination = bool(options)

        self.dest_combo.configure(values=options)

        if not options:
            # 空态：没有任何非系统固定盘 —— 明确告知，而不是留一个空下拉
            self.dest_var.set("")
            self.lbl_space.configure(text="未检测到可用的非系统固定磁盘")
            self._tip_dest.set_text(
                "没有可用的目标磁盘：本工具只使用非系统固定盘（U 盘/光驱/网络盘不算）。\n"
                "接入或挂载一个固定磁盘后，重启程序即可选择。"
            )
            self._notify_no_destination()
            self._update_start_state()
            return

        current = self.dest_var.get().strip()
        if current not in options:
            # 默认 D:\UserData；该盘不存在时回落到首个可用选项
            preferred = f"D:\\{DEFAULT_TARGET_LEAF}"
            self.dest_var.set(preferred if preferred in options else options[0])
            self._post(self._refresh_rows)

        # 磁盘回来了：状态栏的"无可用目标磁盘"必须撤回，否则与摘要自相矛盾（C7）
        self._clear_no_destination_hint()
        self._update_space_label(candidates)
        self._tip_dest.set_text(
            "目标根目录固定为各非系统固定盘的 UserData 文件夹。\n\n各盘当前可用空间：\n"
            + "\n".join(f"  {d.label()}" for d in candidates)
        )

    def _update_space_label(self, candidates) -> None:
        """回显当前所选目标盘的可用/总容量（直接用 DriveInfo.label() 的既有格式化）。"""
        letter = (self.dest_var.get().strip()[:2] or "").upper()
        for drive in candidates:
            if drive.letter.upper() == letter:
                self.lbl_space.configure(text=drive.label())
                return
        self.lbl_space.configure(text="")

    def _notify_no_destination(self) -> None:
        """无可用目标盘时给一次显式提示（状态栏 + 摘要），恢复后自动撤回。"""
        if self.status_var.get() != _NO_DESTINATION_HINT:
            self.status_var.set(_NO_DESTINATION_HINT)

    def _clear_no_destination_hint(self) -> None:
        """磁盘恢复可用后撤回状态栏的空态提示（审计 C7）。

        只撤自己设过的那一条：用户在无盘期间可能已经看到别的状态文案
        （例如"已打开日志目录"、"正在处理…"），不该被顺手清掉。
        """
        if self.status_var.get() == _NO_DESTINATION_HINT:
            self.status_var.set("就绪")

    def _update_summary(self) -> None:
        checked = [k for k in self._ordered_checked()]
        text = f"已选择 {len(checked)} / {len(FOLDERS)} 项"
        if not self._has_destination:
            text += "（无可用目标磁盘）"
        elif not checked:
            text += "（请先勾选项目）"
        # 规模预统计已按产品决策移除：勾选后零读盘；每项多大在执行时
        # 由引擎权威扫描并经空间闸门把关，不在列表里预览。
        self.lbl_summary.configure(text=text)
        self.select_all_var.set(len(checked) == len(FOLDERS) and len(FOLDERS) > 0)
        self._update_start_state()

    def _primary_tip_text(self) -> str:
        """主按钮提示随状态变化：可用性原因永远有解释。"""
        if self._busy:
            if self._cancelable:
                return ("停止当前任务：已完成的项目会保留，\n"
                        "未完成项目的源文件不会被删除，注册表也不会写入。")
            return "正在重启资源管理器，此操作不可中断，请稍候。"
        if not self._has_destination:
            return "未检测到可用的目标磁盘（非系统固定盘），请先接入或挂载。"
        if not self._checked:
            return "请先在列表中勾选至少一项需要转移的内容。"
        return ("流程：先复制并校验，全部一致后才删除源目录；\n"
                "任何一步失败会自动回滚，已写入的注册表值也会还原。")

    def _update_start_state(self) -> None:
        """主按钮的唯一状态出口：空闲时是「开始转移」，执行中切换为「停止」。

        未勾选 / 无目标盘时禁用：把「点了才报错」前移为「不可点 + 原因可见」。
        """
        if not hasattr(self, "btn_start"):
            return
        if self._busy:
            if self._cancelable:
                text, state, cmd = "停止", "normal", self._on_stop
                if self._cancel.is_set() or self._exit_requested:
                    text, state = "正在停止…", "disabled"
            else:
                text, state, cmd = self._busy_label, "disabled", self._on_start
        else:
            text = "开始转移"
            cmd = self._on_start
            state = "normal" if (self._checked and self._has_destination) else "disabled"
        try:
            self.btn_start.configure(text=text, command=cmd, state=state)
        except tk.TclError:
            pass
        if hasattr(self, "_tip_start"):
            self._tip_start.set_text(self._primary_tip_text())

    def _ordered_checked(self) -> List[str]:
        return [spec.key for spec in FOLDERS if spec.key in self._checked]

    # ==================================================================
    # 交互
    # ==================================================================
    def _on_tree_click(self, event) -> Optional[str]:
        region = self.tree.identify_region(event.x, event.y)
        if region in ("heading", "separator"):
            # 列宽由内容自动分配，表头分隔线不支持拖拽：手动拖出来的宽度会在下一次
            # 重排（窗口缩放 / 数据刷新）时被覆盖，与其自相矛盾，不如明确不支持。
            # 返回 "break" 会拦掉 Tk 的类绑定，拖动行为不会启动。
            return "break"
        if region not in ("cell", "tree"):
            return None
        row = self.tree.identify_row(event.y)
        column = self.tree.identify_column(event.x)
        if not row:
            return None
        # 勾选框图标列（#0）与名称列（#1）切换勾选，其余列保持原生选中行为
        if column in ("#0", "#1"):
            self._toggle(row)
            return "break"
        return None

    def _on_tree_space(self, event) -> Optional[str]:
        # Ctrl+Return：不切换勾选，让全局「开始转移」快捷键生效
        if getattr(event, "state", 0) & 0x0004:
            return None
        for row in self.tree.selection() or ():
            self._toggle(row)
        if not self.tree.selection():
            focused = self.tree.focus()
            if focused:
                self._toggle(focused)
        return "break"

    def _toggle(self, key: str) -> None:
        # 忙碌期禁改勾选（审计 C6）：勾选项在 _on_start / _on_restore_defaults
        # 里已经定稿，此时改动只会让列表与"全选 / 创建用户目录 / 恢复默认值"
        # 这些已灰控件的口径不一致；顺带避免 _render_marks 把正在处理那一行的
        # "处理中…"写回旧描述（进度反馈被抹掉一行）。
        if self._busy:
            return
        if key not in self._known_keys:
            return
        if key in self._checked:
            self._checked.discard(key)
        else:
            self._checked.add(key)
        self._render_marks()
        self._update_summary()

    def _on_select_all(self) -> None:
        if self._busy:
            # 忙碌期忽略（勾选项已定稿）；但要把复选框显示同步回真实状态，
            # 否则会出现"复选框看着已勾全选、实际没生效"的假象（审计 C6）
            self.select_all_var.set(
                len(self._ordered_checked()) == len(FOLDERS) and len(FOLDERS) > 0
            )
            return
        if self.select_all_var.get():
            self._checked = set(self._known_keys)
        else:
            self._checked.clear()
        self._render_marks()
        self._update_summary()

    def _on_create_user_dir(self) -> None:
        self.config.create_user_dir = self._create_user_dir_enabled()
        # 目标根目录选项固定不变，只需刷新各行的「转移目标」
        self._on_destination_changed()

    def _on_destination_changed(self) -> None:
        self._post(self._refresh_rows)

    # ---------------------------------------------------------- 键盘通道
    def _on_ctrl_enter(self, _event=None) -> str:
        self._on_start()
        return "break"

    # ==================================================================
    # 执行
    # ==================================================================
    def _set_controls_enabled(self, enabled: bool) -> None:
        """忙碌期禁用「输入类」控件；主按钮由 _update_start_state 单独管理
        （它在执行中要变成可点的「停止」，不能一并禁用）。

        P1-1：目标根目录是**只读**下拉，启用时必须恢复 ``readonly``。原先统一
        写成 ``normal``，导致首次执行任意任务后它可以被自由输入，破坏
        「固定选项不可编辑」的契约。
        """
        state = "normal" if enabled else "disabled"
        for widget in (
            self.btn_restore, self.chk_select_all, self.chk_create_user_dir,
        ):
            try:
                widget.configure(state=state)
            except tk.TclError:
                pass
        try:
            self.dest_combo.configure(state="readonly" if enabled else "disabled")
        except tk.TclError:
            pass
        self._update_start_state()

    def _on_stop(self) -> None:
        """停止当前转移：引擎按 is_cancelled 逐项收尾，已完成的项保留。"""
        if not self._busy or not self._cancelable:
            return
        self._cancel.set()
        self.status_var.set(
            "正在停止：已完成的项目会保留；未完成项目的源文件不会被删除，注册表也不会写入。"
        )
        self._update_start_state()
        self.log.info("用户请求停止当前任务")

    def _validate(self) -> Optional[str]:
        dest = self._current_destination()
        sources = [
            self._plans[k].current
            for k in self._ordered_checked()
            if k in self._plans
        ]
        if not sources:
            return "请先勾选至少一个项目。"

        ok, message = fsops.validate_destination(dest, sources)
        if not ok:
            return message
        return None

    def _on_start(self) -> None:
        if self._busy:
            return
        error = self._validate()
        if error:
            dialogs.show_warning(self.root, "无法开始", error)
            return

        keys = self._ordered_checked()
        movable = [k for k in keys if self._plans[k].needs_move]
        if not movable:
            dialogs.show_info(
                self.root,
                "无需转移",
                "所选项目当前位置与目标位置相同，无需转移。\n\n"
                "如需调整，请修改「目标根目录」。",
            )
            return

        # 执行前确认：内部固定开启（原「基本参数」页签的勾选项已内置为默认行为）。
        # 明细不再整段铺进弹窗（7 项时弹窗极长且与列表重复），改为
        # 「项数 + 合计规模 + 目标」摘要，并在列表中把本次将转移的行标出来。
        summary = f"即将转移 {len(movable)} 项"
        scanned = [self._plans[k] for k in movable if self._plans[k].scanned]
        if scanned and len(scanned) == len(movable):
            total = fsops.TreeStat(
                files=sum(i.stat.files for i in scanned),
                dirs=sum(i.stat.dirs for i in scanned),
                bytes=sum(i.stat.bytes for i in scanned),
            )
            summary += f"，合计 {total.describe()}"
        else:
            summary += "（规模在执行时统计）"

        self._pending = set(movable)
        self._render_marks()
        try:
            confirmed = dialogs.ask_yes_no(
                self.root,
                "确认开始转移",
                f"{summary}\n目标根目录：{self._current_destination()}\n\n"
                "本次将转移的项目已在列表中用浅蓝底色标出，"
                "可逐行核对「当前位置 → 转移目标」。\n\n"
                "先复制并校验，通过后才删除源目录；任一步失败都会自动回滚。",
                kind="warning",
            )
        finally:
            self._pending.clear()
            self._render_marks()
        if not confirmed:
            return

        self._cancel.clear()
        # 规模预统计已移除：引擎执行时自行权威扫描（含空间闸门与读不到即阻断）。
        self._start_work(
            self._work_transfer,
            keys,
            self._current_destination(),
            self._create_user_dir_enabled(),
            cancelable=True,
        )

    def _on_restore_defaults(self) -> None:
        if self._busy:
            return
        keys = self._ordered_checked()
        if not keys:
            dialogs.show_info(self.root, "未选择项目", "请先勾选要恢复的项目。")
            return

        names = "、".join(FOLDERS_BY_KEY[k].display for k in keys)
        confirmed = dialogs.ask_yes_no(
            self.root,
            "恢复系统默认值",
            f"以下项目将恢复为系统默认：\n\n{names}\n\n"
            "内容搬回系统默认目录（如 C:\\Users\\<当前用户>\\Documents），"
            "注册表改回指向该目录。先复制校验，全部通过后才写注册表、删原目录；"
            "任一步失败都不删源目录、不改注册表。\n\n"
            "空间不足的项目单独跳过并列出缺口，其余照常执行。",
            kind="warning",
        )
        if not confirmed:
            return

        self._cancel.clear()
        self._start_work(
            self._work_restore, keys, cancelable=True,
            busy_label="恢复中…", status="正在恢复系统默认位置…",
        )

    def _start_work(
        self,
        worker: Callable,
        *args,
        cancelable: bool = True,
        busy_label: str = "处理中…",
        status: str = "正在统计待转移内容，请稍候…",
    ) -> None:
        """执行任务。

        ``cancelable=False`` 时主按钮显示 ``busy_label`` 且不可点
        （重启资源管理器这类不可中断的短任务没有中断回调，不给假的停止入口）。
        """
        self._busy = True
        self._cancelable = cancelable
        self._busy_label = busy_label
        self._set_controls_enabled(False)
        self.progress.configure(value=0, maximum=100)
        self.status_var.set(status)
        # 参数在主线程取好后传入：工作线程**不读 Tk 变量**（tkinter 线程契约）
        try:
            threading.Thread(target=worker, args=args, daemon=True).start()
        except BaseException as exc:   # noqa: BLE001 - 见下方注释（审计 C3）
            # 线程根本起不来时，不会有任何回调来复位 _busy —— 主按钮会永久停在
            # "处理中…"并保持不可点（只剩关窗一条路）。必须就地收尾并说明原因。
            self.log.exception("后台线程启动失败")
            self._finish_work()
            dialogs.show_error(
                self.root, "无法启动任务", f"后台线程启动失败：{exc}"
            )

    def _finish_work(self) -> None:
        self._busy = False
        self._cancelable = False
        # 进度条归零（审计 C8）：不复位的话，一次成功执行后它会永久停在 100%，
        # 看起来像"还有任务在跑"，直到下次 _start_work 才归零。
        self.progress.configure(value=0)
        self._set_controls_enabled(True)
        self._post(self._refresh_rows)

    def _work_transfer(
        self,
        keys: Sequence[str],
        dest: str,
        create_user_dir: bool,
    ) -> None:
        try:
            self._post(self.status_var.set, "正在统计待转移内容，请稍候…")
            plan = self.engine.build_plan(
                keys, dest, create_user_dir, scan=True
            )
            report = self.engine.execute(
                plan,
                on_item_start=lambda item: self._post(self._on_item_start, item),
                on_item_done=lambda item: self._post(self._on_item_done, item),
                on_progress=self._on_progress,
                is_cancelled=self._cancel.is_set,
            )
            self._post(self._after_transfer, report)
        except BaseException as exc:  # noqa: BLE001 - 兜底：任何异常都不能让界面卡死
            # 这里刻意接 ``BaseException`` 而不是 ``Exception``（审计 C3）：
            # ``KeyboardInterrupt`` / ``SystemExit`` 在 ``--windowed`` exe 里没有
            # 实际语义，但漏掉它们会让线程**静默死亡**——没有回调复位 _busy，
            # 主按钮永久卡在"停止 / 处理中…"，比报个错危险得多。
            self.log.exception("转移过程发生未预期错误")
            self._post(self._on_worker_error, exc)

    def _work_restore(
        self,
        keys: Sequence[str],
    ) -> None:
        """完整恢复（注册表 + 文件搬回）。"""
        try:
            self._post(self.status_var.set, "正在恢复系统默认位置…")
            report = self.engine.restore_defaults(
                keys,
                on_item_start=lambda item: self._post(self._on_item_start, item),
                on_item_done=lambda item: self._post(self._on_item_done, item),
                on_progress=self._on_progress,
                is_cancelled=self._cancel.is_set,
            )
            self._post(self._after_transfer, report)
        except BaseException as exc:  # noqa: BLE001 - 同 _work_transfer（审计 C3）
            self.log.exception("恢复默认值过程发生未预期错误")
            self._post(self._on_worker_error, exc)

    def _on_progress(self, done: int, total: int, name: str) -> None:
        self._post(self._set_progress, done, total, name)

    def _set_progress(self, done: int, total: int, name: str) -> None:
        self.progress.configure(maximum=max(total, 1), value=done)
        self.status_var.set(f"正在处理 {name}（{done}/{total}）")

    def _on_item_start(self, item: PlanItem) -> None:
        self.status_var.set(f"正在转移【{item.display}】…")
        if self.tree.exists(item.key):
            self.tree.set(item.key, "status", "处理中…")
            # 处理中的行用强调色标出，一眼可辨当前进度落在哪一项
            self.tree.item(item.key, tags=("even", "active"))

    def _on_item_done(self, item: PlanItem) -> None:
        if self.tree.exists(item.key):
            self.tree.set(item.key, "status", item.describe())
            index = self.tree.index(item.key)
            self.tree.item(item.key, tags=self._row_tags(item, index))

    def _after_transfer(self, report: TransferReport) -> None:
        # 审计 R1：整段收尾 UI（弹结果窗 → 写状态栏 → 可能再问是否重启资源
        # 管理器）期间必须挡住 ``_poll_exit`` 的销毁。``_busy`` 在 ``_finish_work``
        # 第一行就复位了，靠它守不住这段。
        self._finishing_ui = True
        try:
            # 先记录结果再刷新：重建行时按结果着色（完成=绿 / 失败=红）。
            self._last_outcomes = {o.key: o.status for o in report.outcomes}
            self._finish_work()

            if self._exit_requested:
                # 用户已经点过退出：总结弹窗无人看，而弹窗期间跑的嵌套事件循环
                # 正是销毁窗口的入口。只落日志，绝不弹窗、绝不进入重启确认。
                self.log.info("已请求退出：跳过结果弹窗（%s）", report.summary())
                return

            lines = [
                f"{o.label}｜{o.display}：{o.message or '—'}" for o in report.outcomes
            ]
            body = "\n".join(lines) if lines else "没有执行任何操作。"
            headline = report.summary()

            if report.cancelled:
                dialogs.show_info(
                    self.root,
                    "已停止",
                    f"{headline}\n\n{body}\n\n"
                    "已完成的项目保持有效，未完成项目的源文件未被删除。如需继续，请再次点击「开始转移」。",
                )
            elif report.failed:
                dialogs.show_warning(
                    self.root,
                    "部分项目未完成",
                    f"{headline}\n\n{body}\n\n"
                    "失败项目的源文件未被删除，可修正后重试；完整日志见「打开日志目录」。",
                )
                # 失败/异常后的可操作出口：状态栏出现「打开日志目录」
                self._show_logs_link()
            else:
                dialogs.show_success(self.root, "操作完成", f"{headline}\n\n{body}")

            self.status_var.set(headline)

            if report.snapshot_path:
                self.log.info("注册表快照：%s", report.snapshot_path)

            # 重启资源管理器：内部固定开启
            if not self.dry_run and report.succeeded:
                self._confirm_restart_explorer()
        finally:
            self._finishing_ui = False

    def _confirm_restart_explorer(self) -> None:
        # 审计 R1（铁律）：用户已请求退出后**绝不能**再走 ``taskkill explorer``。
        # 这是最后一道闸门——``_after_transfer`` 已经早退，但本方法也可能被别的
        # 路径直接调用，而它会在用户关窗之后去强杀系统 shell。
        if self._exit_requested:
            self.log.info("已请求退出：跳过重启资源管理器（不执行 taskkill）")
            return
        if not dialogs.ask_yes_no(
            self.root,
            "重启资源管理器",
            "注册表已更新。是否立即重启资源管理器以使其生效？\n\n"
            "重启期间桌面与任务栏会短暂消失，随后自动恢复；"
            "系统未启用 shell 自动重启（AutoRestartShell=0）时会自动跳过。",
        ):
            self.status_var.set("已跳过重启资源管理器，注销后重新登录同样可以生效。")
            return

        # 审计 P1-6：taskkill（超时 RESTART_EXPLORER_TIMEOUT 秒）+ 两段等待
        # （RESTART_TERM_WAIT + RESTART_RELAUNCH_WAIT）合计可阻塞约
        # int(_RESTART_TOTAL_WAIT) 秒。它原先跑在主线程 → 界面完全无响应；
        # 改到工作线程，结果再回主线程显示。（数字取自 constants，与退出提示
        # 文案同源，改一处两处同时生效。）
        self._start_work(
            self._work_restart_explorer,
            cancelable=False,
            busy_label="重启中…",
            status="正在重启资源管理器，请稍候…",
        )

    def _work_restart_explorer(self) -> None:
        try:
            _ok, message = shell.restart_explorer(self.store, dry_run=self.dry_run)
            self.log.info("重启资源管理器：%s", message)
            self._post(self._after_restart_explorer, message)
        except BaseException as exc:  # noqa: BLE001 - 同 _work_transfer（审计 C3）
            self.log.exception("重启资源管理器发生未预期错误")
            self._post(self._on_worker_error, exc)

    def _after_restart_explorer(self, message: str) -> None:
        self._finish_work()
        self.status_var.set(message)

    def _show_logs_link(self) -> None:
        """失败后在状态栏露出「打开日志目录」出口（恢复成功不显示）。"""
        try:
            self.btn_open_logs.grid()
        except tk.TclError:
            pass

    def _open_logs_dir(self) -> None:
        # C4：日志目录建不出来时（exe 放在 Program Files 等无写权限位置）不能报
        # "已打开日志目录"——shell.open_in_explorer 对不存在的路径直接返回，
        # 资源管理器什么都不会弹，用户会以为程序坏了。
        path = self.log_dir
        if not path.is_dir():
            self.status_var.set(f"日志目录不可用（可能没有写权限）：{path}")
            return
        shell.open_in_explorer(str(path))
        self.status_var.set(f"已打开日志目录：{path}")

    # ==================================================================
    # Tk 回调异常兜底（审计 R3）
    # ==================================================================
    def _on_tk_exception(self, exc, val, tb) -> None:
        """``report_callback_exception`` 钩子：Tk 回调异常的唯一出口。

        ``--windowed`` 打包后 ``sys.stderr`` 是 ``None``，而 Python 3.14 的
        ``tkinter`` 默认实现就是 ``print(..., file=sys.stderr)`` —— 异常被
        **静默丢弃**，界面只表现成"点了没反应"，用户与日志都无从得知。

        ``_drain_queue`` 只兜住了**队列回调**（历史上"后台线程导入失败 → 按钮
        永远禁用"的那次修复），其余 Tk 回调——``_check_pending_intent``、
        ``_poll_exit`` 自身的 ``destroy()``、``_open_logs_dir``、Treeview /
        Combobox 的三个绑定、``ToolTip._show``——全都没有兜底。这里统一接管：

        * **不弹窗**：一次回调异常不该打断用户的操作流；
        * **不吞异常**：完整 traceback 必须落到日志，否则无从排障。
        """
        detail = "".join(traceback.format_exception(exc, val, tb)).strip()
        try:
            self.log.error("Tk 回调异常：\n%s", detail)
        except Exception:   # noqa: BLE001 - 日志本身坏了也不能再抛给 Tk
            pass
        if not logging_setup.file_logging_available():
            # 日志目录建不出来（exe 在无写权限位置）→ 仿 run.py:463-468 落到
            # startup_error.txt，保证交付产物里仍有一处可看的痕迹。
            self._write_crash_fallback(detail)

    def _write_crash_fallback(self, detail: str) -> None:
        """日志不可用时的兜底落点（与 ``run.py`` 的 startup_error.txt 同源）。"""
        try:
            path = self.log_dir / "startup_error.txt"
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("a", encoding="utf-8", errors="replace") as handle:
                handle.write(detail + "\n")
        except OSError:
            pass

    def _on_worker_error(self, exc: BaseException) -> None:
        self._finishing_ui = True
        try:
            self._finish_work()
            if self._exit_requested:
                # 审计 R1：同 _after_transfer —— 用户已请求退出，不弹错误窗
                self.log.error(
                    "已请求退出：跳过错误弹窗（%s: %s）", type(exc).__name__, exc
                )
                return
            self._show_logs_link()
            dialogs.show_error(
                self.root,
                "执行出错",
                f"发生未预期错误：\n\n{type(exc).__name__}: {exc}\n\n"
                f"详细过程见日志目录：\n{self.log_dir}",
            )
        finally:
            self._finishing_ui = False

    # ==================================================================
    # 线程 → 界面 通信
    # ==================================================================
    def _post(self, fn: Callable, *args) -> None:
        self._queue.put((fn, args))

    def _drain_queue(self) -> None:
        try:
            while True:
                fn, args = self._queue.get_nowait()
                try:
                    fn(*args)
                except Exception:
                    # C1：回调异常不能只进日志文件——界面会停在"半刷新"状态
                    # （摘要说"已选择 7/7 项"、列表却可能是空的、主按钮还能点），
                    # 用户完全看不出出过错。这里必须落到用户可见的地方。
                    self.log.exception("界面回调执行失败")
                    try:
                        self.status_var.set("界面刷新失败，详情见日志文件")
                        self._show_logs_link()
                    except tk.TclError:
                        pass   # 窗口已销毁，无处可显示
        except queue.Empty:
            pass
        try:
            self.root.after(80, self._drain_queue)
        except tk.TclError:
            pass  # 窗口已销毁

    def _show_admin_banner(self) -> None:
        px = self.theme.px
        banner = tk.Frame(self.root, background=COLORS["warning_bg"],
                          highlightthickness=px(1),
                          highlightbackground=COLORS["warning_border"])
        banner.grid(row=_ROW_BANNER, column=0, sticky="ew",
                    padx=px(SP_LG), pady=(px(SP_MD), 0))
        banner.columnconfigure(1, weight=1)

        # 警示小图标：与表格勾选框同一套运行时渲染管线（icons）
        banner_icon = self.theme.icon("warning", units=16)
        self._keep_banner_icon = banner_icon   # 防 PhotoImage 被垃圾回收
        tk.Label(
            banner, image=banner_icon, background=COLORS["warning_bg"],
        ).grid(row=0, column=0, sticky="w", padx=(px(SP_SM), 0))

        tk.Label(
            banner,
            text="当前未以管理员身份运行：部分被系统占用的位置可能转移失败。",
            background=COLORS["warning_bg"], foreground=COLORS["warning"],
            font=self.theme.fonts["small"], anchor="w",
            padx=px(SP_MD), pady=px(SP_SM),
        ).grid(row=0, column=1, sticky="ew")

        # 提权入口统一为链接风格（Link.TButton 语义，警示底上不出现第二种按钮实现）
        #
        # 试运行宿主（dry_run=True）不提供该入口（审计 C5）：`relaunch_as_admin`
        # 只能拉起一个**不带 --dry-run** 的普通实例，等于把试运行悄悄变成真实执行；
        # 而传 `--dry-run` 也不对——那在 CLI 上被 `main` 路由到控制台版
        # `_cmd_dry_run`，在 --windowed 交付物里没有控制台，用户只会看到
        # "命令行输出无法直接显示"，什么也得不到。宁可不开这个口子。
        if not self.dry_run:
            ttk.Button(
                banner, text="以管理员身份重启", style="WarningLink.TButton",
                command=self._relaunch_admin,
            ).grid(row=0, column=2, padx=px(SP_MD))
        else:
            self.log.info("试运行模式：不提供提权重启入口（避免绕过试运行保证）")

        self.log.warning("当前未以管理员身份运行")

    def _relaunch_admin(self) -> None:
        # 先释放单实例互斥体（审计 C5）：否则提权后的子进程可能先跑到
        # _acquire_single_instance()，看到一个"已有实例"的互斥体 → 弹
        # "程序已在运行"后退出，而父进程随后也收工，用户看到主程序整块消失。
        # 释放动作由 run.py 注入（句柄在那边），见 __init__ 的说明。
        if self._on_before_relaunch is not None:
            try:
                self._on_before_relaunch()
            except Exception:
                self.log.exception("释放单实例互斥体失败")
        ok, message = shell.relaunch_as_admin()
        self.log.info("提权重启：%s", message)
        if ok:
            self.root.destroy()
        else:
            dialogs.show_info(self.root, "无法提权", message)

    # ==================================================================
    # 上次未完成的事务（审计 P0-4）
    # ==================================================================
    def _check_pending_intent(self) -> None:
        """启动时检查意图日志：上次被中断则明确告知，并允许一键回滚注册表。"""
        if self.dry_run:
            return
        pending = IntentJournal.pending(self.backup_dir)
        # 审计 B1：``IntentJournal.pending()`` 把「日志损坏/不可读」写进
        # ``_JOURNAL_READ_ERROR``，但该信号此前**生产零调用**消费者——日志只写了
        # 一半（进程被硬杀）时 ``pending()`` 返回 ``[]``，界面与日志都收不到「上次
        # 可能有半完成项」。这里显式取走：非空即记日志并弹**警示**框（属"需注意/
        # 操作受阻"类，故用 show_warning 而非 show_info）。
        journal_error = IntentJournal.consume_read_error()
        if journal_error:
            self.log.warning("意图日志读取失败：%s", journal_error)
            dialogs.show_warning(
                self.root,
                "上次事务记录不可用",
                "无法读取上次转移留下的意图日志——可能上次运行被强制中断，"
                "日志只写了一半。\n\n"
                f"详情：{journal_error}\n\n"
                "本程序因此无法判断是否有未完成项，也无法一键回滚；"
                "请核对各文件夹「当前位置」是否符合预期，必要时人工修正。",
            )
            return
        if not pending:
            return

        lines = []
        for entry in pending:
            stage = STAGE_LABELS.get(entry["stage"], entry["stage"])
            lines.append(
                f"· {entry['display']}：{stage}\n"
                f"    源：{entry['source']}\n    目标：{entry['target']}"
            )
        snapshot = next((e["snapshot"] for e in pending if e["snapshot"]), "")
        tail = (
            "是否用当时的注册表快照回滚到操作前状态？\n"
            if snapshot
            else "（未找到可用的注册表快照，只能人工核对）\n"
        )
        answer = dialogs.ask_yes_no_cancel(
            self.root,
            "上次转移未正常收尾",
            f"上次有 {len(pending)} 项转移未完成：\n\n"
            + "\n".join(lines)
            + "\n\n数据不会丢失（未通过校验的源目录不会被删除），"
            "但注册表可能停在中途状态。\n\n"
            + tail
            + "选「否」＝忽略并清除记录；选「取消」＝保留记录，下次启动再提示。",
        )
        if answer is None:
            return
        if answer and snapshot:
            # 审计 C15-fix（R9-1/R9-2）：整份快照回滚会把**本轮已成功**（源目录已
            # 删除）的项也滚回旧路径，让注册表指向一个不存在的目录。这里把「未完成
            # 项涉及的 (子键, 值名)」与「日志是否带 registry 字段」一并交给回滚，
            # 由它决定裁剪还是整份回滚。
            known = all(bool(e.get("registry_known")) for e in pending)
            targets = IntentJournal.unfinished_registry_targets(self.backup_dir)
            self._rollback_from_snapshot(snapshot, targets, known)

        path = IntentJournal.journal_path(self.backup_dir)
        if path is not None:
            try:
                path.unlink(missing_ok=True)
            except OSError as exc:
                self.log.warning("清除意图日志失败：%s", exc)

    def _rollback_from_snapshot(self, path: str, targets=None, known: bool = True) -> None:
        """按快照回滚注册表。

        ``known=True``（日志带 ``registry`` 字段）时只回滚 ``targets`` 里「未完成项」
        涉及的条目（审计 C15-fix / R9-2），避免把本轮已成功、源目录已删除的项也滚回
        旧路径。``known=False``（旧版本日志，无法区分已完成项）时保持整份回滚——绝不
        凭残缺信息收窄回滚范围。

        **新增调用点必须显式传 ``known``**：默认 ``True`` 会让「缺 ``registry`` 字段的
        老日志」也走裁剪分支，而那时 ``targets`` 为空 → 快照被裁空 → 误判「无需回滚」
        （明明该整份回滚却什么都不做）。这是静默的行为错误，当前唯一调用点已显式传参。
        """
        if self._exit_requested:
            # 审计 R1：用户已请求退出 → 不弹回滚结果窗（同 _after_transfer）
            self.log.info("已请求退出：跳过回滚结果弹窗")
            return
        try:
            snapshot = load_snapshot(path)
            excluded = 0
            if known:
                snapshot, excluded = restrict_registry_snapshot(snapshot, targets or ())
                if not snapshot.entries:
                    # 残留项都已成功收尾：没有任何需要回滚的注册表值
                    dialogs.show_info(
                        self.root,
                        "无需回滚",
                        "上次未完成的项目都已成功收尾，"
                        "没有需要回滚的注册表值。",
                    )
                    return
            restored, removed = self.store.rollback(snapshot)
            # 审计 C17：registry.rollback() 会把「快照期读取失败、原值未知因而被跳过、
            # 未做任何改动」的条数记在这里，但生产代码此前从未读它——用户只看到
            # 「恢复 N 处、删除 M 处」，看不到有若干处被静默跳过（与 C1/C4「不让界面
            # 说谎」同类）。
            skipped = self.store.last_rollback_skipped
        except (OSError, ValueError, KeyError, AttributeError) as exc:
            dialogs.show_error(
                self.root,
                "回滚失败", f"无法按快照 {path} 回滚：{exc}")
            return
        self.log.info("按快照回滚注册表：恢复 %s 处，删除 %s 处", restored, removed)
        message = f"已按快照恢复 {restored} 处、删除 {removed} 处注册表值。"
        if not known:
            message += "\n\n该记录由旧版本生成，无法区分已完成项，已整份回滚。"
        elif excluded:
            message += f"\n\n已跳过 {excluded} 处属于已完成项目、不应回滚的值。"
        # 独立分支（不是 elif）：与上面两句是两件不同的事，可能同时出现
        if skipped:
            message += (
                f"\n\n另有 {skipped} 处注册表值因快照读取失败、原值未知而被跳过"
                "（未做任何改动），需人工核对。"
            )
        dialogs.show_success(self.root, "已回滚", message)
        self._post(self._refresh_rows)

    # ==================================================================
    def _on_exit(self) -> None:
        """退出程序（按钮与窗口 X 都走这里，审计 P0-3）。

        执行中退出**不立刻销毁窗口**：先置取消位，再轮询等工作线程把当前项
        收尾。否则 daemon 线程可能在 ``rmtree`` 中途被强杀，留下删了一半的
        源目录 —— 与「未完成项目的源文件不会被删除」的承诺直接矛盾。
        """
        if self._exit_requested:
            return
        if self._busy:
            if self._cancelable:
                prompt = (
                    "当前有任务正在执行。\n\n"
                    "建议先点主按钮上的「停止」："
                    "程序会逐项收尾，已完成的项目保留，未完成的源文件不会被删除。\n\n"
                    "仍然退出程序吗？（会先安全收尾，可能需要一点时间）"
                )
            else:
                # 审计 C14：不可中断的任务（重启资源管理器）此刻主按钮显示
                # "重启中…"且已禁用——界面上根本没有"停止"入口，不能再说
                # "建议先点停止"。
                # A-7/B-6：时长由 constants 的同批常量算出（与 shell.py 的实际
                # 等待一致），不再写死——改了等待时长，这里跟着变。
                prompt = (
                    "当前正在执行一个不可中断的步骤（重启资源管理器）。\n\n"
                    f"该步骤没有停止入口，退出会等它完成（最长约 "
                    f"{int(RESTART_EXPLORER_TIMEOUT)} 秒，含两段等待合计约 "
                    f"{int(_RESTART_TOTAL_WAIT)} 秒）后自动收尾，"
                    "不会中断在途的系统操作。\n\n"
                    "仍然退出程序吗？"
                )
            if not dialogs.ask_yes_no(self.root, "正在执行", prompt):
                return
            self._cancel.set()
            self._exit_requested = True
            self._exit_waited = 0
            self.status_var.set("正在安全收尾：等当前项写完并校验后自动退出…")
            self._update_start_state()
            try:
                self.root.after(_EXIT_POLL_MS, self._poll_exit)
            except tk.TclError:
                pass
            return
        self.root.destroy()

    def _poll_exit(self) -> None:
        """等待工作线程收尾后再销毁窗口；超时则记日志并强制退出。"""
        if not self._exit_requested:
            return
        # 审计 R1：``_busy`` 之外还要看 ``_finishing_ui``。收尾 UI（结果弹窗、
        # 状态栏、重启确认）跑在**嵌套事件循环**里，200ms 轮询照常触发；只看
        # ``_busy`` 会在弹窗显示期间把窗口销毁（弹窗按钮点下去时窗口已不存在，
        # 回调抛 TclError，用户一次也看不到总结）。
        if self._busy or self._finishing_ui:
            self._exit_waited += _EXIT_POLL_MS
            if self._exit_waited < _EXIT_TIMEOUT_MS:
                try:
                    self.root.after(_EXIT_POLL_MS, self._poll_exit)
                except tk.TclError:
                    pass
                return
            self.log.warning("等待收尾超时（%s ms），强制退出", _EXIT_TIMEOUT_MS)
        try:
            self.root.destroy()
        except tk.TclError:
            # 审计 R4：窗口可能已被别的路径销毁（例如 ``_relaunch_admin`` 的
            # ``root.destroy()``），此时就地抛 TclError 会污染退出流程。
            pass
