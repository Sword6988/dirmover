"""入口层（``run.py``）与系统集成层（``shell.py``）的关键纯逻辑回归。

补齐审计报告 §7 测试盲区的第 1、2 项——这两块原先**完全没有单元测试**，
一旦被改动（参数、退出码、CSV 解析、返回码判定）就没有任何自动防线。

覆盖策略：只测**可离线、可确定**的纯逻辑与分支判定；所有真实系统调用
（注册表、``MessageBoxW``、``taskkill``、``SetFileAttributesW``、``ShellExecuteW``）
一律 mock —— 与仓库既有约定一致：**不触碰真实注册表、真实进程与用户文件**。

运行：
    <python> -m unittest tests.test_entrypoints -v
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import logging
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import run  # noqa: E402
from docmover import logging_setup, registry, shell  # noqa: E402
from docmover.constants import (  # noqa: E402
    FOLDERS,
    RESTART_EXPLORER_TIMEOUT,
    RESTART_RELAUNCH_WAIT,
    RESTART_TERM_WAIT,
    WINLOGON_SUBKEY,
)
from docmover.registry import (  # noqa: E402
    REG_EXPAND_SZ,
    REG_SZ,
    MemoryBackend,
    RegistryStore,
)


# ============================================================ run.py：控制台输出

class TestConsoleOutput(unittest.TestCase):
    """审计 P2-2：``--windowed`` 打包后 stdout 为 None，输出必须落到文件而不是无声蒸发。"""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        # 审计 C4：run.py 的输出缓冲/落盘状态是**模块级**的，用例之间必须隔离，
        # 否则一个用例留下的缓冲会让下一个用例的提示内容（甚至弹框与否）错位。
        run._console_tail.clear()
        run._CONSOLE_LOG_OK = True
        run._CONSOLE_NOTIFY_DONE = False
        self.addCleanup(run._console_tail.clear)
        self.addCleanup(setattr, run, "_CONSOLE_LOG_OK", True)
        self.addCleanup(setattr, run, "_CONSOLE_NOTIFY_DONE", False)

    def test_display_width_counts_cjk_as_two(self):
        self.assertEqual(run._display_width("abc"), 3)
        self.assertEqual(run._display_width("文件夹"), 6)
        self.assertEqual(run._display_width("A文"), 3)

    def test_pad_never_shrinks_below_text(self):
        self.assertEqual(run._pad("文", 4), "文  ")
        # 文本已超宽时不得截断，也不得产生负数量的空格
        self.assertEqual(run._pad("文件", 2), "文件")

    def test_out_uses_stdout_when_available(self):
        buf = io.StringIO()
        with mock.patch.object(run.sys, "stdout", buf), \
             mock.patch.object(run, "_notify_no_console") as notify:
            run._out("plain")
        self.assertEqual(buf.getvalue(), "plain\n")
        notify.assert_not_called()

    def test_out_falls_back_to_file_when_no_stdout(self):
        """无控制台时输出落盘；提示**延后到 main 收尾**（那时才摘得出末尾若干行）。

        坏实现（在第一条输出就弹框，或干脆不落盘）下，用户要么只看到一行、
        要么什么也看不到。
        """
        with mock.patch.object(run, "app_dir", return_value=Path(self.tmp)), \
             mock.patch.object(run, "_notify_no_console") as notify, \
             mock.patch.object(run.sys, "stdout", None):
            run._out("hello 世界")
        notify.assert_not_called()
        content = (Path(self.tmp) / "logs" / "console.log").read_text(encoding="utf-8")
        self.assertIn("hello 世界", content)

    def test_flush_console_notice_is_silent_without_output(self):
        """收尾提示只在"确实无控制台且真的有过输出"时触发。

        ``--help``/``--version`` 由 argparse 直接写 stdout，不经过 ``_out``，
        不该因此弹一个内容为空的框。
        """
        with mock.patch.object(run, "_notify_no_console") as notify:
            run._flush_console_notice()
        notify.assert_not_called()

    def test_flush_console_notice_fires_when_there_is_output(self):
        """有输出时收尾提示才会触发，且缓冲区里留着末尾若干行。

        "只弹一次"由 ``_notify_no_console`` 内的 ``_CONSOLE_NOTIFY_DONE`` 保证
        （见 test_notify_fires_exactly_once），这里只钉住触发条件与摘要内容。
        """
        with mock.patch.object(run, "app_dir", return_value=Path(self.tmp)), \
             mock.patch.object(run, "_notify_no_console") as notify, \
             mock.patch.object(run.sys, "stdout", None):
            run._out("第一行")
            run._out("最后一行")
            run._flush_console_notice()
        notify.assert_called_once()
        self.assertEqual(list(run._console_tail), ["第一行", "最后一行"])

    def test_main_flushes_notice_once_at_end(self):
        """审计 C4：提示统一在 ``main`` 收尾触发一次，且带上该次运行的输出。"""
        with mock.patch.object(run, "app_dir", return_value=Path(self.tmp)), \
             mock.patch.object(run, "_force_utf8_console"), \
             mock.patch.object(run, "_cmd_scan",
                               side_effect=lambda: (run._out("扫描结果行"), 0)[1]), \
             mock.patch.object(run, "_notify_no_console") as notify, \
             mock.patch.object(run.sys, "stdout", None):
            self.assertEqual(run.main(["--scan"]), 0)
        notify.assert_called_once()
        self.assertEqual(list(run._console_tail), ["扫描结果行"])

    def test_console_tail_is_bounded(self):
        """缓冲区必须封顶，否则 --scan 的长输出会全量留在内存里等着拼进弹窗。"""
        with mock.patch.object(run, "app_dir", return_value=Path(self.tmp)), \
             mock.patch.object(run.sys, "stdout", None):
            for index in range(run._CONSOLE_TAIL_LINES * 3):
                run._out(f"行{index}")
        self.assertLessEqual(len(run._console_tail), run._CONSOLE_TAIL_LINES)
        self.assertEqual(run._console_tail[-1], f"行{run._CONSOLE_TAIL_LINES * 3 - 1}")

    def test_no_console_message_omits_written_claim_when_file_fails(self):
        """审计 C4：日志文件写不进去时不得声称"已写入日志文件"，要把结果直接贴出来。

        坏实现（无条件写"完整结果已写入日志文件：<路径>"）会让装在 Program Files
        下（无写权限）的用户按提示去找一个并不存在的文件——排障指引指向空处。
        """
        fake_ctypes = mock.MagicMock()
        with mock.patch.object(run, "ctypes", fake_ctypes), \
             mock.patch.object(run, "app_dir", return_value=Path(self.tmp)), \
             mock.patch.object(run.sys, "stdout", None), \
             mock.patch.object(Path, "open", side_effect=OSError("denied")):
            run._out("第一行")
            run._out("第二行")
            run._notify_no_console()

        self.assertFalse(run._CONSOLE_LOG_OK, "落盘失败没有被记录")
        body = fake_ctypes.windll.user32.MessageBoxW.call_args[0][1]
        self.assertNotIn("已写入日志文件", body)
        self.assertIn("无法写入", body)
        self.assertIn("第二行", body, "降级输出里必须带上结果本身")

    def test_log_console_output_survives_unwritable_target(self):
        """落盘失败也必须静默吞掉——输出通道本身不能在报错时二次伤害。"""
        with mock.patch.object(run, "app_dir", return_value=Path(self.tmp)), \
             mock.patch.object(Path, "open", side_effect=OSError("denied")):
            run._log_console_output("x")     # 不抛即通过
        self.assertFalse(run._CONSOLE_LOG_OK)

    def test_notify_fires_exactly_once(self):
        """一次性提示：连续多次无控制台输出，只弹一次，避免刷屏。"""
        self.addCleanup(setattr, run, "_CONSOLE_NOTIFY_DONE", False)
        run._CONSOLE_NOTIFY_DONE = False

        fake_ctypes = mock.MagicMock()
        with mock.patch.object(run, "ctypes", fake_ctypes), \
             mock.patch.object(run, "app_dir", return_value=Path(self.tmp)):
            run._notify_no_console()
            run._notify_no_console()
        fake_ctypes.windll.user32.MessageBoxW.assert_called_once()


# ============================================================ run.py：CLI 路由

class TestCliRouting(unittest.TestCase):

    def test_scan_and_dry_run_are_mutually_exclusive(self):
        parser = run.build_parser()
        with self.assertRaises(SystemExit):
            parser.parse_args(["--scan", "--dry-run"])

    def test_scan_and_gui_are_mutually_exclusive(self):
        parser = run.build_parser()
        with self.assertRaises(SystemExit):
            parser.parse_args(["--scan", "--gui"])

    def test_version_exits_zero(self):
        with mock.patch.object(run, "_force_utf8_console"), \
             mock.patch.object(run.sys, "stdout", io.StringIO()), \
             self.assertRaises(SystemExit) as caught:
            run.main(["--version"])
        self.assertEqual(caught.exception.code, 0)

    def test_help_exits_zero(self):
        with mock.patch.object(run, "_force_utf8_console"), \
             mock.patch.object(run.sys, "stdout", io.StringIO()), \
             self.assertRaises(SystemExit) as caught:
            run.main(["--help"])
        self.assertEqual(caught.exception.code, 0)

    def test_main_routes_scan(self):
        with mock.patch.object(run, "_force_utf8_console"), \
             mock.patch.object(run, "_cmd_scan", return_value=0) as handler:
            self.assertEqual(run.main(["--scan"]), 0)
        handler.assert_called_once()

    def test_main_routes_dry_run(self):
        with mock.patch.object(run, "_force_utf8_console"), \
             mock.patch.object(run, "_cmd_dry_run", return_value=0) as handler:
            self.assertEqual(run.main(["--dry-run"]), 0)
        handler.assert_called_once()

    def test_main_defaults_to_gui(self):
        with mock.patch.object(run, "_force_utf8_console"), \
             mock.patch.object(run, "_cmd_gui", return_value=0) as handler:
            self.assertEqual(run.main([]), 0)
        handler.assert_called_once()

    def test_unexpected_error_returns_code_4(self):
        """顶层兜底：任何未预期异常都要变成可诊断的退出码 4，而不是裸 traceback。"""
        with mock.patch.object(run, "_force_utf8_console"), \
             mock.patch.object(run, "build_parser", side_effect=ValueError("boom")), \
             mock.patch.object(run, "_log_console_output") as logged, \
             mock.patch.object(run, "_out"):
            self.assertEqual(run.main([]), 4)
        logged.assert_called_once()          # 明细必须落盘，不能只剩退出码

    def test_system_exit_is_not_swallowed(self):
        """argparse 的 SystemExit 必须原样放行，否则 --help/--version 的退出码会被改写。"""
        with mock.patch.object(run, "_force_utf8_console"), \
             mock.patch.object(run, "_cmd_gui", side_effect=SystemExit(7)), \
             self.assertRaises(SystemExit) as caught:
            run.main([])
        self.assertEqual(caught.exception.code, 7)

    @unittest.skipUnless(sys.platform == "win32", "仅 Windows")
    def test_check_environment_passes_on_windows(self):
        self.assertEqual(run._check_environment(), "")


# ============================================================ shell.py：PID 解析

class TestExplorerPids(unittest.TestCase):
    """审计 P2-16：tasklist 输出改 csv 解析后，必须能吃下引号内含逗号的字段。"""

    @staticmethod
    def _completed(stdout: str):
        return mock.Mock(stdout=stdout, returncode=0, stderr=b"")

    def test_parses_pids_from_csv(self):
        out = (
            '"explorer.exe","1234","Console","1","50,000 K"\n'
            '"explorer.exe","5678","Console","1","40,120 K"\n'
        )
        with mock.patch.object(shell.subprocess, "run", return_value=self._completed(out)):
            self.assertEqual(shell._explorer_pids(), [1234, 5678])

    def test_ignores_non_explorer_rows(self):
        out = (
            '"svchost.exe","111","Services","0","5,000 K"\n'
            '"explorer.exe","222","Console","1","50,000 K"\n'
        )
        with mock.patch.object(shell.subprocess, "run", return_value=self._completed(out)):
            self.assertEqual(shell._explorer_pids(), [222])

    def test_ignores_rows_with_non_numeric_pid(self):
        out = '"explorer.exe","N/A","Console","1","50,000 K"\n'
        with mock.patch.object(shell.subprocess, "run", return_value=self._completed(out)):
            self.assertEqual(shell._explorer_pids(), [])

    def test_empty_output_returns_empty_list(self):
        with mock.patch.object(shell.subprocess, "run", return_value=self._completed("")):
            self.assertEqual(shell._explorer_pids(), [])

    def test_subprocess_failure_returns_empty_list(self):
        with mock.patch.object(shell.subprocess, "run", side_effect=OSError("no tasklist")):
            self.assertEqual(shell._explorer_pids(), [])

    # --- P2-2 伴随修复的**命令层**钉子（窄口径复核补） ---------------------------------
    #
    # 之所以单列三条：此前只有 restart_explorer 层的用例（把 ``_explorer_pids`` 整个
    # mock 掉）→ 只钉住了「restart_explorer 把 user_filter 交给了 _explorer_pids」，
    # **没有**钉住「_explorer_pids 转交给了 pids_by_image」与「pids_by_image 真的把它
    # 拼进了 tasklist 的 /fi」。变异检验实证：把 pids_by_image 里的转发删掉/改错，
    # 全量 326 条**全绿**（在错的层假通过）。下面三条把每一层都钉死。

    def test_explorer_pids_forwards_filter_to_pids_by_image(self):
        """``_explorer_pids`` 必须把 ``user_filter`` 原样转交，中间层不得吞掉。"""
        with mock.patch.object(shell, "pids_by_image", return_value=[1]) as pbi:
            shell._explorer_pids(r"username eq MYPC\alice")
        pbi.assert_called_once_with("explorer.exe", r"username eq MYPC\alice")

    def test_user_filter_is_forwarded_to_tasklist(self):
        """``pids_by_image`` 必须把 ``user_filter`` 作为独立的 ``/fi`` 拼进 tasklist。"""
        captured = {}

        def _run(args, **kwargs):
            captured["args"] = list(args)
            return self._completed('"explorer.exe","4242","Console","1","50,000 K"\n')

        with mock.patch.object(shell.subprocess, "run", side_effect=_run):
            self.assertEqual(
                shell.pids_by_image("explorer.exe", r"username eq MYPC\alice"), [4242]
            )
        args = captured["args"]
        self.assertEqual(args.count("/fi"), 2, f"应有 imagename + username 两个 /fi：{args}")
        self.assertIn(r"username eq MYPC\alice", args)
        # 过滤值必须**紧跟**其 ``/fi``（两元素形态，不是拼好的一整串）
        self.assertEqual(args[args.index(r"username eq MYPC\alice") - 1], "/fi")

    def test_no_user_filter_leaves_single_imagename_filter(self):
        """``user_filter=None`` 时**不得**多加用户过滤（``run.py`` 单实例校验要看全机）。"""
        captured = {}

        def _run(args, **kwargs):
            captured["args"] = list(args)
            return self._completed("")

        with mock.patch.object(shell.subprocess, "run", side_effect=_run):
            shell.pids_by_image("DirMover.exe")
        args = captured["args"]
        self.assertEqual(args.count("/fi"), 1, f"只应有 imagename 一个 /fi：{args}")
        self.assertIn("imagename eq DirMover.exe", args)
        self.assertFalse(any("username eq" in a for a in args))


# ============================================================ shell.py：重启分支

class TestRestartExplorer(unittest.TestCase):
    """审计 P2-9：taskkill 的返回码必须参与判定，不能无论成败都往下走。"""

    def test_dry_run_short_circuits(self):
        ok, message = shell.restart_explorer(dry_run=True)
        self.assertTrue(ok)
        self.assertIn("试运行", message)

    def test_skipped_when_autorestart_disabled(self):
        with mock.patch.object(shell, "is_autorestart_shell_enabled", return_value=False):
            ok, message = shell.restart_explorer()
        self.assertFalse(ok)
        self.assertIn("AutoRestartShell", message)

    def test_no_explorer_running(self):
        with mock.patch.object(shell, "is_autorestart_shell_enabled", return_value=True), \
             mock.patch.object(shell, "_explorer_pids", return_value=[]):
            ok, message = shell.restart_explorer()
        self.assertFalse(ok)
        self.assertIn("未检测到", message)

    def test_taskkill_failure_with_process_still_alive_is_reported(self):
        completed = mock.Mock(returncode=5, stderr=b"Access is denied.")
        # 第一次（重启前）有进程；taskkill 之后进程仍在 → 必须如实报失败
        with mock.patch.object(shell, "is_autorestart_shell_enabled", return_value=True), \
             mock.patch.object(shell, "_explorer_pids", side_effect=[[4242], [4242]]), \
             mock.patch.object(shell.subprocess, "run", return_value=completed), \
             mock.patch.object(shell.time, "sleep"):
            ok, message = shell.restart_explorer()
        self.assertFalse(ok)
        self.assertIn("未能结束", message)


# ============================================================ shell.py：AutoRestartShell

class TestAutorestartFlag(unittest.TestCase):
    """审计 P2-2：``AutoRestartShell`` 的真值位于 **HKLM**，判定必须读那里。

    坏实现会让本用例变红：把 ``shell.is_autorestart_shell_enabled`` 里的
    ``backend.read_value_hklm`` 换回 ``read_value``（HKCU）——
    ``test_enabled_when_hklm_value_is_one`` 与 ``test_hkcu_value_is_ignored``
    会立刻失败。
    """

    @staticmethod
    def _store(value, *, hklm=True):
        seed = {} if value is None else {WINLOGON_SUBKEY: {"AutoRestartShell": (value, 4)}}
        backend = MemoryBackend(hklm=seed) if hklm else MemoryBackend(seed)
        return RegistryStore(backend)

    def test_enabled_when_hklm_value_is_one(self):
        self.assertTrue(shell.is_autorestart_shell_enabled(self._store(1)))

    def test_disabled_when_hklm_value_is_zero(self):
        """管理员显式关掉自动重启（=0）→ 尊重该设置，不结束 explorer。"""
        self.assertFalse(shell.is_autorestart_shell_enabled(self._store(0)))

    def test_enabled_when_hklm_value_absent_uses_windows_default(self):
        """值不存在 = Windows 默认 1（MS 文档 Range 0|1 / Default 1）→ 启用。

        缺省 ≠ 关闭：若按未启用处理，界面上会告诉用户"系统未启用 shell 自动重启"，
        而系统其实会自重启——那正是本节要消灭的**不实口径**。
        """
        self.assertTrue(shell.is_autorestart_shell_enabled(self._store(None)))

    def test_disabled_when_value_not_a_number(self):
        self.assertFalse(shell.is_autorestart_shell_enabled(self._store("yes")))

    def test_hkcu_value_is_ignored(self):
        """``AutoRestartShell=0`` 只放在 **HKCU**（旧口径读的位置）不得生效。

        这条钉死根因：HKCU 的 Winlogon 子键里没有这个值，判定必须看 HKLM。
        实现若退回读 HKCU，本用例会得到 ``False`` 而失败。
        """
        self.assertTrue(shell.is_autorestart_shell_enabled(self._store(0, hklm=False)))

    def test_read_failure_is_fail_closed(self):
        """读 HKLM 失败（权限 / 句柄 / 损坏）→ 按未启用处理，绝不冒险结束 explorer。"""

        class _Boom(MemoryBackend):
            def read_value_hklm(self, subkey, name):
                raise OSError("拒绝访问")

        with mock.patch.object(shell._log, "warning"):
            self.assertFalse(shell.is_autorestart_shell_enabled(RegistryStore(_Boom())))


# =============================================== registry.py：hive 路由（审计 P2-2）

class TestWinRegBackendHiveRouting(unittest.TestCase):
    """``read_value`` 走 HKCU、``read_value_hklm`` 走 HKLM——用 mock 断言传入的根键。

    mock 掉 ``winreg.OpenKey`` 后**只断言"根键是哪一个"**，不读写真实注册表，
    与仓库「不触碰真实注册表」的约定一致。坏实现会让本用例变红：把
    ``read_value_hklm`` 的根键退回 ``HKEY_CURRENT_USER``（P2-2 的原缺陷）。
    """

    def setUp(self):
        if registry.winreg is None:
            self.skipTest("非 Windows 平台（winreg 不可用）")
        self.backend = registry.WinRegBackend()

    def _root_used_by(self, call):
        fake_key = mock.MagicMock()
        with mock.patch.object(
            registry.winreg, "OpenKey", return_value=fake_key
        ) as open_key, mock.patch.object(
            registry.winreg, "QueryValueEx", return_value=(1, 4)
        ):
            call()
        return open_key.call_args[0][0]

    def test_read_value_uses_hkcu_root(self):
        root = self._root_used_by(lambda: self.backend.read_value("SUB", "V"))
        self.assertEqual(root, registry.winreg.HKEY_CURRENT_USER)

    def test_read_value_hklm_uses_hklm_root(self):
        root = self._root_used_by(lambda: self.backend.read_value_hklm("SUB", "V"))
        self.assertEqual(root, registry.winreg.HKEY_LOCAL_MACHINE)


# ============================================================ shell.py：其余纯逻辑

class TestShellMisc(unittest.TestCase):

    def test_release_name_mapping(self):
        self.assertEqual(shell._release_name(11, 22631), "11")
        self.assertEqual(shell._release_name(10, 22000), "11")   # 内核 10 但 build ≥22000
        self.assertEqual(shell._release_name(10, 19045), "10")
        self.assertEqual(shell._release_name(6, 7601), "8.1/7")
        self.assertEqual(shell._release_name(5, 0), "5")

    def test_is_admin_returns_bool_not_exception(self):
        with mock.patch.object(shell, "ctypes") as fake:
            fake.windll.shell32.IsUserAnAdmin.return_value = 1
            self.assertTrue(shell.is_admin())
            fake.windll.shell32.IsUserAnAdmin.return_value = 0
            self.assertFalse(shell.is_admin())

    def test_is_admin_swallows_oserror(self):
        with mock.patch.object(shell, "ctypes") as fake:
            fake.windll.shell32.IsUserAnAdmin.side_effect = OSError("boom")
            self.assertFalse(shell.is_admin())

    def test_write_desktop_ini_rejects_empty_alias(self):
        ok, message = shell.write_desktop_ini("C:/whatever", "")
        self.assertFalse(ok)
        self.assertIn("别名", message)

    def test_write_desktop_ini_rejects_missing_dir(self):
        ok, message = shell.write_desktop_ini(str(Path(tempfile.gettempdir()) / "no_such_dir_xyz"), "名")
        self.assertFalse(ok)
        self.assertIn("不存在", message)

    def test_write_desktop_ini_writes_utf16_with_alias(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, True)
        with mock.patch.object(shell, "_set_attributes") as attrs:
            ok, message = shell.write_desktop_ini(tmp, "我的文档")
        self.assertTrue(ok)
        body = (Path(tmp) / "Desktop.ini").read_text(encoding="utf-16")
        self.assertIn("我的文档", body)
        self.assertIn("[.ShellClassInfo]", body)
        # Desktop.ini 必须被标为隐藏 + 系统，否则资源管理器不认
        attrs.assert_called()

    def test_write_desktop_ini_is_idempotent(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, True)
        with mock.patch.object(shell, "_set_attributes"):
            shell.write_desktop_ini(tmp, "我的文档")
            ok, message = shell.write_desktop_ini(tmp, "我的文档")
        self.assertTrue(ok)
        self.assertIn("已是", message)


# ============================================================ logging_setup：文件日志状态

class TestFileLoggingState(unittest.TestCase):
    """审计 C4：``setup_logging`` 必须把"文件处理器到底建起来没有"外露。

    坏实现（静默吞掉 OSError、只记 ``_CONFIGURED``）会让调用方在无写权限时仍然
    告诉用户"完整结果已写入日志文件：<路径>"——排障指引指向一个不存在的文件。
    """

    def setUp(self):
        self.ls = logging_setup
        self._saved_handler = logging_setup._FILE_HANDLER
        self._saved_ok = logging_setup._FILE_LOGGING_OK
        self._saved_configured = logging_setup._CONFIGURED
        self.tmp = tempfile.mkdtemp()
        # 注册顺序即清理的逆序：先关句柄（_restore），再删目录
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.addCleanup(self._restore)

    def _restore(self):
        """把 logger 上的文件处理器摘掉并关闭（否则 Windows 下临时目录删不掉）。"""
        logger = logging.getLogger("DirMover")
        for handler in list(logger.handlers):
            if isinstance(handler, logging.FileHandler):
                logger.removeHandler(handler)
                try:
                    handler.close()
                except Exception:
                    pass
        if self._saved_handler is not None:
            logger.addHandler(self._saved_handler)
        # 恢复模块级状态，避免影响后续用例（含界面冒烟）
        self.ls._FILE_HANDLER = self._saved_handler
        self.ls._FILE_LOGGING_OK = self._saved_ok
        self.ls._CONFIGURED = self._saved_configured

    def test_unavailable_when_directory_cannot_be_created(self):
        with mock.patch.object(self.ls.Path, "mkdir", side_effect=OSError("denied")):
            self.ls.setup_logging(Path(self.tmp))
        self.assertFalse(self.ls.file_logging_available())

    def test_available_when_directory_is_writable(self):
        self.ls.setup_logging(Path(self.tmp))
        self.assertTrue(self.ls.file_logging_available())


# ============================================================ tools/smoke_exe.py

class TestSmokeExeTool(unittest.TestCase):
    """审计 C11：产物带 ``requireAdministrator`` 清单，非提权会话里 ``Popen`` 会抛
    ``OSError``（WinError 740）。脚本必须给可操作指引并返回 1，而不是甩 traceback
    ——那会让人误以为脚本本身坏了。
    """

    @staticmethod
    def _load():
        path = Path(__file__).resolve().parent.parent / "tools" / "smoke_exe.py"
        spec = importlib.util.spec_from_file_location("smoke_exe_under_test", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def test_popen_elevation_failure_is_reported_not_raised(self):
        """用例必须**与机器上是否正跑着 exe 无关**（hermetic）。

        本用例只关心「Popen 抛 WinError 740 时的文案与退出码」，而 ``main()``
        的**第一步**是残留实例预检——若不打桩 ``_docmover_pids``，本机只要有
        一个残留的 DirMover 实例，就会在进到 Popen 之前先返回，断言随之失败
        （实测踩到：开发机上一个早先冒烟留下的实例把它弄红）。故这里显式打桩成
        「无残留实例」，把被测路径钉死在提权失败分支上。
        预检本身的覆盖见 ``test_pre_existing_instance_is_refused``。
        """
        smoke = self._load()
        out = io.StringIO()
        with mock.patch.object(smoke, "EXE") as exe, \
             mock.patch.object(smoke, "enable_dpi"), \
             mock.patch.object(smoke, "_docmover_pids", return_value=set()), \
             mock.patch.object(smoke.subprocess, "Popen",
                               side_effect=OSError(740, "请求的操作需要提升")), \
             contextlib.redirect_stdout(out):
            exe.exists.return_value = True
            exe.name = "DirMover.exe"
            code = smoke.main()

        self.assertEqual(code, 1)
        text = out.getvalue()
        self.assertIn("[FAIL]", text)
        self.assertIn("740", text)

    def test_missing_artifact_still_reports_fail(self):
        smoke = self._load()
        out = io.StringIO()
        with mock.patch.object(smoke, "EXE") as exe, \
             mock.patch.object(smoke, "enable_dpi"), \
             contextlib.redirect_stdout(out):
            exe.exists.return_value = False
            code = smoke.main()
        self.assertEqual(code, 1)
        self.assertIn("未找到", out.getvalue())

    def test_pre_existing_instance_is_refused(self):
        """F4 加固①的回归：残留实例必须**先于启动**被拒（此前零测试覆盖）。

        没有这道预检时，冒烟会按标题在整个桌面搜窗口，机器上任何一个残留的
        DirMover 实例都可能被当成「本次启动的窗口」，造成**假通过**。这里打桩
        ``_docmover_pids`` 假装机器上已有实例，断言：不进 Popen、退出码 1、
        文案指明是「先于本次启动就存在」并给出关闭指引。
        """
        smoke = self._load()
        out = io.StringIO()
        with mock.patch.object(smoke, "EXE") as exe, \
             mock.patch.object(smoke, "enable_dpi"), \
             mock.patch.object(smoke, "_docmover_pids",
                               return_value={4242, 4243}), \
             mock.patch.object(smoke.subprocess, "Popen") as popen, \
             contextlib.redirect_stdout(out):
            exe.exists.return_value = True
            exe.name = "DirMover.exe"
            code = smoke.main()

        self.assertEqual(code, 1)
        text = out.getvalue()
        self.assertIn("先于本次启动就存在", text)
        self.assertIn("4242", text)
        self.assertIn("taskkill", text)
        popen.assert_not_called()


class TestAlreadyRunningPopup(unittest.TestCase):
    """第二个实例提示窗的图标语义（第十二批，用户反馈）。

    「程序已在运行。请勿重复打开。」是**拦下操作**，不是纯提示——应走
    ``show_warning``（棕色「!」，与主界面「无法开始」「恢复受阻」同类），
    而不是 ``show_info``（蓝色「i」）。这里通过打桩弹窗助手来钉住图标语义，
    避免真的弹模态框（``show_*`` 内部有 ``wait_window``，不打桩会卡住测试）。
    """

    def test_uses_warning_icon_not_info(self):
        import docmover.ui.dialogs as dialogs

        warned = mock.Mock()
        infoed = mock.Mock()
        with mock.patch.object(dialogs, "show_warning", warned), \
             mock.patch.object(dialogs, "show_info", infoed), \
             mock.patch.object(run, "_app_icon_path", return_value=None):
            code = run._notify_already_running()

        self.assertEqual(code, 0)
        self.assertEqual(warned.call_count, 1, "应使用 show_warning（棕色感叹号）")
        self.assertEqual(infoed.call_count, 0, "不得再用 show_info（蓝色 i）")
        message = warned.call_args[0][2]
        self.assertIn("已在运行", message)
        self.assertIn("请勿重复打开", message)

    def test_fallback_native_box_is_warning_too(self):
        """自绘弹窗失败时退回的原生提示，同样应是 warning 而非 info。"""
        import docmover.ui.dialogs as dialogs

        warned = mock.Mock()
        infoed = mock.Mock()
        native_warning = mock.Mock()
        native_info = mock.Mock()
        with mock.patch.object(dialogs, "show_warning", warned), \
             mock.patch.object(dialogs, "show_info", infoed), \
             mock.patch.object(run, "_app_icon_path", return_value=None), \
             mock.patch("tkinter.messagebox.showwarning", native_warning), \
             mock.patch("tkinter.messagebox.showinfo", native_info):
            # 让自绘弹窗抛错，逼出原生兜底分支
            warned.side_effect = RuntimeError("boom")
            code = run._notify_already_running()

        self.assertEqual(code, 0)
        self.assertEqual(native_warning.call_count, 1, "兜底也应是 showwarning")
        self.assertEqual(native_info.call_count, 0, "兜底不得再用 showinfo")


# ============================================================ 审计 F6：单实例抢占

class TestSingleInstanceMutex(unittest.TestCase):
    """审计 F6：同名互斥体可以被**同会话普通进程**抢占（DoS），不能只看"已存在"。

    ``Local\\`` 按登录会话隔离但跨完整性级别可见，NULL DACL 又让同用户可以打开。
    一个非提权进程先建同名互斥体就能让本程序永远停在"程序已在运行"，故必须校验
    持有者是不是一个**活着的 DirMover**。
    """

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.owner_file = self.tmp / "single_instance.pid"
        self.addCleanup(setattr, run, "_SINGLE_INSTANCE_HANDLE", run._SINGLE_INSTANCE_HANDLE)
        self.addCleanup(setattr, run, "_KERNEL32", run._KERNEL32)
        run._SINGLE_INSTANCE_HANDLE = None

    def _acquire(self, *, owner, pids, already_exists=True):
        """在受控条件下跑一遍 :func:`run._acquire_single_instance`。"""
        if owner is None:
            self.owner_file.unlink(missing_ok=True)
        else:
            self.owner_file.write_text(str(owner), encoding="utf-8")

        k32 = mock.Mock()
        k32.CreateMutexW.return_value = 4242
        with mock.patch.object(run, "_mutex_owner_path", return_value=self.owner_file), \
             mock.patch.object(run, "_kernel32", return_value=k32), \
             mock.patch.object(run, "_docmover_pids", return_value=set(pids)), \
             mock.patch.object(run.ctypes, "get_last_error",
                               return_value=run._ERROR_ALREADY_EXISTS if already_exists else 0):
            return run._acquire_single_instance()

    def test_first_instance_records_its_pid(self):
        self.assertTrue(self._acquire(owner=None, pids=[], already_exists=False))
        self.assertEqual(
            int(self.owner_file.read_text(encoding="utf-8").strip()), os.getpid()
        )

    def test_second_real_instance_is_still_blocked(self):
        """正常的「重复双击 → 提示已运行」行为不得被本修复破坏。"""
        other = os.getpid() + 9999
        self.assertFalse(self._acquire(owner=other, pids=[other]))

    def test_dead_owner_pid_is_treated_as_preemption(self):
        """持有者进程已不在 → 不是"程序已在运行"，允许启动。"""
        self.assertTrue(self._acquire(owner=os.getpid() + 9999, pids=[12345]))

    def test_foreign_owner_pid_is_treated_as_preemption(self):
        """持有者是别的程序（典型 DoS 场景）→ 允许启动。"""
        self.assertTrue(self._acquire(owner=4321, pids=[os.getpid()]))

    def test_unverifiable_owner_allows_startup(self):
        """校验无法完成（PID 文件缺失）→ 允许启动，宁可放过重复启动也不误报。"""
        self.assertTrue(self._acquire(owner=None, pids=[os.getpid() + 7]))

    def test_release_clears_owner_file(self):
        """审计 C5 × F6：提权重启前必须连 PID 文件一起清掉。

        否则子进程拿到互斥体后读到「父进程 PID 仍存活且镜像名相同」→ 判定"已在
        运行"，把 C5 修好的提权重启又堵回去。
        """
        self.owner_file.write_text("123", encoding="utf-8")
        run._SINGLE_INSTANCE_HANDLE = 4242
        k32 = mock.Mock()
        with mock.patch.object(run, "_mutex_owner_path", return_value=self.owner_file), \
             mock.patch.object(run, "_kernel32", return_value=k32):
            run._release_single_instance()

        self.assertFalse(self.owner_file.exists(), "PID 文件没有随互斥体一起释放")
        k32.CloseHandle.assert_called_once_with(4242)

    def test_normal_gui_exit_clears_owner_file(self):
        """审计 C1：**正常关窗退出**（未走提权重启）也必须清掉 PID 文件。

        坏实现（只在 ``_release_single_instance`` 里清理、正常运行路径不调用）会让
        ``single_instance.pid`` 永久留在 ``%LOCALAPPDATA%\\DirMover\\``。这里通过
        **真实调用** :func:`run._cmd_gui`（只打桩 Tk / MainWindow / 环境探测）走到
        正常退出分支，断言 PID 文件被清除——不是只断言 ``_clear_mutex_owner`` 能被调。
        """
        self.owner_file.write_text("123", encoding="utf-8")
        run._SINGLE_INSTANCE_HANDLE = 4242
        fake_root = mock.Mock()
        with mock.patch.object(run, "_mutex_owner_path", return_value=self.owner_file), \
             mock.patch.object(run, "_kernel32", return_value=mock.Mock()), \
             mock.patch.object(run, "_acquire_single_instance", return_value=True), \
             mock.patch.object(run, "_check_environment", return_value=""), \
             mock.patch.object(run, "_enable_dpi_awareness"), \
             mock.patch.object(run, "AppConfig", return_value=mock.Mock()), \
             mock.patch.object(run.logging_setup, "setup_logging",
                               return_value=mock.Mock()), \
             mock.patch("tkinter.Tk", return_value=fake_root), \
             mock.patch("docmover.ui.main_window.MainWindow", return_value=mock.Mock()):
            code = run._cmd_gui()

        self.assertEqual(code, 0)
        self.assertFalse(self.owner_file.exists(), "正常退出没有清理单实例 PID 文件")
        fake_root.mainloop.assert_called_once()


# ============================================================ 审计 B1：--scan 消费意图日志错误

class TestScanSurfacesJournalReadError(unittest.TestCase):
    """审计 B1：``run.py --scan`` 必须消费 ``IntentJournal`` 的读取错误并给出可见提示。

    坏实现（``pending()`` 后不取 ``consume_read_error()``）下，损坏的 ``intent.json``
    会让 ``pending()`` 静默返回 ``[]``，用户与日志都收不到「上次可能有半完成项」。
    """

    def test_corrupt_intent_journal_is_surfaced(self):
        from docmover.transfer import IntentJournal

        # 项目惯例：临时目录优先落在 D:\tmp（避免在系统盘 C: 上造目录）；CI 上
        # 没有 D: 盘时退回系统临时目录，保证可移植。
        base = "D:/tmp" if os.path.isdir("D:/tmp") else None
        tmp = Path(tempfile.mkdtemp(dir=base))
        self.addCleanup(shutil.rmtree, tmp, True)
        backup = tmp / "backups"
        backup.mkdir()
        # 半截 JSON：正是「进程被硬杀、非原子直写」会留下的形态
        (backup / "intent.json").write_text('{"items": {', encoding="utf-8")

        engine = mock.Mock()
        engine.resolve_current.return_value = r"D:\UserData\me\Documents"
        store = mock.Mock()
        store.consume_read_error.return_value = None
        store.enum_values.return_value = {}
        shell_mock = mock.Mock()
        shell_mock.detect_windows_version.return_value = ("Windows 11", 10)
        shell_mock.is_admin.return_value = False

        lines = []
        IntentJournal.consume_read_error()   # 清掉可能残留的全局错误
        with mock.patch.object(run, "app_dir", return_value=tmp), \
             mock.patch.object(run, "RegistryStore", return_value=store), \
             mock.patch.object(run, "TransferEngine", return_value=engine), \
             mock.patch.object(run, "shell", shell_mock), \
             mock.patch.object(run.fsops, "list_fixed_drives", return_value=[]), \
             mock.patch.object(run, "_out",
                               side_effect=lambda text="": lines.append(text)):
            code = run._cmd_scan()

        self.assertEqual(code, 0)
        joined = "\n".join(lines)
        self.assertIn("上次事务日志不可用", joined,
                      "损坏的意图日志没有被 --scan 暴露给用户")


# ============================================================ 审计 F7：taskkill 加固

class TestTaskkillHardening(unittest.TestCase):
    """审计 F7 加固 + **P2-2 已解除不可达**：``taskkill`` 命令面的纵深防御。

    审计 F7 当时 ``is_autorestart_shell_enabled`` 读 HKCU 的 ``AutoRestartShell``
    （真值在 HKLM），生产里恒为 False → ``taskkill`` 执行不到，加固属"已上膛、
    保险未开"。**P2-2 修好之后该分支已可达**（默认系统 HKLM=1），所以下面这些
    「用户过滤 / 参数数组 / 共用常量 / 未提权拉起」的断言不再只是预留——
    它们现在是**生产路径上唯一**的限制条件，必须保持为真。
    """

    def _run_restart(self, runner, *, pids=((4242,), ()), slept=None):
        completed = mock.Mock(stdout="", returncode=0, stderr=b"")
        runner.return_value = completed
        sleep_patch = (mock.patch.object(shell.time, "sleep", slept)
                       if slept is not None
                       else mock.patch.object(shell.time, "sleep"))
        with mock.patch.dict(os.environ, {"USERDOMAIN": "MYPC", "USERNAME": "alice"}), \
             mock.patch.object(shell, "is_autorestart_shell_enabled", return_value=True), \
             mock.patch.object(shell, "_explorer_pids", side_effect=[list(p) for p in pids]), \
             mock.patch.object(shell, "_relaunch_explorer", return_value=True), \
             sleep_patch:
            return shell.restart_explorer()

    def test_taskkill_filters_by_current_user(self):
        with mock.patch.object(shell.subprocess, "run") as runner:
            ok, message = self._run_restart(runner)
        args = runner.call_args[0][0]
        self.assertIn("/fi", args, f"taskkill 未加会话/用户过滤：{args}")
        self.assertIn(r"username eq MYPC\alice", args)
        self.assertTrue(ok, message)

    def test_explorer_listing_uses_same_user_filter_as_taskkill(self):
        """P2-2 **伴随修复**：列举 explorer 与强杀 explorer 必须用同一个用户过滤。

        坏实现：列举退回不加过滤的 ``_explorer_pids()``——在「别人也登录着（RDP /
        快速切换用户）」的机器上，"我们的 explorer 回来了没有"会被**别人的**
        explorer 干扰，于是该兜底拉起时不拉、还误报"资源管理器已重启"。本用例断言
        每次列举都带上了 ``username eq MYPC\\alice``。
        """
        completed = mock.Mock(stdout="", returncode=0, stderr=b"")
        with mock.patch.object(shell.subprocess, "run", return_value=completed), \
             mock.patch.object(shell, "is_autorestart_shell_enabled", return_value=True), \
             mock.patch.object(shell, "_explorer_pids", side_effect=[[4242], []]) as pids, \
             mock.patch.object(shell, "_relaunch_explorer", return_value=True), \
             mock.patch.object(shell.time, "sleep"), \
             mock.patch.dict(os.environ, {"USERDOMAIN": "MYPC", "USERNAME": "alice"}):
            shell.restart_explorer()
        self.assertTrue(pids.call_args_list, "没有列举过 explorer")
        for call in pids.call_args_list:
            self.assertEqual(
                call.args,
                (r"username eq MYPC\alice",),
                f"列举 explorer 未使用与 taskkill 相同的用户过滤：{call}",
            )

    def test_taskkill_uses_argument_array(self):
        """参数必须是数组而非拼接串（含空格的域/用户名不会散架）。"""
        with mock.patch.object(shell.subprocess, "run") as runner:
            self._run_restart(runner)
        args = runner.call_args[0][0]
        self.assertIsInstance(args, list)
        self.assertEqual(args.count("explorer.exe"), 1)
        # 真正的钉子：/im 与镜像名是两个独立元素（不是拼好的一整串）
        self.assertEqual(args[args.index("/im") + 1], "explorer.exe")

    def test_restart_uses_shared_constants(self):
        """A-7/B-6：超时与两段等待取自 constants，不再写死字面量。"""
        slept = mock.Mock()
        with mock.patch.object(shell.subprocess, "run") as runner:
            self._run_restart(runner, slept=slept)
        self.assertEqual(runner.call_args.kwargs.get("timeout"), RESTART_EXPLORER_TIMEOUT)
        self.assertEqual(
            [call.args[0] for call in slept.call_args_list],
            [RESTART_TERM_WAIT, RESTART_RELAUNCH_WAIT],
        )

    def test_relaunch_prefers_shellexecute_over_popen(self):
        with mock.patch.object(shell, "_relaunch_explorer_via_shell", return_value=True), \
             mock.patch.object(shell.subprocess, "Popen") as popen:
            self.assertTrue(shell._relaunch_explorer())
        popen.assert_not_called()

    def test_relaunch_falls_back_to_popen(self):
        with mock.patch.object(shell, "_relaunch_explorer_via_shell", return_value=False), \
             mock.patch.object(shell.subprocess, "Popen") as popen:
            self.assertTrue(shell._relaunch_explorer())
        popen.assert_called_once()

    def test_relaunch_reports_failure_when_both_paths_fail(self):
        with mock.patch.object(shell, "_relaunch_explorer_via_shell", return_value=False), \
             mock.patch.object(shell.subprocess, "Popen", side_effect=OSError("denied")):
            self.assertFalse(shell._relaunch_explorer())

    def test_pids_by_image_is_reusable_for_single_instance_check(self):
        """F6 复用：``pids_by_image`` 能按任意镜像名解析（不只是 explorer）。"""
        out = '"DirMover.exe","4321","Console","1","60,000 K"\n'
        completed = mock.Mock(stdout=out, returncode=0, stderr=b"")
        with mock.patch.object(shell.subprocess, "run", return_value=completed):
            self.assertEqual(shell.pids_by_image("DirMover.exe"), [4321])


# ============================================================ 审计 F10：REG_EXPAND_SZ

class TestExpandableRegType(unittest.TestCase):
    """审计 F10：值里**任意位置**含 ``%`` 不再一律写成 REG_EXPAND_SZ。

    旧实现 ``if "%" in value`` 让 ``D:\\a%USERPROFILE%b\\UserData`` 这类路径被
    系统二次展开成 ``D:\\aC:\\Users\\xxx b\\UserData`` —— 路径静默失真。
    """

    def setUp(self):
        self.store = RegistryStore(MemoryBackend())
        self.subkey = r"Software\Test"

    def test_default_profile_path_stays_expandable(self):
        regtype = self.store.set_text(self.subkey, "Personal", r"%USERPROFILE%\Documents")
        self.assertEqual(regtype, REG_EXPAND_SZ)

    def test_all_folder_defaults_stay_expandable(self):
        """正常工作路径不得被本修复破坏（``FOLDERS[*].default`` 都是 ``%VAR%\\...``）。"""
        for spec in FOLDERS:
            self.assertEqual(
                self.store.set_text(self.subkey, spec.value_name, spec.default),
                REG_EXPAND_SZ,
                f"{spec.key} 的默认路径应为可展开类型",
            )

    def test_bare_variable_is_expandable(self):
        self.assertEqual(
            self.store.set_text(self.subkey, "A", "%USERPROFILE%"), REG_EXPAND_SZ
        )

    def test_percent_in_the_middle_is_plain_sz(self):
        value = r"D:\a%USERPROFILE%b\UserData"
        self.assertEqual(self.store.set_text(self.subkey, "A", value), REG_SZ)
        # 关键：值必须原样存下，读回时不会被二次展开
        self.assertEqual(self.store.backend.read_value(self.subkey, "A")[0], value)

    def test_percent_suffix_and_partial_variable_are_plain_sz(self):
        self.assertEqual(self.store.set_text(self.subkey, "A", "100%"), REG_SZ)
        self.assertEqual(self.store.set_text(self.subkey, "B", "%FOO%bar"), REG_SZ)
        self.assertEqual(self.store.set_text(self.subkey, "C", "%FOO"), REG_SZ)

    def test_keep_expandable_false_still_forces_sz(self):
        self.assertEqual(
            self.store.set_text(self.subkey, "A", r"%USERPROFILE%\D", keep_expandable=False),
            REG_SZ,
        )


if __name__ == "__main__":
    unittest.main()
