"""主题化弹窗（dialogs）的回归测试。

用真实 Tk 事件循环驱动：先安排一个 after 回调去点击按钮 / 发送 Esc，
再调用 show() 阻塞等待，验证返回值与界面结构。
"""

from __future__ import annotations

import tkinter as tk
import unittest

from docmover.ui import dialogs


class DialogsTest(unittest.TestCase):
    def setUp(self):
        self.root = tk.Tk()
        self.root.withdraw()

    def tearDown(self):
        try:
            self.root.destroy()
        except tk.TclError:
            pass

    # ------------------------------------------------------------ 助手
    @staticmethod
    def _destroy_quietly(widget):
        """销毁控件，容忍「Tk 解释器已随根窗口销毁」——``addCleanup`` 在
        ``tearDown`` 之后执行，此时 root 已被销毁，再 destroy 子控件会抛
        ``TclError: cannot invoke "destroy" command: application has been
        destroyed``（控件其实已随根窗口一起没了）。"""
        try:
            widget.destroy()
        except tk.TclError:
            pass

    def _map_root_or_skip(self):
        """grab 需要窗口可映射；无图形会话时跳过（与既有用例同一套手法）。"""
        try:
            self.root.deiconify()
            self.root.update()
        except tk.TclError:
            self.skipTest("无图形会话")
        if not self.root.winfo_ismapped():
            self.skipTest("无图形会话")

    def _children_labels(self, dialog: "dialogs.ModalDialog") -> list:
        labels = []
        for child in dialog.tl.winfo_children():
            for sub in child.winfo_children():
                if isinstance(sub, tk.Label):
                    labels.append(sub)
        return labels

    # ------------------------------------------------------------ 用例
    def test_show_info_builds_title_paragraphs_and_returns_none(self):
        captured = {}

        def inspect_and_close():
            captured["title"] = dialog.tl.title()
            labels = self._children_labels(dialog)
            texts = [lb.cget("text") for lb in labels]
            captured["texts"] = texts
            dialog._finish(None)

        dialog = dialogs.ModalDialog(
            self.root, "标题甲", "第一段。\n\n第二段。", kind="info",
        )
        self.root.after(60, inspect_and_close)
        result = dialog.show()

        self.assertIsNone(result)
        self.assertEqual(captured["title"], "标题甲")
        # 图标 + 标题 + 两个正文段落
        self.assertEqual(len(captured["texts"]), 4)
        self.assertIn("第一段。", captured["texts"])
        self.assertIn("第二段。", captured["texts"])

    def test_ask_yes_no_click_yes_returns_true(self):
        dialog = dialogs.ModalDialog(
            self.root, "确认", "继续吗？", kind="question",
            buttons=(("是", "primary", True), ("否", "normal", False)),
            cancel_value=False,
        )
        self.root.after(60, dialog._default_btn.invoke)
        self.assertTrue(dialog.show())

    def test_ask_yes_no_escape_returns_false(self):
        dialog = dialogs.ModalDialog(
            self.root, "确认", "继续吗？", kind="question",
            buttons=(("是", "primary", True), ("否", "normal", False)),
            cancel_value=False,
        )
        # 键盘契约：Escape 必须绑定了「取消」回调（真键模拟在 grab 下不可靠）
        self.assertTrue(str(dialog.tl.bind("<Escape>")), "Escape 未绑定取消语义")
        self.root.after(60, dialog._on_close)
        self.assertFalse(dialog.show())

    def test_yes_no_cancel_close_returns_none(self):
        """直接关闭窗口 = 「取消」，返回 None（对应原 askyesnocancel 语义）。"""
        dialog = dialogs.ModalDialog(
            self.root, "未收尾", "上次有转移没走完", kind="warning",
            buttons=(("是", "primary", True), ("否", "normal", False),
                     ("取消", "normal", None)),
            cancel_value=None,
        )
        self.assertTrue(str(dialog.tl.bind("<Return>")), "Return 未绑定默认按钮")
        self.root.after(60, dialog._on_close)
        self.assertIsNone(dialog.show())

    def test_yes_no_cancel_no_button_returns_false(self):
        dialog = dialogs.ModalDialog(
            self.root, "未收尾", "上次有转移没走完", kind="warning",
            buttons=(("是", "primary", True), ("否", "normal", False),
                     ("取消", "normal", None)),
            cancel_value=None,
        )
        self.root.after(60, lambda: dialog._btn_widgets[1].invoke())
        self.assertFalse(dialog.show())

    def test_button_styles_primary_last_visually_rightmost(self):
        """按钮行右对齐、主按钮在最先创建位（视觉最左为次要、最右…）——
        这里只验证样式与数量：主按钮用 Accent，其余用默认。"""
        dialog = dialogs.ModalDialog(
            self.root, "未收尾", "内容", kind="warning",
            buttons=(("是", "primary", True), ("否", "normal", False),
                     ("取消", "normal", None)),
            cancel_value=None,
        )
        styles = [str(b.cget("style")) for b in dialog._btn_widgets]
        self.assertEqual(styles[0], "Accent.TButton")
        self.assertEqual(styles[1], "TButton")
        self.assertEqual(styles[2], "TButton")

    def test_dialog_maps_even_when_parent_is_hidden(self):
        """父窗口不可见（withdraw）时不得设 transient。

        Tk 的 ``wm transient`` 规定：master 未映射时，transient 子窗口
        **永远不会被映射**（实测 ``winfo_ismapped()`` 恒为 0）——弹窗会
        「不出现」。第二个实例的提示就走这条路径（根窗口先隐藏），
        所以这里钉住「退化为普通独立窗口且确实能显示」。
        """
        self.assertFalse(self.root.winfo_ismapped(), "前置条件：根窗口应处于隐藏态")

        dialog = dialogs.ModalDialog(self.root, "提示", "内容")
        try:
            self.assertEqual(str(dialog.tl.transient()), "",
                             "父窗口隐藏时仍设了 transient，弹窗将无法显示")
            dialog.tl.deiconify()
            dialog.tl.update()
            self.assertEqual(dialog.tl.winfo_ismapped(), 1, "弹窗未能被映射")
        finally:
            dialog.tl.destroy()

    def test_hidden_parent_dialog_centers_on_screen(self):
        """父窗口隐藏时没有可用参照矩形，弹窗必须按屏幕居中，不能贴角或跑出屏。"""
        dialog = dialogs.ModalDialog(self.root, "提示", "内容")
        try:
            dialog.tl.deiconify()
            dialog.tl.update()
            dialog._center_on_parent()
            dialog.tl.update()

            x, y = dialog.tl.winfo_x(), dialog.tl.winfo_y()
            sw, sh = dialog.tl.winfo_screenwidth(), dialog.tl.winfo_screenheight()
            self.assertGreaterEqual(x, 0)
            self.assertGreaterEqual(y, 0)
            self.assertLess(x, sw, "弹窗横向跑出屏幕")
            self.assertLess(y, sh, "弹窗纵向跑出屏幕")
        finally:
            dialog.tl.destroy()

    def test_parent_mapped_dialog_still_transient(self):
        """反向护栏：父窗口可见时 transient 必须照旧生效（不能被上面的退化改坏）。"""
        try:
            self.root.deiconify()
            self.root.update()
        except tk.TclError:
            self.skipTest("无法映射根窗口（无图形会话）")
        if not self.root.winfo_ismapped():
            self.skipTest("无法映射根窗口（无图形会话）")

        try:
            dialog = dialogs.ModalDialog(self.root, "提示", "内容")
            try:
                self.assertEqual(str(dialog.tl.transient()), str(self.root))
            finally:
                dialog.tl.destroy()
        finally:
            self.root.withdraw()

    # --------------------------------------------- 嵌套弹窗的抓取归还（审计 C2）
    def test_nested_dialog_returns_grab_to_outer_window(self):
        """审计 C2：内层弹窗关闭后必须把 grab 还给外层，否则外层失去模态。

        坏实现（不记 prev）下 Tk 会把抓取直接归零（实测 ``grab_current()`` 变
        ``None``）：外层弹窗还留在屏幕上，用户却能点主窗口，甚至在它底下另起一轮
        操作。真实触发路径：执行中点窗口 X 弹出确认框（A），周期性 ``after`` 在 A
        的嵌套事件循环里跑完队列又弹出结果框（B），B 关闭后 A 不再模态。
        """
        self._map_root_or_skip()
        outer = tk.Toplevel(self.root)
        self.addCleanup(self._destroy_quietly, outer)
        outer.deiconify()
        outer.update_idletasks()
        try:
            outer.grab_set()
        except tk.TclError:
            self.skipTest("无图形会话：无法建立 grab")

        try:
            self.assertEqual(self.root.grab_current(), outer, "前置条件：外层应持有抓取")

            inner = dialogs.ModalDialog(self.root, "内层", "内容")
            inner.tl.after(80, lambda: inner._finish(None))
            self.assertIsNone(inner.show())

            self.assertEqual(
                self.root.grab_current(), outer,
                "内层弹窗关闭后抓取没有还给外层，外层已失去模态",
            )
        finally:
            self.root.withdraw()

    def test_grab_holder_destroyed_before_return_does_not_raise(self):
        """反向护栏（审计 C2）：抢抓取的窗口在归还前已被销毁时不得抛异常。"""
        self._map_root_or_skip()
        stale = tk.Toplevel(self.root)
        stale.deiconify()
        stale.update_idletasks()
        try:
            stale.grab_set()
        except tk.TclError:
            self.skipTest("无图形会话：无法建立 grab")

        inner = dialogs.ModalDialog(self.root, "内层", "内容")

        def close_after_destroying_holder():
            stale.destroy()      # 持有者先没了
            inner._finish(None)

        inner.tl.after(80, close_after_destroying_holder)
        try:
            # 不抛异常即通过（winfo_exists() 为假时必须跳过归还）
            self.assertIsNone(inner.show())
        finally:
            self.root.withdraw()

    # -------------------------------------------------- 图标工厂缓存（审计 C12）
    def test_icon_factory_is_cached_per_root(self):
        """审计 C12：同一根窗口内不得每个弹窗都新建工厂（那等于每次重渲染 30–40ms）。"""
        first = dialogs.ModalDialog(self.root, "甲", "内容")
        second = dialogs.ModalDialog(self.root, "乙", "内容")
        other_kind = dialogs.ModalDialog(self.root, "丙", "内容", kind="error")
        try:
            self.assertIs(
                dialogs._icon_factory(self.root), dialogs._icon_factory(first.tl),
                "同一根窗口内应当复用同一个图标工厂",
            )
            self.assertIs(
                first._keep_icon, second._keep_icon,
                "同 kind、同尺寸的图标应当复用同一张 PhotoImage",
            )
            self.assertIsNot(
                first._keep_icon, other_kind._keep_icon,
                "不同 kind 不得串图（缓存键必须含 kind）",
            )
        finally:
            for dialog in (first, second, other_kind):
                dialog.tl.destroy()


if __name__ == "__main__":
    unittest.main()
