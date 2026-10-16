"""界面冒烟测试：真实创建窗口并检查关键控件，不执行任何转移操作。

在无图形会话的环境下会自动跳过，不会导致测试失败。
"""

from __future__ import annotations

import contextlib
import os
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import tkinter as tk  # noqa: E402
from tkinter import ttk  # noqa: E402

from docmover.config import AppConfig  # noqa: E402
from docmover.constants import APP_TITLE, FOLDER_KEYS, FOLDERS  # noqa: E402
from docmover.ui import columns as column_rules  # noqa: E402


class TestMainWindowSmoke(unittest.TestCase):

    def setUp(self):
        try:
            self.root = tk.Tk()
        except tk.TclError as exc:
            self.skipTest(f"无可用图形环境：{exc}")
        self.root.withdraw()  # 构建控件但不真正弹出窗口
        self.window = None

        # 审计 P2-22：把真机状态隔离掉——
        #   ① 日志写到临时目录（不再污染仓库 pydm/logs/）；
        #   ② 注册表用内存后端替换真实 HKCU；
        #   ③ 固定盘符探测固定为已知假盘，断言与真实机器无关。
        from docmover import fsops as _fsops
        from docmover import registry as _registry
        from docmover.ui import main_window as _mw

        self._log_tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._log_tmp.cleanup)

        store_patcher = mock.patch.object(
            _mw,
            "RegistryStore",
            side_effect=lambda *a, **k: _registry.RegistryStore(_registry.MemoryBackend()),
        )
        store_patcher.start()
        self.addCleanup(store_patcher.stop)

        fake_drive = _fsops.DriveInfo(
            letter="D:", total_bytes=1 << 40, free_bytes=1 << 39
        )
        drive_patcher = mock.patch.object(
            _fsops, "list_fixed_drives", return_value=[fake_drive]
        )
        drive_patcher.start()
        self.addCleanup(drive_patcher.stop)

    def tearDown(self):
        if self.window is not None:
            try:
                self.window._cancel.set()
            except Exception:
                pass
        # 关闭注入的临时日志文件句柄，否则 Windows 下临时目录无法删除
        import logging as _logging

        logger = _logging.getLogger("DirMover")
        for handler in list(logger.handlers):
            if isinstance(handler, _logging.FileHandler):
                logger.removeHandler(handler)
                try:
                    handler.close()
                except Exception:
                    pass
        # 取消所有待触发的 after 回调，否则销毁窗口后 Tcl 会打印
        # "invalid command name ..." 噪声，可能掩盖真实错误。
        try:
            for after_id in self.root.tk.eval("after info").split():
                try:
                    self.root.after_cancel(after_id)
                except tk.TclError:
                    pass
        except tk.TclError:
            pass
        try:
            self.root.update_idletasks()
            self.root.destroy()
        except tk.TclError:
            pass

    def _make(self, **overrides) -> object:
        from docmover.ui.main_window import MainWindow

        config = AppConfig(**overrides)
        config.normalize()
        self.window = MainWindow(
            self.root, config, dry_run=True, log_dir=Path(self._log_tmp.name)
        )
        self.root.update()          # 触发排队的刷新回调
        self.root.update_idletasks()
        return self.window

    # ------------------------------------------------------------------
    def test_window_builds_with_all_rows(self):
        window = self._make()
        rows = window.tree.get_children()
        self.assertEqual(len(rows), len(FOLDERS))
        self.assertEqual(
            [window.tree.set(row, "name").strip() for row in rows],
            [spec.display for spec in FOLDERS],
        )

    def test_default_state_checks_all(self):
        """无持久化：每次启动默认全选所有项目。"""
        window = self._make()
        self.assertEqual(window._checked, set(FOLDER_KEYS))
        self.assertIn(f"已选择 {len(FOLDERS)}", window.lbl_summary.cget("text"))

    def test_toggle_check(self):
        window = self._make()

        def row_image(key):
            return str(window.tree.item(key, "image")[0])

        # 默认全选：第一次点击取消勾选，再点一次重新勾选
        window._toggle("documents")
        self.assertNotIn("documents", window._checked)
        self.assertEqual(row_image("documents"), str(window.theme.checkbox(False)))

        window._toggle("documents")
        self.assertIn("documents", window._checked)
        self.assertEqual(row_image("documents"), str(window.theme.checkbox(True)))

    def test_select_all_and_none(self):
        window = self._make()
        window.select_all_var.set(True)
        window._on_select_all()
        self.assertEqual(len(window._checked), len(FOLDERS))

        window.select_all_var.set(False)
        window._on_select_all()
        self.assertEqual(window._checked, set())

    def test_no_recommend_button_and_no_body_title(self):
        """「推荐选择」按钮与正文大标题均已移除（标题只在窗口标题栏）。"""
        window = self._make()
        self.assertFalse(hasattr(window, "btn_recommend"))
        self.assertFalse(hasattr(window, "_on_recommend"))
        labels = self._all_labels(window.root)
        self.assertNotIn(APP_TITLE, labels)

    def _all_labels(self, widget) -> list:
        texts = []
        for child in widget.winfo_children():
            if isinstance(child, (tk.Label, ttk.Label)):
                texts.append(str(child.cget("text")))
            texts.extend(self._all_labels(child))
        return texts

    def _all_texts(self, widget) -> list:
        """收集所有带 text 的控件的文案（含按钮 / 勾选框），用于「不得出现」断言。"""
        texts = []
        for child in widget.winfo_children():
            try:
                texts.append(str(child.cget("text")))
            except tk.TclError:
                pass
            texts.extend(self._all_texts(child))
        return texts

    def test_notebook_tabs(self):
        """单屏重构：Notebook 与两个分页已移除；日志抽屉亦已按需求整体移除。"""
        window = self._make()
        self.assertFalse(hasattr(window, "notebook"))
        self.assertFalse(hasattr(window, "page_transfer"))
        self.assertFalse(hasattr(window, "page_log"))
        # 日志抽屉（2026-10 需求：一次性操作不需要日志界面）不得存在
        self.assertFalse(hasattr(window, "log_drawer"))
        self.assertFalse(hasattr(window, "log_toggle"))
        # 界面上不允许出现「运行日志」字样（按钮 / 标签 / 抓手都算）
        for text in self._all_texts(window.root):
            self.assertNotIn("运行日志", text, msg=text)
            self.assertNotIn("Ctrl+L", text, msg=text)

    def test_status_column_fits_labels(self):
        """状态列必须留得下状态文案（历史缺陷：150px 列宽 + 过长文案被硬裁）。

        自适应改造后这条契约不变，而且更强：状态列下限 = 内容需要宽度（且不低于
        文本列硬下限），窗口再小也不裁字（见 test_column_contract_* 系列）。

        注意两点（2026-10-16 CI 恒红教训，见 ``docs/审计报告.md`` §8.26）：

        1. 内容需要宽度取自**当前表格里最长的一条状态文案**，是随数据漂移的：
           本机目标目录已存在 → 首行「将合并（0 个文件 / 0 B）」170px → 状态列
           182px；换台机器该目录不存在 → 全是「待转移」→ 需要宽度只有 57px。
        2. 因此下限必须用 ``column_rules.MIN_COL`` 而不是字面量 90——90 是在开发机
           数据下「标定」出来的，换个数据集就不成立。只有「内容需要宽度」与
           「逻辑像素硬下限」才是与数据、与字体都无关的契约。
        """
        window = self._make()
        width = int(window.tree.column("status", "width"))
        need = self._column_need("status")
        # 失败时把环境一并打印出来：字体 / 缩放 / 视口 / 各列宽，便于定位是
        # 「布局算错」还是「这台机器的字体度量不一样」。
        detail = (
            f"status={width} need={need} "
            f"MIN_COL={window.theme.px(column_rules.MIN_COL)} "
            f"font={window.theme.ui_font} scale={window.theme.scale} "
            f"viewport={window._table_viewport()} widths={self._widths(window)}"
        )
        self.assertGreaterEqual(width, need, msg=detail)
        self.assertGreaterEqual(width, window.theme.px(column_rules.MIN_COL),
                                msg=detail)

    # ------------------------------------------------- 列宽自适应（列宽契约）
    def _column_need(self, key: str) -> int:
        """独立量出某列的内容需要宽度（不复用被测实现，避免自证）。"""
        from tkinter import font as tkfont

        from docmover.ui import columns as rules

        px = self.window.theme.px
        need = (tkfont.Font(root=self.root, font=self.window.theme.fonts["h3"])
                .measure(rules.HEADERS[key]) + px(rules.HEAD_PAD))
        index = rules.COLUMN_KEYS.index(key)
        for iid in self.window.tree.get_children():
            values = self.window.tree.item(iid, "values")
            if index < len(values):
                text_w = tkfont.Font(root=self.root,
                                     font=self.window.theme.fonts["base"]).measure(
                                         str(values[index]))
                need = max(need, text_w + px(rules.CELL_PAD))
        return need

    def _widths(self, window) -> dict:
        return {col: int(window.tree.column(col, "width"))
                for col in ("#0",) + column_rules.COLUMN_KEYS}

    def test_column_contract_fills_viewport_and_keeps_long_text(self):
        """列宽契约：总和恰为可用宽；名称列不超配额；状态列不裁字。"""
        window = self._make()
        px = window.theme.px
        widths = self._widths(window)
        viewport = window._table_viewport()

        self.assertEqual(sum(widths[key] for key in column_rules.COLUMN_KEYS),
                         viewport, msg=str(widths))
        # 行首图标列固定（= Item padding 6 + 图标 16 + 余量，不参与自适应）
        self.assertEqual(widths["#0"], px(28))
        # 名称列：不得超出「内容需要 + 角色配额」——历史缺陷是它白占 95px 空白
        name_quota = px(column_rules.COLUMNS[0][3])
        self.assertLessEqual(widths["name"], self._column_need("name") + name_quota,
                             msg=str(widths))
        # 名称列必须放得下表头（否则表头被裁成「文件夹名…」）
        self.assertGreaterEqual(widths["name"], self._column_need("name"))
        # 状态列：内容需要宽度是下限，一个字都不裁
        self.assertGreaterEqual(widths["status"], self._column_need("status"))

    def test_column_contract_path_columns_absorb_growth(self):
        """窗口变宽：路径列吃掉余量，名称列增幅不超过自己的（很小的）配额。"""
        window = self._make()
        px = window.theme.px
        window._apply_column_fit(viewport=px(720))
        small = self._widths(window)
        window._apply_column_fit(viewport=px(1000))
        big = self._widths(window)

        name_quota = px(column_rules.COLUMNS[0][3])
        self.assertLessEqual(big["name"] - small["name"], name_quota)
        self.assertGreater(big["current"] - small["current"],
                           big["name"] - small["name"])
        self.assertGreater(big["target"] - small["target"],
                           big["name"] - small["name"])
        self.assertEqual(sum(big[key] for key in column_rules.COLUMN_KEYS), px(1000))

    def test_column_contract_shrink_never_cuts_protected_columns(self):
        """窗口变窄：只有路径列让步；名称 / 状态最多退回内容需要宽度，绝不裁字。"""
        window = self._make()
        px = window.theme.px
        window._apply_column_fit(viewport=px(1000))
        wide = self._widths(window)
        window._apply_column_fit(viewport=px(720))
        narrow = self._widths(window)

        for key in ("name", "status"):
            need = self._column_need(key)
            self.assertGreaterEqual(narrow[key], need, msg=f"{key} 被裁字了")
            self.assertLessEqual(narrow[key], wide[key], msg=f"{key} 反而变宽了")
        self.assertLess(narrow["current"], wide["current"])
        self.assertLess(narrow["target"], wide["target"])
        self.assertGreaterEqual(narrow["current"], px(column_rules.MIN_COL))
        self.assertGreaterEqual(narrow["target"], px(column_rules.MIN_COL))
        self.assertEqual(
            sum(narrow[key] for key in column_rules.COLUMN_KEYS), px(720))

    def test_column_contract_follows_longer_content(self):
        """列宽跟着内容走：某行路径变长后，该列自动变宽以容纳。"""
        window = self._make()
        px = window.theme.px
        row = window.tree.get_children()[0]
        long_path = "C:\\" + "非常长的目录名称\\" * 6 + "Documents"
        window.tree.set(row, "current", long_path)
        window._need_hwm.clear()
        window._apply_column_fit(viewport=px(1600))

        need = self._column_need("current")
        self.assertGreater(need, window.theme.px(400), "夹具本身应当是长文本")
        self.assertGreaterEqual(self._widths(window)["current"], need)

    def test_column_contract_resets_after_data_refresh(self):
        """数据刷新后高水位清零：换到更短的目标盘，列宽不再被旧长路径撑住。"""
        window = self._make()
        row = window.tree.get_children()[0]
        window.tree.set(row, "current", "C:\\" + "A" * 200)
        window._apply_column_fit(viewport=window.theme.px(1600))
        stretched = self._widths(window)["current"]

        window._refresh_rows()          # 重建行 + 清零高水位
        window._apply_column_fit(viewport=window.theme.px(1600))
        self.assertLess(self._widths(window)["current"], stretched)

    def test_cell_tooltip_reveals_truncated_path_only_when_needed(self):
        """路径被裁时悬停给出完整文本；内容放得下时不打扰。"""
        from types import SimpleNamespace

        window = self._make()
        px = window.theme.px
        row = window.tree.get_children()[0]
        full_path = window.tree.set(row, "current")

        # 归零 #0 列宽，用坐标定位第 2 个值列（#2 = 当前位置）
        window.tree.column("#0", width=0)
        window.tree.column("current", width=px(40))
        window.tree.column("name", width=px(40))
        window.tree.column("target", width=px(40))
        window.tree.update_idletasks()
        event = SimpleNamespace(x=px(40) + px(10), y=px(40))
        with mock.patch.object(window.tree, "identify_row", return_value=row):
            tip = window._cell_tip_text(event)
        self.assertIsNotNone(tip)
        self.assertIn(full_path, tip)
        self.assertTrue(tip.startswith("当前位置"))

        # 内容完整可见（列宽足够）→ 不弹提示
        window.tree.column("current", width=px(4000))
        with mock.patch.object(window.tree, "identify_column", return_value="#2"), \
                mock.patch.object(window.tree, "identify_row", return_value=row):
            self.assertIsNone(window._cell_tip_text(SimpleNamespace(x=px(10),
                                                                    y=px(40))))

    def test_heading_drag_is_blocked(self):
        """列宽由内容决定：表头分隔线不参与拖拽（否则拖完会被重排覆盖）。"""
        from types import SimpleNamespace

        window = self._make()
        event = SimpleNamespace(x=5, y=2)
        for region in ("heading", "separator"):
            with mock.patch.object(window.tree, "identify_region",
                                   return_value=region):
                self.assertEqual(window._on_tree_click(event), "break", region)

    def test_no_body_title_still(self):
        """正文不出现窗口标题；日志抓手文案亦不得使用该标题。"""
        window = self._make()
        labels = self._all_labels(window.root)
        self.assertNotIn(APP_TITLE, labels)

    def test_restore_from_config_selection(self):
        window = self._make(selected=["documents", "desktop"])
        self.assertEqual(window._checked, {"documents", "desktop"})

    def test_destination_options_are_fixed_drive_roots(self):
        """目标根目录选项必须是固定的 <盘>:\\UserData，不含用户名变体。"""
        window = self._make()
        options = list(window.dest_combo.cget("values"))
        self.assertTrue(options)
        for opt in options:
            self.assertRegex(opt, r"^[A-Z]:\\UserData$", msg=opt)
        # 只读下拉：不允许自由输入
        self.assertIn(str(window.dest_combo.cget("state")), ("readonly", "disabled"))

    def test_create_user_dir_checkbox_toggles_targets(self):
        """勾选「创建当前用户目录」：勾上 = <根>/<用户名>/<基名>，不勾 = <根>/<基名>。

        直接驱动真实 BooleanVar（而非绕过它写注册表变量），确保变量绑定
        没有被垃圾回收解除（历史 bug：内联创建的 var 被 GC 后勾选静默失效）。
        """
        from docmover import fsops

        window = self._make(selected=["documents"])
        window.dest_var.set("D:\\UserData")
        current = window._plans["documents"].current
        leaf = fsops.tail_name(current)

        window.create_user_dir_var.set(True)
        window._refresh_rows()
        checked_target = window._plans["documents"].target
        self.assertEqual(
            os.path.normcase(checked_target),
            os.path.normcase(os.path.join("D:\\UserData", os.environ.get("USERNAME", ""), leaf)),
        )

        window.create_user_dir_var.set(False)
        window._refresh_rows()
        unchecked_target = window._plans["documents"].target
        self.assertEqual(
            os.path.normcase(unchecked_target),
            os.path.normcase(os.path.join("D:\\UserData", leaf)),
        )

        # 变量必须真实存活：getvar 能读到当前值
        self.assertEqual(
            str(window.root.getvar(str(window.create_user_dir_var))),
            "0",
        )

    def test_validate_rejects_empty_selection(self):
        window = self._make(selected=[])
        error = window._validate()
        self.assertIsNotNone(error)
        self.assertIn("至少", error)

    def test_validate_rejects_bad_destination(self):
        """界面级唯一防线：越界目标必须被 _validate 以「系统目录」明确拒绝。

        P3-6：断言到具体消息，而不仅是「有错误」。

        注意：这里**刻意不调用 _refresh_rows()**。目标根目录是只读下拉，
        _refresh_rows → _refresh_destination_choices 会按只读语义把非法值
        回落到合法选项；若先刷新，就等于把要测的校验分支抹掉了（历史上
        该用例因此依赖真机盘符状态而时好时坏）。直接注入 dest_var 再校验，
        才能稳定命中「禁止系统目录」这一真实契约，且与机器状态无关。
        """
        window = self._make(selected=["documents"])
        window.dest_var.set("C:\\Windows")
        error = window._validate()
        self.assertIsNotNone(error)
        self.assertIn("系统目录", error)

    def test_refresh_resets_illegal_destination_to_valid_option(self):
        """只读下拉的契约：非法目标值会被回落为一个合法选项，不会留在界面上。"""
        window = self._make(selected=["documents"])
        options = list(window.dest_combo.cget("values"))
        self.assertTrue(options, "本用例需要至少一个可用目标盘")
        window.dest_var.set("C:\\Windows")
        window._refresh_rows()
        self.assertIn(window.dest_var.get(), options)

    def test_removed_buttons_absent(self):
        """刷新 / 保存设置 / 复制全部 按钮及其处理逻辑均已移除。"""
        window = self._make()
        for attr in ("btn_refresh", "btn_save", "btn_copy_log"):
            self.assertFalse(hasattr(window, attr), msg=attr)
        for method in ("_copy_log", "_save_settings", "_collect_config"):
            self.assertFalse(hasattr(window, method), msg=method)

    def test_basic_page_has_removed_fields(self):
        """基本参数页签已整体移除；执行前确认/重启资源管理器固定内置开启。"""
        window = self._make()
        for attr in ("title_var", "alias_var", "var_policy",
                     "var_restart_explorer", "var_confirm", "page_basic"):
            self.assertFalse(hasattr(window, attr), msg=attr)

        # 原页签对应的两项设置固定为开启：配置中不再保存、行为恒定
        self.assertFalse(hasattr(window.config, "restart_explorer"))
        self.assertFalse(hasattr(window.config, "confirm_before_transfer"))


    def test_regression_redline_removed_widgets_absent(self):
        """红线回归：已删控件、已删页签与日志抽屉一律不得复活。"""
        window = self._make()
        for attr in ("notebook", "page_transfer", "page_log",
                     "btn_refresh", "btn_save", "btn_copy_log",
                     "log_drawer", "log_toggle", "log_handle", "log_text"):
            self.assertFalse(hasattr(window, attr), msg=f"已删属性复活：{attr}")
        for method in ("_toggle_log_drawer", "_set_log_expanded",
                       "_refresh_log", "_on_ctrl_l", "_on_escape",
                       "_copy_log", "_save_settings", "_collect_config"):
            self.assertFalse(hasattr(window, method), msg=f"已删方法复活：{method}")

    def test_keyboard_bindings_match_design(self):
        """仅保留 Ctrl+Return；Ctrl+L / Esc 随抽屉一并移除，F5/Ctrl+S/Ctrl+C 不得出现。"""
        window = self._make()
        self.assertTrue(self.root.bind("<Control-Return>"),
                        "缺少 Ctrl+Return 绑定")
        for seq in ("<Control-l>", "<Control-L>", "<Escape>",
                    "<F5>", "<Control-s>", "<Control-S>",
                    "<Control-c>", "<Control-C>"):
            self.assertFalse(self.root.bind(seq), msg=f"已删功能快捷键复活：{seq}")

    # ==================================================================
    # 界面优化批次（P0 缺陷补全 + P1 体验增益）的回归锁
    # ==================================================================
    def test_dead_styles_removed_and_live_styles_kept(self):
        """P0-3：已无引用的样式不得复活；仍在用的样式必须存在。"""
        from docmover.ui.theme import COLORS

        window = self._make()
        style = window.theme.style
        # 纯标签 / 容器 / 勾选框 / 进度条样式：ttk 对「本进程未 configure 过」
        # 的名字返回 None，可直接判定
        for name in ("Title.TLabel", "Heading.TLabel", "Surface.TLabel",
                     "SurfaceMuted.TLabel", "Success.TLabel", "Danger.TLabel",
                     "Accent.TLabel", "Surface.TFrame", "Surface.TCheckbutton",
                     "TLabelframe.Label", "Countdown.Horizontal.TProgressbar"):
            self.assertIsNone(style.configure(name), msg=f"死样式复活：{name}")
        # TLabelframe / TEntry 是 clam 自带样式（无法用 None 判定），
        # 改为断言它们没有套用本项目的令牌值，即真的没被重绘
        self.assertNotEqual(style.lookup("TLabelframe", "bordercolor"),
                            COLORS["border_hair"], "TLabelframe 被重新着色")
        self.assertNotEqual(style.lookup("TEntry", "fieldbackground"),
                            COLORS["surface"], "TEntry 被重新着色")
        self.assertNotEqual(style.lookup("TEntry", "bordercolor", ("focus",)),
                            COLORS["accent"], "TEntry 焦点色被重新定义")
        # 仍在用的样式一个都不能少
        for name in ("TButton", "Accent.TButton", "Danger.TButton",
                     "Link.TButton", "WarningLink.TButton",
                     "Card.TFrame", "Status.TFrame",
                     "Muted.TLabel", "Summary.TLabel", "Warning.TLabel",
                     "Treeview", "Treeview.Heading"):
            self.assertTrue(style.configure(name), msg=f"在用样式丢失：{name}")

    def test_readonly_combobox_maps_text_color_explicitly(self):
        """用户实测缺陷：只读下拉框聚焦后文字整格消失（看着像"值被清空"）。

        根因：clam 的 TCombobox 把文字色挂在 ``-selectforeground``（白）上，
        而它的映射只在 ``!focus`` 命中 —— 焦点态回落到基值白；本主题又把只读态
        底色映射成 ``surface``（白），于是白字画在白底上不可见。
        （点开下拉再点空白处会让下拉框保持焦点，正是用户复现的那条路径。）

        修法 = 「映射了只读态底色，就必须同时映射只读态文字色」。这里锁**样式表
        本身**而不是 ``lookup``：``foreground`` 已由 ``configure`` 给了基值，
        ``lookup`` 在缺映射时也会返回同一个值，那样断言对缺陷不敏感。
        """
        from docmover.ui.theme import COLORS

        window = self._make()
        mapped = window.theme.style.map("TCombobox", "foreground")
        self.assertIn(("readonly", COLORS["text"]), mapped,
                      "只读态未显式映射文字色 —— 焦点时文字会回落到白色而看不见")
        self.assertIn(("disabled", COLORS["text_disabled"]), mapped,
                      "禁用态未显式映射文字色")
        # 与「底色被映射成 surface」这条前提配对，防止只改一半
        self.assertEqual(
            window.theme.style.lookup("TCombobox", "fieldbackground", ("readonly",)),
            COLORS["surface"],
        )

    def test_risk_branch_and_tag_removed(self):
        """P0-3：风险标签分支永假（7 项全为 normal），已随死代码一并移除。"""
        window = self._make()
        # 未设置的 tag 也会返回「全空值」的选项字典，故看取值而不是看键
        self.assertFalse(window.tree.tag_configure("risk")["foreground"],
                         "risk 行标签复活")
        self.assertTrue(window.tree.tag_configure("pending")["background"],
                        "缺少待确认行标签的底色")

    def test_destination_space_label(self):
        """P0-2：目标盘可用空间必须可见（选项文本保持 X:\\UserData 不变）。"""
        window = self._make()
        options = list(window.dest_combo.cget("values"))
        text = str(window.lbl_space.cget("text"))
        if options:
            self.assertTrue(window._has_destination)
            self.assertRegex(text, r"可用 .+ / .+", msg=text)
        else:
            self.assertIn("未检测到", text)

    def test_no_destination_disk_empty_state(self):
        """P0-1：没有任何非系统固定盘时，给出明确空态而不是空下拉 + 静默。"""
        from docmover import fsops

        window = self._make()
        with mock.patch.object(fsops, "list_fixed_drives", return_value=[]):
            window._refresh_destination_choices()
            window._update_summary()

        self.assertEqual(window.dest_var.get(), "")
        self.assertEqual(list(window.dest_combo.cget("values")), [])
        self.assertFalse(window._has_destination)
        self.assertIn("未检测到", str(window.lbl_space.cget("text")))
        self.assertIn("无可用目标磁盘", str(window.lbl_summary.cget("text")))
        self.assertIn("未检测到", window.status_var.get())
        self.assertEqual(str(window.btn_start.cget("state")), "disabled")
        self.assertIn("未检测到可用的目标磁盘", window._primary_tip_text())

    def test_primary_button_switches_to_stop_while_busy(self):
        """P0-4：执行中主按钮变为可点的「停止」，点击即请求取消。"""
        window = self._make()
        window._busy = True
        window._cancelable = True
        window._update_start_state()
        self.assertEqual(window.btn_start.cget("text"), "停止")
        self.assertEqual(str(window.btn_start.cget("state")), "normal")

        window._on_stop()
        self.assertTrue(window._cancel.is_set())
        self.assertIn("正在停止", window.status_var.get())
        self.assertEqual(window.btn_start.cget("text"), "正在停止…")
        self.assertEqual(str(window.btn_start.cget("state")), "disabled")

        # 收尾：回到空闲态应恢复主操作
        window._busy = False
        window._cancelable = False
        window._cancel.clear()
        window._update_start_state()
        self.assertEqual(window.btn_start.cget("text"), "开始转移")

    def test_uncancelable_task_shows_no_stop_entry(self):
        """恢复默认值只改注册表且引擎无中断回调：不给假的停止入口。"""
        window = self._make()
        window._start_work(lambda: None, cancelable=False, busy_label="恢复中…")
        self.assertEqual(window.btn_start.cget("text"), "恢复中…")
        self.assertEqual(str(window.btn_start.cget("state")), "disabled")

        # 重启资源管理器同样不可中断，但文案要说明自己在做什么（P1-6）
        window._busy_label = "重启中…"
        window._update_start_state()
        self.assertEqual(window.btn_start.cget("text"), "重启中…")

    def test_dest_combo_stays_readonly_after_busy(self):
        """P1-1 回归：忙碌结束后目标根目录必须恢复只读，而不是变成可编辑。"""
        window = self._make()
        if str(window.dest_combo.cget("state")) == "disabled":
            self.skipTest("机器上没有可用的非系统固定盘")

        window._busy = True
        window._set_controls_enabled(False)
        self.assertEqual(str(window.dest_combo.cget("state")), "disabled")

        window._busy = False
        window._set_controls_enabled(True)
        self.assertEqual(str(window.dest_combo.cget("state")), "readonly")

    def test_close_window_routes_to_exit_handler(self):
        """P0-3 回归：窗口 X 必须走 _on_exit 的确认路径，而不是默认 destroy。"""
        from docmover.ui import main_window as mw

        window = self._make()
        window._busy = True
        command = str(self.root.protocol("WM_DELETE_WINDOW"))
        self.assertNotIn("destroy", command, "关窗仍绑在 Tk 默认的 destroy 上")

        with mock.patch.object(mw.dialogs, "ask_yes_no", return_value=False) as asked:
            self.root.tk.call(command)
        asked.assert_called_once()

    def test_exit_waits_for_worker_before_destroy(self):
        """P0-3：执行中退出不得立刻销毁窗口，必须等收尾。"""
        from docmover.ui import main_window as mw

        window = self._make()
        window._busy = True
        window._cancelable = True
        with mock.patch.object(mw.dialogs, "ask_yes_no", return_value=True):
            with mock.patch.object(self.root, "destroy") as destroyed:
                window._on_exit()
                destroyed.assert_not_called()
                self.assertTrue(window._cancel.is_set())
                self.assertTrue(window._exit_requested)
                self.assertIn("安全收尾", window.status_var.get())

                # 工作线程收尾完成 → 轮询时销毁窗口
                window._busy = False
                window._poll_exit()
                destroyed.assert_called_once()

        # 空闲时点退出：直接销毁
        window._exit_requested = False
        window._busy = False
        with mock.patch.object(self.root, "destroy") as destroyed:
            window._on_exit()
            destroyed.assert_called_once()

    def test_primary_tooltip_explains_disabled_reason(self):
        """P1-9：主按钮禁用时，提示要说明原因，而不是沉默的灰按钮。"""
        window = self._make(selected=[])
        window._update_start_state()
        self.assertEqual(str(window.btn_start.cget("state")), "disabled")
        self.assertIn("请先", window._tip_start.text)

        window._toggle("documents")
        window._update_start_state()
        self.assertIn("复制并校验", window._tip_start.text)

    def test_restore_defaults_dialog_text_stays_compact(self):
        """用户两次反馈「恢复默认」弹窗文案太长。这里逐字钉死并卡住长度，
        防止编号列举与重复的安全声明又被加回来。"""
        from docmover.ui import main_window as mw

        window = self._make()
        with mock.patch.object(mw.dialogs, "ask_yes_no", return_value=False) as asked:
            window._on_restore_defaults()

        asked.assert_called_once()
        args, kwargs = asked.call_args
        self.assertEqual(args[1], "恢复系统默认值")
        self.assertEqual(kwargs.get("kind"), "warning")
        message = args[2]

        blocks = message.split("\n\n")
        self.assertEqual(len(blocks), 4, f"段落数应为 4，实际 {len(blocks)}：{blocks}")
        self.assertEqual(blocks[0], "以下项目将恢复为系统默认：")
        self.assertEqual(
            blocks[2],
            "内容搬回系统默认目录（如 C:\\Users\\<当前用户>\\Documents），"
            "注册表改回指向该目录。先复制校验，全部通过后才写注册表、删原目录；"
            "任一步失败都不删源目录、不改注册表。",
        )
        self.assertEqual(blocks[3], "空间不足的项目单独跳过并列出缺口，其余照常执行。")

        prose = message.replace(blocks[1], "")
        self.assertLessEqual(len(prose), 160, f"恢复默认文案又变长了：{prose}")

    def test_pending_highlight_tag_configured_before_zebra(self):
        """「本次将转移」的浅蓝高亮必须先于斑马纹 odd/even 定义。

        ttk 标签冲突时**先 tag_configure 的赢**（与 item 的 tags 列表顺序无关，
        已用红/绿双色实验证实）。曾因 pending 定义在最后而被斑马纹永久盖掉，
        出现「确认弹窗说已用浅蓝底色标出，列表里却看不到高亮」。
        """
        from docmover.ui.theme import COLORS

        window = self._make()

        class _SpyTree:
            def __init__(self):
                self.calls = []

            def tag_configure(self, name, **_kw):
                self.calls.append(name)

        spy = _SpyTree()
        window.theme.apply_treeview_row_tags(spy)
        self.assertLess(spy.calls.index("pending"), spy.calls.index("odd"))
        self.assertLess(spy.calls.index("pending"), spy.calls.index("even"))

        # 高亮色必须与两种斑马底色都可区分，否则视觉上等于没有
        self.assertNotEqual(COLORS["accent_soft"], COLORS["surface"])
        self.assertNotEqual(COLORS["accent_soft"], COLORS["row_alt"])

    def test_prescan_removed(self):
        """产品决策（2026-10-14）：规模预统计已彻底移除。

        用户机器待统计内容达 58 GB / 数十万文件，勾选后自动扫描是分钟级开销，
        而执行时引擎本来就要权威扫描并做空间闸门把关——预览那份统计成了纯开销。
        本用例守卫移除状态：勾选/切换不得触发任何读盘调度。
        """
        window = self._make()
        self.assertFalse(hasattr(window, "_prescan"))
        self.assertFalse(hasattr(window, "_stat_cache"))
        self.assertFalse(hasattr(window, "_schedule_scan"))
        self.assertFalse(hasattr(window, "_start_scan"))

        # 勾选 / 全选后不得排程任何后台任务
        with mock.patch.object(threading, "Thread") as thread:
            window._toggle("documents")
            window._on_select_all()
            thread.assert_not_called()

        # 摘要只显示勾选计数，不再出现「共 …」规模聚合
        window._update_summary()
        self.assertNotIn("共 ", window.lbl_summary.cget("text"))

    def test_double_click_open_location_removed(self):
        """产品决策（2026-10-14）：「双击行打开所在位置」与常驻操作提示均已移除。

        双击易误触且与勾选语义冲突；失败详情由结果弹窗与「打开日志目录」承担。
        本用例守卫移除状态，防止未来无意间把旧交互加回来。
        """
        window = self._make()
        self.assertFalse(hasattr(window, "lbl_hint"), "常驻操作提示标签应已移除")
        self.assertFalse(hasattr(window, "_activate_row"), "双击处理逻辑应已移除")
        self.assertEqual(
            window.tree.bind("<Double-1>"), "", "双击不应再绑定任何处理函数"
        )

    def test_checkbox_icon_colors_match_tokens(self):
        """图标渲染回归：勾选态必须是 accent 蓝实心 + 白勾（2026-10-14 修复）。

        此前 _render 的颜色合成把 Σ(cr)/Σ(ca) 当作最终色，深色被压成黑色
        （accent 蓝 240//4080=0），勾选框一直呈黑色；修复后必须命中令牌色。
        期望值一律取自 icons 的 _RING_* 渲染常量（与 theme.COLORS 语义同步
        维护），守卫意图不变：图标实测色必须等于令牌色。

        2026-10-14 QA 补强：上面的渲染断言两侧同取 icons 常量，对「常量本身
        被改错」天然免疫（期望值跟着实现一起变）；故另加 icons ↔ theme 的
        双源一致性钉死断言，防止主题改色忘改 icons 的漂移。
        """
        from docmover.ui.icons import (_CHECK, _RING_OFF, _RING_OFF_HOVER,
                                       _RING_ON, _RING_ON_PRESSED, IconFactory)
        from docmover.ui.theme import COLORS, _hex2rgb

        # 双源一致性（icons 渲染常量 ↔ theme 语义令牌）：
        self.assertEqual(_RING_ON, (0x0F, 0x6C, 0xBD), "accent 令牌值漂移")
        self.assertEqual(_RING_ON, _hex2rgb(COLORS["accent"]),
                         "icons._RING_ON 与 theme accent 不同步")
        self.assertEqual(_RING_ON_PRESSED, (0x0C, 0x3B, 0x5E),
                         "accent_pressed 令牌值漂移")
        self.assertEqual(_RING_ON_PRESSED, _hex2rgb(COLORS["accent_pressed"]),
                         "icons._RING_ON_PRESSED 与 theme accent_pressed 不同步")
        self.assertEqual(_RING_OFF, _hex2rgb(COLORS["border"]),
                         "icons._RING_OFF 与 theme border 不同步")
        self.assertEqual(_RING_OFF_HOVER, _hex2rgb(COLORS["border_strong"]),
                         "icons._RING_OFF_HOVER 与 theme border_strong 不同步")
        self.assertEqual(_CHECK, (255, 255, 255), "对勾颜色必须是纯白")

        icons = IconFactory(self.root, 1.0)
        on = icons.checkbox(True)
        off = icons.checkbox(False)
        self.assertEqual(on.get(12, 12), _RING_ON, "勾选态描边应为 accent 蓝")
        self.assertEqual(on.get(8, 8), _CHECK, "对勾应为白色")
        self.assertEqual(on.get(5, 5), _RING_ON, "勾选态内部应实心填充")
        self.assertEqual(off.get(12, 12), _RING_OFF, "未选态描边应为 border 灰")
        self.assertEqual(off.get(5, 5), (0, 0, 0), "未选态内部应透明")

    def test_status_icons_for_dialog_kinds(self):
        """弹窗语义图标：五种 kind 都能渲染出 40px 图标。"""
        from docmover.ui.icons import IconFactory

        icons = IconFactory(self.root, 1.0)
        for kind in ("info", "question", "warning", "error", "success"):
            img = icons.status(kind)
            self.assertEqual((img.width(), img.height()), (40, 40))

    def test_logs_link_hidden_until_failure(self):
        """P1-10：日志目录入口默认收起，仅在失败/异常后出现。"""
        window = self._make()
        self.assertEqual(dict(window.btn_open_logs.grid_info()), {},
                         "日志入口不应常驻状态栏")
        window._show_logs_link()
        info = dict(window.btn_open_logs.grid_info())
        self.assertTrue(info, "失败后未露出日志入口")
        self.assertEqual(window.btn_open_logs.cget("text"), "打开日志目录")

    # ==================================================================
    # 审计 C1/C3/C6/C7/C8/C14 的回归锁
    # ==================================================================
    def test_drain_queue_surfaces_callback_failure(self):
        """审计 C1：队列回调抛错必须落到用户可见处，不能只写进日志文件。

        坏实现（只 ``log.exception``）会让界面停在半刷新态——摘要说"已选择 7/7 项"
        而列表可能是空的、主按钮还能点——用户完全看不出出过错。
        """
        window = self._make()
        self.assertEqual(dict(window.btn_open_logs.grid_info()), {}, "前置：日志入口默认收起")

        def boom():
            raise RuntimeError("build_plan 炸了")

        window._post(boom)
        window._drain_queue()

        self.assertIn("界面刷新失败", window.status_var.get())
        self.assertTrue(dict(window.btn_open_logs.grid_info()),
                        "回调失败后没有露出日志出口")

    def test_thread_start_failure_recovers_busy_state(self):
        """审计 C3：线程起不来时必须就地复位 _busy。

        坏实现（不包 try）下异常冒到 Tk 回调、静默丢弃，_busy 永远是 True：
        主按钮停在"处理中…"且不可点，只剩关窗一条路。
        """
        from docmover.ui import main_window as mw

        window = self._make(selected=[])
        with mock.patch.object(threading, "Thread", side_effect=RuntimeError("no threads")), \
             mock.patch.object(mw.dialogs, "show_error") as shown:
            window._start_work(lambda: None)

        self.assertFalse(window._busy, "线程起不来却把界面永久锁在忙碌态")
        self.assertEqual(str(window.btn_restore.cget("state")), "normal",
                         "失败后输入类控件没有被恢复")
        shown.assert_called_once()

    def test_worker_baseexception_still_unlocks_ui(self):
        """审计 C3：worker 抛 BaseException 也必须走到错误回调。

        坏实现（只接 ``Exception``）下 ``SystemExit`` 会穿出 worker 函数，没有任何
        回调复位 _busy——主按钮永久卡在"停止 / 处理中…"。
        """
        from docmover.ui import main_window as mw

        window = self._make()
        window._busy = True          # 模拟 _start_work 已经把界面置忙
        with mock.patch.object(mw.dialogs, "show_error") as shown, \
             mock.patch.object(window.engine, "build_plan", return_value=[]), \
             mock.patch.object(window.engine, "execute", side_effect=SystemExit(3)):
            window._work_transfer([], "D:\\UserData", True)
            window._drain_queue()    # _post 的回调在主线程队列里，这里手动排空

        self.assertFalse(window._busy, "worker 异常退出后 _busy 没有复位")
        shown.assert_called_once()

    def test_checkbox_toggle_ignored_while_busy(self):
        """审计 C6：忙碌期勾选已定稿，点列表不得改集合。

        坏实现下用户能在转移进行中改勾选，与"全选/创建用户目录/恢复默认值"这些
        已灰控件的口径不一致，还会让 _render_marks 把正在处理那行的"处理中…"
        写回旧描述。
        """
        window = self._make()
        self.assertIn("documents", window._known_keys, "前置：documents 必须是已知项")
        before = set(window._checked)

        window._busy = True
        window._toggle("documents")
        self.assertEqual(window._checked, before, "忙碌期勾选被改动了")

        window._busy = False
        window._toggle("documents")
        self.assertNotEqual(window._checked, before, "空闲期勾选应当生效")

    def test_no_destination_hint_is_withdrawn_when_disk_returns(self):
        """审计 C7：固定盘恢复可用后，状态栏不得再留着"未检测到可用的目标磁盘"。

        坏实现（只在无盘时 set、有盘时不撤）会让界面同时说"有目标盘"（摘要）与
        "没有可用目标磁盘"（状态栏），用户不知道信哪个。
        """
        from docmover import fsops

        window = self._make()
        with mock.patch.object(fsops, "list_fixed_drives", return_value=[]):
            window._refresh_destination_choices()
            window._update_summary()
        self.assertIn("未检测到可用的目标磁盘", window.status_var.get(),
                      "前置：无盘时应先给出空态提示")

        drive = fsops.DriveInfo(letter="D:", total_bytes=1 << 40, free_bytes=1 << 39)
        with mock.patch.object(fsops, "list_fixed_drives", return_value=[drive]):
            window._refresh_destination_choices()
            window._update_summary()

        self.assertTrue(window._has_destination)
        self.assertNotIn("未检测到可用的目标磁盘", window.status_var.get())

    def test_progress_resets_after_task_finishes(self):
        """审计 C8：任务收尾后进度条必须归零，否则永久停在 100% 像"还在跑"。"""
        window = self._make()
        window.progress.configure(value=100)
        window._busy = True
        window._finish_work()
        self.assertEqual(float(window.progress.cget("value")), 0.0)
        self.assertFalse(window._busy)

    def test_exit_prompt_matches_uncancelable_task(self):
        """审计 C14：不可中断的任务没有停止入口，退出提示不得让别人"先点停止"。"""
        from docmover.ui import main_window as mw

        window = self._make()

        window._busy = True
        window._cancelable = False
        with mock.patch.object(mw.dialogs, "ask_yes_no", return_value=False) as asked:
            window._on_exit()
        uncancelable_prompt = asked.call_args[0][2]
        self.assertNotIn("建议先点主按钮上的「停止」", uncancelable_prompt)
        self.assertIn("不可中断", uncancelable_prompt)

        window._cancelable = True
        with mock.patch.object(mw.dialogs, "ask_yes_no", return_value=False) as asked:
            window._on_exit()
        self.assertIn("建议先点主按钮上的「停止」", asked.call_args[0][2])

    # ---------------------------------- 意图日志回滚裁剪（审计 C15-fix / R9）
    @staticmethod
    def _pending_entry(*, key, stage, registry, registry_known=True):
        """造一条 `IntentJournal.pending()` 形态的 entry（字段与 transfer.py 对齐）。"""
        return {
            "key": key,
            "display": key,
            "source": f"C:/src/{key}",
            "target": f"D:/dst/{key}",
            "stage": stage,
            "snapshot": "C:/fake/snapshot.json",
            "registry": registry,
            "registry_known": registry_known,
        }

    def _drive_pending_intent(self, window, *, pending, targets, snapshot, answer=True,
                              rollback_side_effect=None):
        """打桩驱动 `_check_pending_intent`（不读真实日志、不动真实注册表）。

        `rollback_side_effect` 非空时用 `side_effect` 替换 `store.rollback`
        （供模拟「回滚时跳过若干条、顺带写 `last_rollback_skipped`」）。
        返回 `(rollback_mock, show_info, show_success, show_error)`。
        """
        from docmover.ui import main_window as mw

        with contextlib.ExitStack() as stack:
            stack.enter_context(
                mock.patch.object(mw.IntentJournal, "pending", return_value=pending))
            stack.enter_context(mock.patch.object(
                mw.IntentJournal, "unfinished_registry_targets",
                return_value=set(targets)))
            stack.enter_context(mock.patch.object(
                mw.IntentJournal, "journal_path", return_value=None))
            stack.enter_context(
                mock.patch.object(mw, "load_snapshot", return_value=snapshot))
            if rollback_side_effect is None:
                rollback = stack.enter_context(
                    mock.patch.object(window.store, "rollback", return_value=(1, 0)))
            else:
                rollback = stack.enter_context(
                    mock.patch.object(window.store, "rollback",
                                      side_effect=rollback_side_effect))
            stack.enter_context(mock.patch.object(
                mw.dialogs, "ask_yes_no_cancel", return_value=answer))
            info = stack.enter_context(mock.patch.object(mw.dialogs, "show_info"))
            success = stack.enter_context(mock.patch.object(mw.dialogs, "show_success"))
            error = stack.enter_context(mock.patch.object(mw.dialogs, "show_error"))

            window.dry_run = False   # dry_run 下 _check_pending_intent 直接返回
            window._check_pending_intent()
        return rollback, info, success, error

    def test_pending_rollback_all_completed_skips_rollback(self):
        """审计 C15-fix：残留项若都已成功收尾，裁剪后快照为空 → 不得回滚、走 info。"""
        from docmover.registry import RegistryEntry, RegistrySnapshot

        window = self._make()
        pending = [self._pending_entry(
            key="documents", stage="source-removed",
            registry=[["HKCU\\X", "ValA"]])]
        snapshot = RegistrySnapshot(
            entries=[RegistryEntry(subkey="HKCU\\X", name="ValA")], created_at="t")

        rollback, info, success, error = self._drive_pending_intent(
            window, pending=pending, targets=[], snapshot=snapshot)

        rollback.assert_not_called()
        info.assert_called_once()
        success.assert_not_called()
        error.assert_not_called()

    def test_pending_rollback_prunes_completed_entries(self):
        """审计 C15-fix：只回滚「未完成项」涉及的值，已成功项不得被滚回旧路径。"""
        from docmover.registry import RegistryEntry, RegistrySnapshot

        window = self._make()
        pending = [
            self._pending_entry(key="done", stage="source-removed",
                                registry=[["HKCU\\Done", "Vd"]]),
            self._pending_entry(key="todo", stage="pending",
                                registry=[["HKCU\\Todo", "Vt"]]),
        ]
        snapshot = RegistrySnapshot(entries=[
            RegistryEntry(subkey="HKCU\\Done", name="Vd"),
            RegistryEntry(subkey="HKCU\\Todo", name="Vt"),
        ], created_at="t")

        rollback, info, success, error = self._drive_pending_intent(
            window, pending=pending, targets={("HKCU\\Todo", "Vt")}, snapshot=snapshot)

        rollback.assert_called_once()
        passed = rollback.call_args[0][0]
        self.assertEqual(
            [(e.subkey, e.name) for e in passed.entries],
            [("HKCU\\Todo", "Vt")],
            "回滚收到的快照应当只含未完成项条目",
        )
        info.assert_not_called()
        success.assert_called_once()
        self.assertIn("已跳过 1 处", success.call_args[0][2])
        # C17 反向护栏：skipped == 0 时不得引入「因快照读取失败…跳过」空句
        self.assertNotIn("因快照读取失败", success.call_args[0][2])

    def test_pending_rollback_legacy_log_keeps_full_snapshot(self):
        """审计 C15-fix：旧日志缺 registry 字段时不得裁剪，保持整份回滚。"""
        from docmover.registry import RegistryEntry, RegistrySnapshot

        window = self._make()
        # 旧版日志没有 "registry" 键：pending() 归一为 [] + registry_known=False
        pending = [self._pending_entry(
            key="todo", stage="pending", registry=[], registry_known=False)]
        snapshot = RegistrySnapshot(entries=[
            RegistryEntry(subkey="HKCU\\Done", name="Vd"),
            RegistryEntry(subkey="HKCU\\Todo", name="Vt"),
        ], created_at="t")

        rollback, info, success, error = self._drive_pending_intent(
            window, pending=pending, targets={("HKCU\\Todo", "Vt")}, snapshot=snapshot)

        rollback.assert_called_once()
        passed = rollback.call_args[0][0]
        self.assertEqual(len(passed.entries), 2, "旧日志不得裁剪整份快照")
        self.assertIn("旧版本", success.call_args[0][2])

    def test_rollback_reports_skipped_unknown_values(self):
        """审计 C17：因快照读取失败被跳过、未做改动的条数必须出现在成功文案里。

        `RegistryStore.rollback()` 会把这类条数记进 `last_rollback_skipped`，但生产
        代码此前从未读它——用户只看到「恢复 N 处、删除 M 处」，看不到有若干处被静默
        跳过（与 C1/C4「不让界面说谎」同类）。
        """
        from docmover.registry import RegistryEntry, RegistrySnapshot

        window = self._make()
        pending = [self._pending_entry(
            key="todo", stage="pending", registry=[["HKCU\\Todo", "Vt"]])]
        snapshot = RegistrySnapshot(
            entries=[RegistryEntry(subkey="HKCU\\Todo", name="Vt")], created_at="t")

        def fake_rollback(_snapshot):
            window.store.last_rollback_skipped = 2   # 模拟 2 处因读失败被跳过
            return (0, 0)

        rollback, info, success, error = self._drive_pending_intent(
            window, pending=pending, targets={("HKCU\\Todo", "Vt")},
            snapshot=snapshot, rollback_side_effect=fake_rollback)

        rollback.assert_called_once()
        success.assert_called_once()
        message = success.call_args[0][2]
        self.assertIn("2 处", message)
        self.assertIn("跳过", message)
        self.assertIn("人工核对", message)

    # ---------------------------------- 意图日志损坏的可见信号（审计 B1）
    def test_corrupt_intent_journal_is_surfaced(self):
        """审计 B1：日志损坏（半截 JSON）时，启动检查必须消费读取错误并弹警示。

        坏实现下 ``_check_pending_intent`` 只调 ``pending()``：损坏日志让它静默返回
        ``[]``，界面与日志都收不到「上次可能有半完成项」——这是唯一的收尾信号。
        本用例用**真实**的损坏文件驱动**生产方法** ``_check_pending_intent``，
        只打桩弹窗出口，断言出现 ``show_warning``（"需注意"类）而不是静默早退。
        """
        from docmover.transfer import JOURNAL_FILENAME, IntentJournal
        from docmover.ui import main_window as mw

        window = self._make()
        # 项目惯例：临时目录优先落在 D:\tmp（不在系统盘 C: 上造目录）；无 D: 盘时
        # 退回系统临时目录，保证 CI 可移植。
        base = "D:/tmp" if os.path.isdir("D:/tmp") else None
        backup = tempfile.TemporaryDirectory(dir=base)
        self.addCleanup(backup.cleanup)
        (Path(backup.name) / JOURNAL_FILENAME).write_text(
            '{"items": {', encoding="utf-8")   # 半截 JSON
        window.backup_dir = Path(backup.name)
        window.dry_run = False

        IntentJournal.consume_read_error()   # 清掉可能残留的全局错误
        with mock.patch.object(mw.dialogs, "show_warning") as warned, \
             mock.patch.object(mw.dialogs, "ask_yes_no_cancel") as asked:
            window._check_pending_intent()

        warned.assert_called_once()
        asked.assert_not_called()
        self.assertIn("意图日志", warned.call_args[0][2])

    # ==================================================================
    # 审计 R1 / R3 / R4 的回归锁
    # ==================================================================
    @staticmethod
    def _fake_report(*, succeeded=True, cancelled=False, failed=False):
        """造一个 `_after_transfer` 够用的 TransferReport 替身。"""
        from types import SimpleNamespace

        outcome = SimpleNamespace(
            key="documents", status="ok", label="✓", display="我的文档",
            message="已转移",
        )
        return SimpleNamespace(
            outcomes=[outcome], summary=lambda: "共 1 项，成功 1 项",
            cancelled=cancelled, failed=failed, succeeded=succeeded,
            snapshot_path=None,
        )

    def test_poll_exit_does_not_destroy_during_ui_wrap_up(self):
        """R1：收尾 UI（结果弹窗 / 状态栏 / 重启确认）期间不得销毁窗口。

        坏实现只看 ``_busy``，而它在 ``_finish_work`` 第一行就复位 → 弹窗还在屏幕
        上时窗口已被销毁：用户一次也看不到总结，回调还抛 TclError 进日志。
        """
        window = self._make()
        window._exit_requested = True
        window._busy = False
        window._finishing_ui = True          # 模拟正在弹结果窗

        with mock.patch.object(self.root, "destroy") as destroyed:
            window._poll_exit()
        destroyed.assert_not_called()

        # 收尾 UI 结束 → 立即销毁
        window._finishing_ui = False
        with mock.patch.object(self.root, "destroy") as destroyed:
            window._poll_exit()
        destroyed.assert_called_once()

    def test_poll_exit_survives_already_destroyed_root(self):
        """R4：根窗口已被别的路径销毁时，``destroy()`` 不得就地抛 TclError。"""
        window = self._make()
        window._exit_requested = True
        with mock.patch.object(self.root, "destroy", side_effect=tk.TclError("gone")):
            window._poll_exit()   # 不得抛出

    def test_after_transfer_holds_finishing_flag_across_dialogs(self):
        """R1：``_finishing_ui`` 必须覆盖**整个**收尾 UI，含弹窗期间。"""
        from docmover.ui import main_window as mw

        window = self._make()
        seen = {}

        def spy(*_a, **_k):
            seen["finishing"] = window._finishing_ui
            return None

        with mock.patch.object(mw.dialogs, "show_success", side_effect=spy), \
             mock.patch.object(window, "_confirm_restart_explorer"):
            window._after_transfer(self._fake_report(succeeded=True))

        self.assertTrue(seen.get("finishing"), "弹窗期间 _finishing_ui 未生效")
        self.assertFalse(window._finishing_ui, "收尾结束后标志没有复位")

    def test_after_transfer_short_circuits_when_exit_requested(self):
        """R1：用户已请求退出 → 只落日志，不弹窗、不进重启确认。"""
        from docmover.ui import main_window as mw

        window = self._make()
        window._busy = True
        window._exit_requested = True

        with mock.patch.object(mw.dialogs, "show_info") as info, \
             mock.patch.object(mw.dialogs, "show_warning") as warn, \
             mock.patch.object(mw.dialogs, "show_success") as ok, \
             mock.patch.object(mw.dialogs, "show_error") as err, \
             mock.patch.object(window, "_confirm_restart_explorer") as restart:
            window._after_transfer(self._fake_report(succeeded=True))

        for shown in (info, warn, ok, err):
            shown.assert_not_called()
        restart.assert_not_called()
        self.assertFalse(window._busy, "早退也必须复位忙碌态，否则退出永远卡住")
        self.assertFalse(window._finishing_ui)

    def test_confirm_restart_explorer_never_runs_after_exit_requested(self):
        """R1 铁律：退出后绝不能再走到 ``taskkill explorer``。

        ``_after_transfer`` 已经早退，这里是最后一道闸门：本方法被任何路径直接
        调用时都必须短路（否则用户关窗后后台还去强杀系统 shell）。
        """
        from docmover import shell
        from docmover.ui import main_window as mw

        window = self._make()
        window._exit_requested = True

        with mock.patch.object(mw.dialogs, "ask_yes_no") as asked, \
             mock.patch.object(window, "_start_work") as started, \
             mock.patch.object(shell, "restart_explorer") as restarted:
            window._confirm_restart_explorer()

        asked.assert_not_called()
        started.assert_not_called()
        restarted.assert_not_called()

    def test_worker_error_short_circuits_when_exit_requested(self):
        """R1：退出收尾期间的工作线程异常同样不弹窗，且要复位忙碌态。"""
        from docmover.ui import main_window as mw

        window = self._make()
        window._busy = True
        window._exit_requested = True

        with mock.patch.object(mw.dialogs, "show_error") as err:
            window._on_worker_error(RuntimeError("boom"))

        err.assert_not_called()
        self.assertFalse(window._busy)
        self.assertFalse(window._finishing_ui)

    def test_rollback_from_snapshot_short_circuits_when_exit_requested(self):
        """R1：退出收尾期间的回滚结果弹窗同样跳过。"""
        from docmover.ui import main_window as mw

        window = self._make()
        window._exit_requested = True

        with mock.patch.object(mw.dialogs, "show_error") as err, \
             mock.patch.object(mw.dialogs, "show_success") as ok, \
             mock.patch.object(mw.dialogs, "show_info") as info:
            window._rollback_from_snapshot("C:/nonexistent.json")

        for shown in (err, ok, info):
            shown.assert_not_called()

    def test_tk_callback_exception_hook_is_installed(self):
        """R3：``--windowed`` 下 Tk 回调异常默认被静默丢弃，必须装钩子接管。"""
        window = self._make()
        installed = self.root.report_callback_exception
        self.assertIsNotNone(installed)
        self.assertEqual(installed, window._on_tk_exception)

    def test_tk_callback_exception_is_logged_not_swallowed(self):
        """R3：钩子必须把 traceback 落进日志，且**不弹窗**干扰用户。"""
        from docmover.ui import main_window as mw

        window = self._make()
        try:
            raise RuntimeError("按钮回调炸了")
        except RuntimeError as exc:
            with self.assertLogs("DirMover", level="ERROR") as captured, \
                 mock.patch.object(mw.dialogs, "show_error") as err:
                window._on_tk_exception(type(exc), exc, exc.__traceback__)

        err.assert_not_called()
        joined = "\n".join(captured.output)
        self.assertIn("Tk 回调异常", joined)
        # 不吞异常：traceback 必须完整落盘
        self.assertIn("RuntimeError", joined)
        self.assertIn("按钮回调炸了", joined)
        self.assertIn("Traceback", joined)


if __name__ == "__main__":
    unittest.main(verbosity=2)
