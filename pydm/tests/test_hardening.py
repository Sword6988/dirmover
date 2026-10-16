"""加固批次（审计 P2-2/3/4/5/6/7/13/14）的针对性回归。

全部使用临时目录 + 内存注册表后端，**不触碰真实注册表与用户文件**。

运行：
    <python> -m unittest tests.test_hardening -v
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from docmover import constants, fsops  # noqa: E402
from docmover.constants import FOLDERS_BY_KEY, USER_SHELL_FOLDERS_SUBKEY  # noqa: E402
from docmover.registry import (  # noqa: E402
    MemoryBackend,
    RegistryEntry,
    RegistryReadError,
    RegistrySnapshot,
    RegistryStore,
)
from docmover.transfer import (  # noqa: E402
    JOURNAL_FILENAME,
    IntentJournal,
    PlanItem,
    TransferEngine,
    _unsafe_source_tree,
    restrict_registry_snapshot,
)


# --------------------------------------------------------------- 长路径 (P2-4)

class TestLongPath(unittest.TestCase):

    def test_short_path_untouched(self):
        """未达阈值的普通路径必须原样返回（保证既有行为/性能不变）。"""
        p = r"D:\UserData\me\Documents"
        self.assertEqual(fsops._lp(p), p)
        self.assertEqual(fsops._unlp(p), p)

    def test_prefix_added_only_when_long_and_drive_paths(self):
        long_path = "D:\\" + "a" * (fsops._LONG_PATH_THRESHOLD + 5)
        prefixed = fsops._lp(long_path)
        self.assertTrue(prefixed.startswith("\\\\?\\"))
        self.assertEqual(fsops._unlp(prefixed), os.path.abspath(long_path))

    def test_blocking_reason_uses_long_path_prefix_for_source(self):
        """审计 B-1：源路径 ≥240 字符时，判存在与否必须走 ``fsops._lp()``。

        这里断言「真正传给 ``os.path.isdir`` 的参数带 ``\\\\?\\\\`` 前缀」，而不是
        只看结果——只断言结果会依赖「宿主机有没有开长路径支持」，在开着的机器上
        即使修复被改坏也照样绿。
        """
        from docmover.transfer import PlanItem

        base = tempfile.mkdtemp()
        try:
            deep = os.path.join(base, "src")
            os.makedirs(fsops._lp(deep), exist_ok=True)
            while len(deep) < fsops._LONG_PATH_THRESHOLD + 40:
                deep = os.path.join(deep, "d" * 40)
                os.makedirs(fsops._lp(deep), exist_ok=True)

            item = PlanItem(
                spec=FOLDERS_BY_KEY["documents"],
                current=deep,
                target=os.path.join(base, "dst"),
            )
            self.assertGreaterEqual(len(deep), fsops._LONG_PATH_THRESHOLD)

            called: list = []
            real_isdir = os.path.isdir

            def recording_isdir(path):
                called.append(path)
                return real_isdir(path)

            with mock.patch.object(os.path, "isdir", side_effect=recording_isdir):
                reason = item.blocking_reason()

            self.assertIn(
                fsops._lp(deep), called,
                "超长源路径没有走 \\\\?\\ 前缀去探测（B-1 回归）",
            )
            # 真实结果也必须仍然判「存在」（修复前会被误判成"当前位置不存在"）
            self.assertNotIn("当前位置不存在", reason)
        finally:
            shutil.rmtree(fsops._lp(base), ignore_errors=True)

    def test_unc_path_becomes_unc_prefixed(self):
        long_unc = "\\\\server\\share\\" + "b" * (fsops._LONG_PATH_THRESHOLD + 5)
        prefixed = fsops._lp(long_unc)
        self.assertTrue(prefixed.startswith("\\\\?\\UNC\\"))
        self.assertEqual(fsops._unlp(prefixed), long_unc)

    def test_already_prefixed_is_idempotent(self):
        already = "\\\\?\\D:\\" + "c" * (fsops._LONG_PATH_THRESHOLD + 5)
        self.assertEqual(fsops._lp(already), already)

    def test_long_path_copy_and_verify_roundtrip(self):
        """超出 260 字符的目录树也能完整复制并校验通过（审计 P2-4）。"""
        base = tempfile.mkdtemp()
        src = os.path.join(base, "src")
        dst = os.path.join(base, "dst")
        try:
            # 用 _lp 创建超长目录（普通 API 在 >260 时会被截断/拒绝）
            deep = src
            os.makedirs(fsops._lp(deep), exist_ok=True)
            while len(deep) < fsops._LONG_PATH_THRESHOLD + 40:
                deep = os.path.join(deep, "d" * 40)
                os.makedirs(fsops._lp(deep), exist_ok=True)
            payload = os.path.join(deep, "payload.bin")
            with open(fsops._lp(payload), "wb") as handle:
                handle.write(b"LONG-PATH-DATA" * 100)

            result = fsops.copy_merge(src, dst)
            self.assertTrue(result.ok, result.errors)
            ok, detail = fsops.verify_copy(src, dst, result)
            self.assertTrue(ok, detail)

            rel = os.path.relpath(payload, src)
            # rel_path 必须是干净的相对路径（不带 \\?\ 前缀）
            self.assertNotIn("?", rel)
            target = os.path.join(dst, rel)
            self.assertTrue(os.path.isfile(fsops._lp(target)))
            with open(fsops._lp(target), "rb") as handle:
                self.assertEqual(handle.read(), b"LONG-PATH-DATA" * 100)
        finally:
            shutil.rmtree(fsops._lp(base), ignore_errors=True)

    def test_long_path_prefix_reaches_fs_api(self):
        """超长路径在**真正调用文件系统 API** 时必须带 ``\\\\?\\`` 扩展前缀。

        审计 P2-4（QA 补强）：上一条端到端用例依赖「宿主机是否已启用长路径支持」
        ——在已启用的机器上即使 ``_lp`` 被破坏（不加前缀）也会误绿。这里改为直接
        观测「复制过程中实际传给 FS API 的路径」：包住 ``builtins.open`` 记录入参，
        断言其中确有以 ``\\\\?\\`` 开头者，且前缀路径不含未归一化的 ``..`` 片段
        （``\\\\?\\`` 路径不做点号归一化，这是 P2-4 的关键约束）。
        """
        import builtins

        base = tempfile.mkdtemp()
        src = os.path.join(base, "src")
        dst = os.path.join(base, "dst")
        try:
            deep = src
            os.makedirs(fsops._lp(deep), exist_ok=True)
            while len(deep) < fsops._LONG_PATH_THRESHOLD + 40:
                deep = os.path.join(deep, "d" * 40)
                os.makedirs(fsops._lp(deep), exist_ok=True)
            payload = os.path.join(deep, "payload.bin")
            with open(fsops._lp(payload), "wb") as handle:
                handle.write(b"FS-API-PREFIX" * 50)

            recorded: list = []
            real_open = builtins.open

            def _recording_open(file, *args, **kwargs):
                recorded.append(file)
                return real_open(file, *args, **kwargs)

            with mock.patch.object(builtins, "open", side_effect=_recording_open):
                result = fsops.copy_merge(src, dst)
            self.assertTrue(result.ok, result.errors)

            prefixed = [
                p for p in recorded
                if isinstance(p, str) and p.startswith("\\\\?\\")
            ]
            self.assertTrue(
                prefixed,
                "复制过程中没有任何文件系统调用使用 \\\\?\\ 前缀（长路径未被处理）",
            )
            # 它必须确实是「超长」的那条，而不是碰巧被加了前缀的短路径
            self.assertTrue(
                any(len(p) >= fsops._LONG_PATH_THRESHOLD for p in prefixed),
                "带前缀的路径均未超长，前缀没有作用在长路径上",
            )
            # \\?\ 前缀不做归一化：不得出现未展开的 .. 片段
            for path in prefixed:
                self.assertNotIn(
                    "..", fsops._unlp(path).split("\\"),
                    f"扩展前缀路径未被归一化：{path}",
                )
        finally:
            shutil.rmtree(fsops._lp(base), ignore_errors=True)


# --------------------------------------------------- 稀疏文件 / 硬链接 (P2-5)

def _make_sparse_file(path: str, hole: int = 4 << 20, tail: bytes = b"TAIL") -> bool:
    """尽量造一个真正的稀疏文件；平台不支持时返回 False。"""
    try:
        import ctypes
        import msvcrt
        from ctypes import wintypes
    except ImportError:
        return False
    with open(path, "wb") as handle:
        os_handle = msvcrt.get_osfhandle(handle.fileno())
        returned = wintypes.DWORD(0)
        ok = ctypes.windll.kernel32.DeviceIoControl(
            wintypes.HANDLE(os_handle), 0x900C4, None, 0, None, 0,
            ctypes.byref(returned), None,
        )
        if not ok:
            return False
        handle.seek(hole)
        handle.write(tail)
    return True


def _allocated_bytes(path: str) -> int:
    """文件在磁盘上的**实际占盘**字节数（``GetCompressedFileSizeW``）。

    审计 P2-5（QA 补强）：这是区分「目标真成了稀疏文件」与「只是把全零也实体
    写进去了」的**可观察物理结果**——稀疏文件的占盘会显著小于其逻辑大小。
    """
    import ctypes

    k32 = ctypes.windll.kernel32
    k32.GetCompressedFileSizeW.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_ulong)]
    k32.GetCompressedFileSizeW.restype = ctypes.c_ulong
    high = ctypes.c_ulong(0)
    low = k32.GetCompressedFileSizeW(path, ctypes.byref(high))
    return (high.value << 32) | low


class TestSparseAndHardlink(unittest.TestCase):

    def test_sparse_file_is_copied_and_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            source_file = src / "disk.vhd"
            if not _make_sparse_file(str(source_file)):
                self.skipTest("当前环境不支持标记稀疏文件")
            self.assertTrue(
                os.stat(source_file).st_file_attributes & fsops.FILE_ATTRIBUTE_SPARSE_FILE
            )

            result = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(result.ok, result.errors)
            self.assertEqual(result.sparse_files, 1, "稀疏属性未被识别")
            # 内容（含零洞）必须逐字节一致，且校验通过
            self.assertEqual(
                (dst / "disk.vhd").read_bytes(), source_file.read_bytes()
            )
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertTrue(ok, detail)
            self.assertIn("稀疏", detail)

            # 审计 P2-5（QA 补强）：必须验证**目标本身真的成了稀疏文件**，而不是
            # 只看由「源」属性推导出的 result.sparse_files 计数——否则一旦稀疏写
            # 路径被关闭（整文件实体写入），本用例仍会误绿。
            target_file = dst / "disk.vhd"
            target_attr = getattr(os.stat(target_file), "st_file_attributes", 0)
            self.assertTrue(
                target_attr & fsops.FILE_ATTRIBUTE_SPARSE_FILE,
                f"目标未保留稀疏属性（attr=0x{target_attr:x}）",
            )
            logical = target_file.stat().st_size
            allocated = _allocated_bytes(str(target_file))
            self.assertLess(
                allocated,
                logical // 2,
                f"目标未真正成为稀疏：占盘 {allocated} / 逻辑 {logical}",
            )

    def test_hardlinked_files_are_relinked(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "a.bin").write_bytes(b"HARD-LINK-DATA" * 50)
            try:
                os.link(str(src / "a.bin"), str(src / "b.bin"))
            except OSError as exc:
                self.skipTest(f"当前环境不支持硬链接：{exc}")

            result = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(result.ok, result.errors)
            self.assertEqual(result.hardlinked_files, 1, "硬链接未被重建")
            # 目标侧两个文件应共享同一 inode（未各存一份）
            ia = os.stat(dst / "a.bin")
            ib = os.stat(dst / "b.bin")
            self.assertEqual((ia.st_dev, ia.st_ino), (ib.st_dev, ib.st_ino))
            self.assertEqual((dst / "b.bin").read_bytes(), b"HARD-LINK-DATA" * 50)
            self.assertTrue(fsops.verify_copy(str(src), str(dst), result)[0])

    def test_slots_reduce_per_record_overhead(self):
        fp = fsops.FileFingerprint(1, 2, "x")
        self.assertFalse(hasattr(fp, "__dict__"), "FileFingerprint 未启用 __slots__")
        item = fsops.CopyItem("a", "a", fp)
        self.assertFalse(hasattr(item, "__dict__"), "CopyItem 未启用 __slots__")


# ------------------------------------------------------- 保留名校验 (P2-14)

class TestReservedNames(unittest.TestCase):

    def test_rejects_reserved_device_names(self):
        for bad in (r"D:\UserData\CON", r"D:\UserData\nul", r"D:\UserData\COM1",
                    r"D:\UserData\LPT9", r"D:\UserData\CON.txt", r"D:\UserData\aux.log"):
            ok, msg = fsops.validate_destination(bad, [])
            self.assertFalse(ok, msg=bad)
            self.assertIn("保留名", msg)

    def test_rejects_trailing_dot_or_space_segment(self):
        for bad in ("D:\\UserData\\name. ", "D:\\UserData\\name."):
            ok, msg = fsops.validate_destination(bad, [])
            self.assertFalse(ok, msg=bad)
            self.assertIn("点/空格", msg)

    def test_does_not_break_legit_names(self):
        # 含保留名前缀的正常名字不能被误伤
        for good in (r"D:\UserData\console", r"D:\UserData\contacts",
                     r"D:\UserData\auxiliary", r"D:\UserData\COM10"):
            ok, msg = fsops.validate_destination(good, [])
            self.assertTrue(ok, f"{good} 被误伤：{msg}")


# ---------------------------------------------- 注册表读取异常可见 (P2-3)

class _RaisingBackend(MemoryBackend):
    """读取时抛 RegistryReadError 的假后端。"""

    def read_value(self, subkey, name):
        raise RegistryReadError(f"注入的读取失败 {subkey}\\{name}")


class TestRegistryReadErrorVisibility(unittest.TestCase):

    def test_read_error_is_recorded_and_consumable(self):
        store = RegistryStore(_RaisingBackend())
        self.assertIsNone(store.get_raw(USER_SHELL_FOLDERS_SUBKEY, "Personal"))
        self.assertIsNotNone(store.last_read_error)
        taken = store.consume_read_error()
        self.assertIn("注入的读取失败", taken)
        # 取走后清空
        self.assertIsNone(store.consume_read_error())

    def test_missing_value_does_not_raise(self):
        store = RegistryStore(MemoryBackend({}))
        self.assertIsNone(store.get_raw(USER_SHELL_FOLDERS_SUBKEY, "Personal"))
        self.assertIsNone(store.consume_read_error())

    def test_resolve_current_warns_but_still_falls_back(self):
        store = RegistryStore(_RaisingBackend())
        engine = TransferEngine(store=store, dry_run=True)
        spec = __import__("docmover.constants", fromlist=["FOLDERS_BY_KEY"]).FOLDERS_BY_KEY["documents"]
        with self.assertLogs("docmover.registry", level="WARNING"):
            current = engine.resolve_current(spec)
        self.assertTrue(current)   # 仍回退到默认，但已显式告警


# ------------------------------------------------------- 扫描缓存复用 (P2-7)

class TestStatCacheReuse(unittest.TestCase):

    def setUp(self):
        patcher = mock.patch.object(fsops, "FORBIDDEN_TARGET_ROOTS", ())
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_build_plan_reuses_cached_stat(self):
        from docmover.constants import USER_SHELL_FOLDERS_SUBKEY as USF

        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")
            engine = TransferEngine(
                store=RegistryStore(
                    MemoryBackend({USF: {"Personal": (str(source), 1)}})
                )
            )
            cached = fsops.TreeStat(files=42, bytes=123456)
            with mock.patch.object(fsops, "scan_tree") as scan:
                plan = engine.build_plan(
                    ["documents"], str(base / "new"),
                    create_user_dir=False, scan=True,
                    stat_cache={"documents": cached},
                )
            scan.assert_not_called()
            self.assertIs(plan[0].stat, cached)
            self.assertTrue(plan[0].scanned)

    def test_build_plan_scans_when_cache_misses(self):
        from docmover.constants import USER_SHELL_FOLDERS_SUBKEY as USF

        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")
            engine = TransferEngine(
                store=RegistryStore(
                    MemoryBackend({USF: {"Personal": (str(source), 1)}})
                )
            )
            plan = engine.build_plan(
                ["documents"], str(base / "new"),
                create_user_dir=False, scan=True, stat_cache={},
            )
            self.assertTrue(plan[0].scanned)
            self.assertEqual(plan[0].stat.files, 1)


# ------------------------------------------- windowed CLI 输出与兜底 (P2-2)

class TestWindowedCliOutput(unittest.TestCase):

    def setUp(self):
        import run as run_module

        # run.py 用模块级单例承载「本次运行」的缓冲/提示标志，逐条清空以免相互污染
        run_module._console_tail.clear()
        run_module._CONSOLE_NOTIFY_DONE = False
        run_module._CONSOLE_LOG_OK = True

    def test_out_falls_back_to_log_then_notice_fires_at_flush(self):
        """无 stdout 时 _out 落盘 + 缓冲；弹框推迟到 _flush_console_notice()（C4）。"""
        import run as run_module

        with tempfile.TemporaryDirectory() as tmp:
            log_path = Path(tmp) / "console.log"
            with mock.patch.object(run_module, "_console_log_path", return_value=log_path):
                with mock.patch.object(run_module, "_notify_no_console") as notified:
                    with mock.patch.object(sys, "stdout", None):
                        run_module._out("hello-windowed")
                        # C4 契约：_out 本身不再弹框，避免每行都骚扰用户
                        notified.assert_not_called()
                        run_module._flush_console_notice()
            self.assertTrue(log_path.exists())
            self.assertIn("hello-windowed", log_path.read_text(encoding="utf-8"))
            notified.assert_called_once()

    def test_flush_is_silent_when_nothing_was_output(self):
        """没有经过 _out 的输出（如 --help 直接写 stdout），收尾不得弹框。"""
        import run as run_module

        with mock.patch.object(run_module, "_notify_no_console") as notified:
            run_module._flush_console_notice()
        notified.assert_not_called()

    def test_main_returns_nonzero_on_unexpected_error(self):
        import run as run_module

        with tempfile.TemporaryDirectory() as tmp:
            log_path = Path(tmp) / "console.log"
            with mock.patch.object(run_module, "_console_log_path", return_value=log_path):
                with mock.patch.object(run_module, "_cmd_scan", side_effect=RuntimeError("boom")):
                    with mock.patch.object(sys, "stdout", None):
                        with mock.patch.object(run_module, "_notify_no_console"):
                            code = run_module.main(["--scan"])
            self.assertEqual(code, 4)
            self.assertIn("boom", log_path.read_text(encoding="utf-8"))

    def test_main_lets_help_raise_systemexit(self):
        import contextlib
        import io

        import run as run_module

        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            with self.assertRaises(SystemExit):
                run_module.main(["--version"])
        self.assertIn("DirMover", buffer.getvalue())


# ---------------------------------------- 意图日志原子写 / 损坏可见 (审计 B4)

class TestIntentJournalDurability(unittest.TestCase):
    """意图日志是「进程被硬杀后」唯一的收尾信号，必须原子写、且损坏要可见。

    全部在临时目录内进行，不触碰真实注册表与用户文件。
    """

    @staticmethod
    def _item() -> PlanItem:
        return PlanItem(
            spec=FOLDERS_BY_KEY["documents"],
            current=r"C:\Users\Tester\Documents",
            target=r"D:\UserData\Tester\Documents",
        )

    def setUp(self):
        # 每个用例先清掉上一条用例可能留下的读取错误（模块级通道）
        IntentJournal.consume_read_error()

    def test_half_written_json_is_reported_not_treated_as_clean(self):
        """非原子直写被硬杀留下的半截 JSON，必须被当作「损坏」而非「无记录」。"""
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            backup.mkdir()
            (backup / JOURNAL_FILENAME).write_text('{"items": {', encoding="utf-8")

            pending = IntentJournal.pending(backup)   # 不得抛异常

            self.assertEqual(pending, [])
            # 关键：不能是「干净的空列表」——必须暴露损坏状态
            self.assertTrue(
                IntentJournal.last_read_error(),
                "半截 JSON 被静默当成「没有残留记录」",
            )
            taken = IntentJournal.consume_read_error()
            self.assertIn("损坏", taken)
            self.assertEqual(IntentJournal.consume_read_error(), "")  # 取走后清空

    def test_valid_json_but_wrong_shape_is_reported(self):
        """合法 JSON 但结构不对（数组/数字）同样算损坏，且不得抛异常。"""
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            backup.mkdir()
            (backup / JOURNAL_FILENAME).write_text("[1, 2, 3]", encoding="utf-8")
            self.assertEqual(IntentJournal.pending(backup), [])
            self.assertTrue(IntentJournal.last_read_error())

    def test_missing_file_is_clean_and_not_reported(self):
        """没有日志文件 == 干净：返回 [] 且**不**报损坏。"""
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            backup.mkdir()
            self.assertEqual(IntentJournal.pending(backup), [])
            self.assertEqual(IntentJournal.last_read_error(), "")

    def test_failed_replace_keeps_previous_file_parsable(self):
        """``os.replace`` 失败时不得破坏既有日志（证明不是「截断后写入」）。"""
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            journal = IntentJournal.open(backup, enabled=True)
            item = self._item()
            journal.begin([item], None)             # 先落一份有效日志
            journal_file = backup / JOURNAL_FILENAME
            before = json.loads(journal_file.read_text(encoding="utf-8"))
            self.assertEqual(before["items"][item.key]["stage"], "pending")

            with mock.patch.object(
                os, "replace", side_effect=OSError("注入的替换失败")
            ) as replace:
                journal.mark(item, "copied")        # 走原子写路径，替换失败

            self.assertTrue(replace.called, "未使用 os.replace（仍是非原子直写）")
            # 既有日志必须仍然可解析，且内容还是上一次成功写入的状态
            after = json.loads(journal_file.read_text(encoding="utf-8"))
            self.assertEqual(after["items"][item.key]["stage"], "pending")
            self.assertFalse(journal.enabled, "写失败后应停用日志，避免继续覆盖")
            # 不留下半成品临时文件
            self.assertFalse(
                (backup / (JOURNAL_FILENAME + ".tmp")).exists(),
                "替换失败后残留了 .tmp 临时文件",
            )

    def test_successful_flush_leaves_no_tmp_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            journal = IntentJournal.open(backup, enabled=True)
            item = self._item()
            journal.begin([item], None)
            journal.mark(item, "copied")
            self.assertTrue((backup / JOURNAL_FILENAME).exists())
            self.assertFalse((backup / (JOURNAL_FILENAME + ".tmp")).exists())
            self.assertEqual(IntentJournal.pending(backup)[0]["stage"], "copied")


# ------------------------------------ 取消的正常收尾不残留日志 (审计 B2 / P1)

class TestCancelLeavesNoJournal(unittest.TestCase):
    """中途取消是**正常收尾**：绝不能把 cancelled 留在 intent.json。

    残留会让下次启动把该 stage 当成「上次转移未正常收尾」，并附上本轮
    **整批项**的注册表快照建议回滚——那会把本轮已成功（源目录已删）的项
    一起滚回旧路径，制造指向不存在目录的孤儿注册表值。
    """

    @staticmethod
    def _item() -> PlanItem:
        return PlanItem(
            spec=FOLDERS_BY_KEY["documents"],
            current=r"C:\Users\Tester\Documents",
            target=r"D:\UserData\Tester\Documents",
        )

    def test_close_run_clears_cancelled_entries(self):
        """单测：cancelled 与 pending 一样，在 close_run 后被注销（日志随之删除）。"""
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            journal = IntentJournal.open(backup, enabled=True)
            item = self._item()
            journal.begin([item], None)
            journal.mark(item, "cancelled")
            journal.close_run()

            self.assertEqual(IntentJournal.pending(backup), [])
            self.assertFalse(
                (backup / JOURNAL_FILENAME).exists(),
                "取消后仍残留 intent.json",
            )

    def test_mid_copy_cancel_leaves_no_journal_end_to_end(self):
        """端到端：真实引擎跑「文档成功 + 桌面复制中途取消」，日志不得残留。

        这正是 P1 的复现路径（先做成功一项，再在第二项复制中点停止）。
        """
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            doc_src = base / "old" / "文档"
            doc_src.mkdir(parents=True)
            (doc_src / "a.txt").write_text("aaa", encoding="utf-8")
            desk_src = base / "old" / "桌面"
            desk_src.mkdir(parents=True)
            for i in range(5):
                (desk_src / f"d{i}.txt").write_text("x" * 512, encoding="utf-8")
            backup = base / "backups"

            store = RegistryStore(
                MemoryBackend(
                    {
                        USER_SHELL_FOLDERS_SUBKEY: {
                            "Personal": (str(doc_src), 1),
                            "Desktop": (str(desk_src), 1),
                        }
                    }
                )
            )
            engine = TransferEngine(store=store, backup_dir=backup)
            # 临时目录位于 C:\Users 下，会命中「禁止转移到系统目录」闸门，故放行
            with mock.patch.object(
                fsops, "validate_destination", return_value=(True, "")
            ):
                plan = engine.build_plan(
                    ["documents", "desktop"], str(base / "new"),
                    create_user_dir=False,
                )

            state = {"armed": False, "first": True}

            def on_item_done(item):
                if item.key == "documents":
                    state["armed"] = True   # 文档成功后才允许取消

            def is_cancelled():
                if not state["armed"]:
                    return False
                if state["first"]:
                    # 放行「桌面」的项前预检查，让取消落在复制循环内
                    state["first"] = False
                    return False
                return True

            with mock.patch.object(
                fsops, "try_atomic_move", return_value=(False, "强制复制路径")
            ):
                report = engine.execute(
                    plan, on_item_done=on_item_done, is_cancelled=is_cancelled
                )

            statuses = {o.key: o.status for o in report.outcomes}
            self.assertEqual(statuses["documents"], "ok", report.outcomes[0].message)
            self.assertEqual(statuses["desktop"], "cancelled")
            self.assertFalse(doc_src.exists(), "成功项的源目录应已删除")
            self.assertTrue(desk_src.exists(), "取消项的源目录必须保留")
            self.assertFalse(
                (backup / JOURNAL_FILENAME).exists(),
                "取消后仍残留 intent.json → 下次启动会误报未完成并建议回滚已成功项",
            )
            self.assertEqual(IntentJournal.pending(backup), [])


# ------------------------------------ 删除前的闸门 / 阶段语义 (审计 R2/R3/R5/R7)

class _FailingWriteBackend(MemoryBackend):
    """第 N 次写入抛 OSError（用于制造「注册表写失败」）。"""

    def __init__(self, initial=None, fail_on_write: int = 1):
        super().__init__(initial)
        self.fail_on_write = fail_on_write
        self.attempts = 0

    def write_value(self, subkey, name, value, regtype) -> None:
        self.attempts += 1
        if self.fail_on_write and self.attempts >= self.fail_on_write:
            raise OSError("注入的写入失败")
        super().write_value(subkey, name, value, regtype)


class TestTransferSafetyGates(unittest.TestCase):
    """R2/R3/R5/R7：删源前闸门、源禁区校验、回滚文案、阶段语义。"""

    USF = USER_SHELL_FOLDERS_SUBKEY

    def _engine(self, source: Path, backend=None, backup=None):
        store = RegistryStore(
            backend or MemoryBackend({self.USF: {"Personal": (str(source), 1)}})
        )
        return TransferEngine(store=store, backup_dir=backup)

    # ---- R2①：源/目标是同一物理位置 → 不删源

    def test_refuses_to_delete_when_target_resolves_to_source(self):
        """R2① 第 3 层（删源闸门）：只在「写注册表」之后才暴露的别名必须在此拦下。

        关键：这条用例必须命中 transfer.py 的**第 3 层**闸门，而不是 copy_merge
        的 A2 自保护。做法是让 ``resolves_to_same`` 在校验通过前返回真实值（非
        别名）、校验通过后才「变成别名」——模拟写注册表期间目标被换成指向源的
        联结点。若整次 execute 恒返回 True，则 copy_merge 在复制阶段就会拒绝，
        第 3 层永远不会被执行（那就是「绿着的假覆盖」）。
        """
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")

            engine = self._engine(source)
            with mock.patch.object(
                fsops, "validate_destination", return_value=(True, "")
            ):
                plan = engine.build_plan(
                    ["documents"], str(base / "new"), create_user_dir=False
                )

            real_rts = fsops.resolves_to_same
            real_verify = fsops.verify_copy
            state = {"armed": False}   # 校验通过前保持「非别名」，之后才变「别名」

            def armed_rts(a, b):
                return True if state["armed"] else real_rts(a, b)

            def verify_then_alias(src, dst, result, **kwargs):
                out = real_verify(src, dst, result, **kwargs)
                state["armed"] = True   # 模拟「写注册表」期间目标被换成指向源的联结点
                return out

            with mock.patch.object(
                fsops, "try_atomic_move", return_value=(False, "强制复制路径")
            ), mock.patch.object(
                fsops, "resolves_to_same", side_effect=armed_rts
            ), mock.patch.object(
                fsops, "verify_copy", side_effect=verify_then_alias
            ), mock.patch.object(
                fsops, "safe_remove_directory", return_value=(True, "removed")
            ) as remove:
                report = engine.execute(plan)

            self.assertEqual(report.outcomes[0].status, "failed")
            # 只对第 3 层成立的专属文案（copy_merge 那条是「源与目标指向同一位置，
            # 已拒绝执行」——两者都含「同一位置」，故必须断言更长的专属子串）
            self.assertIn("源与目标实际指向同一位置", report.outcomes[0].message)
            self.assertNotIn("已拒绝执行", report.outcomes[0].message)
            self.assertEqual(remove.call_count, 0, "第 3 层闸门未生效：仍去删源了")
            self.assertTrue(source.exists(), "闸门命中却删掉了源目录")
            self.assertTrue((source / "a.txt").exists())

    # ---- R2②：源在复制后发生变化 → 不删源

    def test_refuses_to_delete_when_source_changed_after_verify(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")

            engine = self._engine(source)
            with mock.patch.object(
                fsops, "validate_destination", return_value=(True, "")
            ):
                plan = engine.build_plan(
                    ["documents"], str(base / "new"), create_user_dir=False
                )

            real_verify = fsops.verify_copy

            def verify_then_intrude(src, dst, result, **kwargs):
                ok, detail = real_verify(src, dst, result, **kwargs)
                # 模拟「写注册表」期间其它程序在源里新建了文件
                (Path(src) / "intruder.txt").write_text("late", encoding="utf-8")
                return ok, detail

            with mock.patch.object(
                fsops, "try_atomic_move", return_value=(False, "强制复制路径")
            ), mock.patch.object(fsops, "verify_copy", side_effect=verify_then_intrude):
                report = engine.execute(plan)

            self.assertEqual(report.outcomes[0].status, "failed")
            self.assertIn("发生变化", report.outcomes[0].message)
            self.assertTrue(
                (source / "intruder.txt").exists(), "迟到的源文件被连带删除了"
            )

    # ---- R3：源的禁区校验（转移路径不再豁免）

    def test_build_plan_blocks_forbidden_source_tree(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            engine = TransferEngine(
                store=RegistryStore(
                    MemoryBackend({self.USF: {"Personal": (r"C:\Windows", 2)}})
                ),
                dry_run=True,
            )
            with mock.patch.object(fsops, "FORBIDDEN_TARGET_ROOTS", ()):
                plan = engine.build_plan(
                    ["documents"], str(base / "new"), create_user_dir=False
                )
            self.assertIn("系统目录", plan[0].invalid_reason)
            self.assertIn("拒绝搬运", plan[0].invalid_reason)

            # 执行必须原地失败：dry_run 引擎保证即使闸门被改坏也不会真去搬
            report = engine.execute(plan)
            self.assertEqual(report.outcomes[0].status, "failed")
            self.assertIn("系统目录", report.outcomes[0].message)

    # ---- R5：_rollback_move 任何分支都不返回空串

    def test_rollback_move_always_reports_destination(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            engine = self._engine(base / "old" / "文档")

            # ① 合并项：不做整目录回滚，但必须说清数据在哪
            merge_item = PlanItem(
                spec=FOLDERS_BY_KEY["documents"],
                current=str(base / "old" / "文档"),
                target=str(base / "new" / "文档"),
                merge=True,
            )
            note = engine._rollback_move(merge_item)
            self.assertTrue(note)
            self.assertIn("已在", note)

            # ② 目标不是目录 → 无法回滚，也要说实话
            plain_item = PlanItem(
                spec=FOLDERS_BY_KEY["documents"],
                current=str(base / "gone" / "文档"),
                target=str(base / "not-a-dir"),
            )
            note = engine._rollback_move(plain_item)
            self.assertTrue(note)
            self.assertIn("已在", note)

            # ③ 条件满足 → 真的移回，并说明去向
            target = base / "new" / "文档"
            target.mkdir(parents=True)
            (target / "a.txt").write_text("a", encoding="utf-8")
            move_item = PlanItem(
                spec=FOLDERS_BY_KEY["documents"],
                current=str(base / "restore" / "文档"),
                target=str(target),
            )
            note = engine._rollback_move(move_item)
            self.assertIn("改回", note)
            self.assertTrue((base / "restore" / "文档" / "a.txt").exists())

    # ---- R7：重命名路径与 registry_only 的阶段语义

    def test_rename_path_marks_source_removed(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")
            backup = base / "backups"

            engine = self._engine(source, backup=backup)
            with mock.patch.object(
                fsops, "validate_destination", return_value=(True, "")
            ):
                plan = engine.build_plan(
                    ["documents"], str(base / "new"), create_user_dir=False
                )

            stages = []
            real_mark = engine.journal.mark

            def recording_mark(item, stage):
                stages.append((item.key, stage))
                return real_mark(item, stage)

            with mock.patch.object(engine.journal, "mark", side_effect=recording_mark):
                report = engine.execute(plan)

            self.assertEqual(report.outcomes[0].status, "ok", report.outcomes[0].message)
            self.assertIn(("documents", "source-removed"), stages)
            self.assertIn(("documents", "registry-written"), stages)
            self.assertFalse((backup / JOURNAL_FILENAME).exists())

    def test_registry_only_marks_source_removed_on_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            engine = self._engine(base / "gone" / "文档", backup=base / "backups")
            item = PlanItem(
                spec=FOLDERS_BY_KEY["documents"],
                current=str(base / "gone" / "文档"),   # 不存在 → registry_only
                target=str(base / "home" / "Documents"),
                registry_value=r"%USERPROFILE%\Documents",
                registry_targets=[(self.USF, "Personal")],
                registry_only=True,
            )
            stages = []
            real_mark = engine.journal.mark

            def recording_mark(it, stage):
                stages.append(stage)
                return real_mark(it, stage)

            engine.journal.begin([item], None)
            with mock.patch.object(engine.journal, "mark", side_effect=recording_mark):
                outcome = engine._execute_registry_only(item)

            self.assertEqual(outcome.status, "ok")
            self.assertIn("source-removed", stages)


# ------------------------- R8：只回滚未完成项涉及的注册表值

class TestUnfinishedRegistryTargets(unittest.TestCase):
    """审计 R8：回滚只能覆盖「未完成」项，否则会把已成功项滚回已删除的旧路径。"""

    USF = USER_SHELL_FOLDERS_SUBKEY

    @staticmethod
    def _item(key: str, subkey: str, name: str) -> PlanItem:
        return PlanItem(
            spec=FOLDERS_BY_KEY[key],
            current=rf"C:\Users\Tester\{key}",
            target=rf"D:\UserData\Tester\{key}",
            registry_targets=[(subkey, name)],
        )

    def test_only_unfinished_items_targets_are_returned(self):
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            journal = IntentJournal.open(backup, enabled=True)
            done = self._item("documents", self.USF, "Personal")
            half = self._item("desktop", self.USF, "Desktop")
            journal.begin([done, half], "registry-1.json")
            journal.mark(done, "source-removed")   # A 已彻底完成
            journal.mark(half, "copied")           # B 半完成
            journal.close_run()

            targets = IntentJournal.unfinished_registry_targets(backup)
            self.assertEqual(targets, {(self.USF, "Desktop")})
            self.assertNotIn((self.USF, "Personal"), targets)

    def test_missing_or_damaged_journal_returns_empty_set(self):
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            backup.mkdir()
            # 无日志
            self.assertEqual(IntentJournal.unfinished_registry_targets(backup), set())
            # 损坏日志
            (backup / JOURNAL_FILENAME).write_text('{"items": {', encoding="utf-8")
            self.assertEqual(IntentJournal.unfinished_registry_targets(backup), set())

    def test_legacy_journal_without_registry_field_returns_empty_set(self):
        """旧日志没有 registry 字段 → 空集合（不过滤，保持既有行为）。"""
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            backup.mkdir()
            (backup / JOURNAL_FILENAME).write_text(
                json.dumps(
                    {
                        "snapshot": "registry-1.json",
                        "items": {
                            "documents": {
                                "display": "我的文档",
                                "source": r"C:\Users\T\Documents",
                                "target": r"D:\UserData\T\Documents",
                                "stage": "copied",
                            }
                        },
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            self.assertEqual(IntentJournal.unfinished_registry_targets(backup), set())


# ------------------------- R9：pending 暴露 registry_known + 快照裁剪纯函数

class TestRegistryKnownFlag(unittest.TestCase):
    """审计 R9-1：``pending()`` 必须区分「本项没有注册表目标」与「旧日志压根没这字段」。"""

    USF = USER_SHELL_FOLDERS_SUBKEY

    def _write_journal(self, backup: Path, item: dict) -> None:
        backup.mkdir(parents=True, exist_ok=True)
        (backup / JOURNAL_FILENAME).write_text(
            json.dumps(
                {"snapshot": "registry-1.json", "items": {"documents": item}},
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )

    def test_legacy_entry_without_registry_key_is_marked_unknown(self):
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            self._write_journal(backup, {
                "display": "我的文档",
                "source": r"C:\Users\T\Documents",
                "target": r"D:\UserData\T\Documents",
                "stage": "copied",
            })
            entry = IntentJournal.pending(backup)[0]
            self.assertEqual(entry["registry"], [])
            self.assertIs(entry["registry_known"], False)

    def test_entry_with_registry_key_is_marked_known(self):
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            self._write_journal(backup, {
                "display": "我的文档",
                "source": r"C:\Users\T\Documents",
                "target": r"D:\UserData\T\Documents",
                "stage": "copied",
                "registry": [[self.USF, "Personal"]],
            })
            entry = IntentJournal.pending(backup)[0]
            self.assertEqual(entry["registry"], [[self.USF, "Personal"]])
            self.assertIs(entry["registry_known"], True)


class TestRestrictRegistrySnapshot(unittest.TestCase):
    """审计 R9-2：整轮回滚前把快照裁到「未完成项」，避免把已成功项滚回已删路径。"""

    USF = USER_SHELL_FOLDERS_SUBKEY

    @staticmethod
    def _snapshot(*names: str) -> RegistrySnapshot:
        return RegistrySnapshot(
            created_at="2026-01-01T00:00:00",
            entries=[
                RegistryEntry(
                    subkey=TestRestrictRegistrySnapshot.USF,
                    name=name,
                    value=rf"D:\UserData\T\{name}",
                    regtype=1,
                    existed=True,
                )
                for name in names
            ],
        )

    def test_keeps_only_matching_entries_and_counts_excluded(self):
        snapshot = self._snapshot("Personal", "Desktop", "Downloads")
        kept, excluded = restrict_registry_snapshot(
            snapshot, [(self.USF, "Desktop")]
        )
        self.assertEqual([e.name for e in kept.entries], ["Desktop"])
        self.assertEqual(excluded, 2)
        # created_at 必须原样保留
        self.assertEqual(kept.created_at, snapshot.created_at)

    def test_matching_is_case_insensitive(self):
        snapshot = self._snapshot("Personal")
        kept, excluded = restrict_registry_snapshot(
            snapshot, [(self.USF.lower(), "personal")]
        )
        self.assertEqual([e.name for e in kept.entries], ["Personal"])
        self.assertEqual(excluded, 0)

    def test_no_match_yields_empty_snapshot_and_full_exclusion(self):
        snapshot = self._snapshot("Personal", "Desktop")
        kept, excluded = restrict_registry_snapshot(
            snapshot, [(self.USF, "Nonexistent")]
        )
        self.assertEqual(kept.entries, [])
        self.assertEqual(excluded, 2)


# ------------------------- R10：源禁区判定须穿透重解析点

class TestUnsafeSourceTreeFollowsLinks(unittest.TestCase):
    """审计 R10：``current`` 是指向禁区的 junction 时也必须被拒。

    ``is_within``/``same_path`` 只做字符串比较，不展开链接——注册表若被改成
    「指向 C:\\Windows 的 junction」，纯字符串判定会放行，随后整目录搬走再删掉。
    这里用 mock 模拟 realpath 展开到系统目录（无需创建真实 junction）。
    """

    def test_reparse_point_into_system_tree_is_rejected(self):
        innocent = r"C:\Users\Tester\MyDocs"
        with mock.patch.object(
            os.path, "realpath", return_value=r"C:\Windows\System32"
        ):
            kind, reason = _unsafe_source_tree(innocent)
        self.assertEqual(kind, "system")
        self.assertIn("系统目录", reason)

    def test_plain_user_path_is_not_rejected(self):
        with mock.patch.object(os.path, "realpath", side_effect=lambda p: p):
            kind, reason = _unsafe_source_tree(r"C:\Users\Tester\MyDocs")
        self.assertEqual(kind, "")
        self.assertEqual(reason, "")


# ------------- F3：源禁区闸门被 ``\\?\`` 扩展前缀绕过（人称问题是关于前缀的）

class TestExtendedPrefixSourceGate(unittest.TestCase):
    r"""审计 F3：``\\?\C:\Windows`` 必须和 ``C:\Windows`` 一样被拦下。

    根因是参与比较的字符串形态不一致：``realpath()`` 对 ``\\?\`` 路径**原样返回**，
    ``normalize()`` 于是得到 ``\\?\c:\windows``，与禁区 ``c:\windows`` 既不等也不
    满足 ``startswith``；而且 ``normalize(realpath(path)) == normalize(path)`` 还会
    误判「无需追加第二候选」，让 R10 的 realpath 加固彻底空转。
    """

    def test_prefixed_system_paths_are_rejected(self):
        for bad in (
            "\\\\?\\C:\\Windows",
            "\\\\?\\C:\\Windows\\System32",
            "\\\\?\\C:\\Program Files",
            "\\\\?\\C:\\Program Files (x86)",
            "\\\\?\\C:\\ProgramData",
            "\\\\?\\C:\\Users",
            "\\\\?\\C:\\Recovery",
            "\\\\.\\C:\\Windows",               # 同族的 device 前缀同样要拦
        ):
            kind, reason = _unsafe_source_tree(bad)
            self.assertEqual(kind, "system", f"{bad} 未被拦下：kind={kind} {reason}")

    def test_prefixed_drive_root_is_rejected(self):
        kind, reason = _unsafe_source_tree("\\\\?\\C:\\")
        self.assertEqual(kind, "root", f"盘根未被拦下：{reason}")

    def test_prefixed_short_name_is_rejected(self):
        r"""``\\?\C:\PROGRA~1``（前缀 + 8.3 短名混合形态）也必须被拦。"""
        if not os.path.isdir(r"C:\PROGRA~1"):
            self.skipTest("本机没有 C:\\PROGRA~1 短名（8.3 名称生成被禁用）")
        for bad in ("\\\\?\\C:\\PROGRA~1", "\\\\?\\C:\\PROGRA~1\\Some App",
                    r"C:\PROGRA~1"):
            kind, reason = _unsafe_source_tree(bad)
            self.assertEqual(kind, "system", f"{bad} 未被拦下：{reason}")

    def test_normal_user_paths_still_pass(self):
        """回归：用户配置目录下的自定义位置是合法状态，不能误伤。"""
        for good in (
            r"C:\Users\Tester\MyDocs",
            r"D:\UserData\Tester\文档",
            r"C:\Users\Tester\AppData\Local\Temp\tmp1234",
        ):
            with mock.patch.object(os.path, "realpath", side_effect=lambda p: p):
                kind, reason = _unsafe_source_tree(good)
            self.assertEqual(kind, "", f"{good} 被误伤：{reason}")

    def test_build_plan_blocks_prefixed_system_source(self):
        """端到端：注册表里的 current 带前缀也必须让该项受阻。"""
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            engine = TransferEngine(
                store=RegistryStore(
                    MemoryBackend(
                        {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (r"\\?\C:\Windows", 2)}}
                    )
                ),
                dry_run=True,
            )
            with mock.patch.object(fsops, "FORBIDDEN_TARGET_ROOTS", ()):
                plan = engine.build_plan(
                    ["documents"], str(base / "new"), create_user_dir=False
                )
            self.assertIn("系统目录", plan[0].invalid_reason, plan[0].invalid_reason)
            # dry_run 引擎：即使闸门再被改坏也不会真去搬 C:\Windows
            report = engine.execute(plan)
            self.assertEqual(report.outcomes[0].status, "failed")


# ------------------------- R11：注册表就地回滚须报出被跳过项

class _SkipRollbackBackend(MemoryBackend):
    """Personal 读/写都失败：快照期读失败 → unknown（回滚必须跳过）；Desktop 正常。"""

    def read_value(self, subkey, name):
        if name == "Personal":
            raise RegistryReadError("injected read failure")
        return super().read_value(subkey, name)

    def write_value(self, subkey, name, value, regtype):
        if name == "Personal":
            raise OSError("injected write failure")
        return super().write_value(subkey, name, value, regtype)


class _WriteFailBackend(MemoryBackend):
    """Personal 写失败、但读取正常且「不存在」→ 回滚走 delete（无跳过项）。"""

    def write_value(self, subkey, name, value, regtype):
        if name == "Personal":
            raise OSError("injected write failure")
        return super().write_value(subkey, name, value, regtype)


class TestWriteRegistryReportsSkippedRollback(unittest.TestCase):
    """审计 R11：就地回滚有跳过项时必须在消息里说明，不能「报成功但没做」。"""

    USF = USER_SHELL_FOLDERS_SUBKEY

    @staticmethod
    def _item() -> PlanItem:
        return PlanItem(
            spec=FOLDERS_BY_KEY["documents"],
            current=r"C:\Users\T\Documents",
            target=r"D:\UserData\T\Documents",
            registry_targets=[
                (TestWriteRegistryReportsSkippedRollback.USF, "Personal"),
                (TestWriteRegistryReportsSkippedRollback.USF, "Desktop"),
            ],
        )

    def test_reports_skipped_entries(self):
        store = RegistryStore(
            _SkipRollbackBackend({self.USF: {"Desktop": ("old", 1)}})
        )
        engine = TransferEngine(store=store)
        ok, message, _written = engine._write_registry(
            self._item(), r"D:\UserData\T\Documents"
        )
        self.assertFalse(ok)
        self.assertIn("另有 1 处因快照读取失败被跳过", message)

    def test_no_extra_sentence_when_nothing_skipped(self):
        store = RegistryStore(
            _WriteFailBackend({self.USF: {"Desktop": ("old", 1)}})
        )
        engine = TransferEngine(store=store)
        ok, message, _written = engine._write_registry(
            self._item(), r"D:\UserData\T\Documents"
        )
        self.assertFalse(ok)
        self.assertIn("已回滚本项 2 处", message)
        self.assertNotIn("跳过", message)


# --------------------------- R2：取消后目标盘不得残留半截文件

class TestCancelLeavesNoPartialFile(unittest.TestCase):
    """审计 R2：旧实现 ``open(dst,"wb")`` 直写，取消时目标已落盘一部分。

    实测 5MB 文件复制中途取消会留下 1MB 半截文件；用户重试时那份残骸与"真的
    复制过来的文件"同名冲突，于是被另存成 ``big (1).bin``，垃圾再也不会被清理。
    """

    BIG = 5 << 20          # 5 MiB：远大于 DIGEST_CHUNK，保证能"写到一半"

    @staticmethod
    def _tree(tmp: str):
        src = Path(tmp) / "src"
        src.mkdir()
        (src / "big.bin").write_bytes(b"X" * TestCancelLeavesNoPartialFile.BIG)
        dst = Path(tmp) / "dst"
        dst.mkdir()
        return src, dst

    def _cancel_after_first_block(self):
        """第 1 次在 copy_merge 顶部、第 2 次在读写循环开始…第 3 次即 True。

        这样取消必然落在**第一个分块写完之后**，正是"目标已落盘 1 MiB"的时刻。
        """
        calls = {"n": 0}

        def is_cancelled():
            calls["n"] += 1
            return calls["n"] >= 3

        return is_cancelled

    def test_cancel_mid_file_leaves_no_partial(self):
        with tempfile.TemporaryDirectory() as tmp:
            src, dst = self._tree(tmp)
            result = fsops.copy_merge(
                str(src), str(dst), is_cancelled=self._cancel_after_first_block()
            )
            self.assertTrue(result.cancelled, "未按预期中途取消")
            leftovers = sorted(os.listdir(dst))
            self.assertEqual(
                leftovers, [], f"取消后在目标留下残骸：{leftovers}"
            )
            # 临时文件也要当场删掉，不能留着占地方
            self.assertEqual(
                sorted(p.name for p in Path(dst).glob("*.part*")), []
            )
            self.assertEqual(
                (src / "big.bin").stat().st_size, self.BIG, "源被改动了"
            )

    def test_retry_after_cancel_produces_no_collision(self):
        """重试必须是"续传"，而不是出现 ``big (1).bin``。"""
        with tempfile.TemporaryDirectory() as tmp:
            src, dst = self._tree(tmp)
            fsops.copy_merge(
                str(src), str(dst), is_cancelled=self._cancel_after_first_block()
            )
            self.assertEqual(sorted(os.listdir(dst)), [])

            second = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(second.ok, second.errors)
            self.assertEqual(
                sorted(os.listdir(dst)), ["big.bin"], "重试产生了 name (1).ext"
            )
            self.assertEqual(
                (dst / "big.bin").stat().st_size, self.BIG, "重试没有完整复制"
            )
            ok, detail = fsops.verify_copy(str(src), str(dst), second)
            self.assertTrue(ok, detail)

    def test_successful_copy_leaves_no_temp_file(self):
        """正常路径同样不能留下任何临时文件。"""
        with tempfile.TemporaryDirectory() as tmp:
            src, dst = self._tree(tmp)
            result = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(result.ok, result.errors)
            self.assertEqual(sorted(os.listdir(dst)), ["big.bin"])
            self.assertEqual(sorted(p.name for p in Path(dst).glob("*.part*")), [])


# ------------------- P1：校验阶段可上报进度、可响应取消（不得删源）

class TestVerifyStageCanBeCancelled(unittest.TestCase):
    """审计 P1：校验的取消必须落到 ST_CANCELLED，且**不得删源**。"""

    USF = USER_SHELL_FOLDERS_SUBKEY

    def test_cancel_during_verify_keeps_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_bytes(b"PAYLOAD" * 1000)

            engine = TransferEngine(
                store=RegistryStore(
                    MemoryBackend({self.USF: {"Personal": (str(source), 1)}})
                )
            )
            with mock.patch.object(
                fsops, "validate_destination", return_value=(True, "")
            ):
                plan = engine.build_plan(
                    ["documents"], str(base / "new"), create_user_dir=False
                )

            armed = {"now": False}
            real_verify = fsops.verify_copy

            def verify_then_cancel(src, dst, result, **kwargs):
                armed["now"] = True          # 进入校验即置位，让内部抛 CopyCancelled
                # 覆盖传入的 is_cancelled（避免与 **kwargs 里的同名键重复传参）
                kwargs["is_cancelled"] = lambda: armed["now"]
                return real_verify(src, dst, result, **kwargs)

            with mock.patch.object(
                fsops, "try_atomic_move", return_value=(False, "强制复制路径")
            ), mock.patch.object(
                fsops, "verify_copy", side_effect=verify_then_cancel
            ), mock.patch.object(
                fsops, "safe_remove_directory", wraps=fsops.safe_remove_directory
            ) as spy:
                report = engine.execute(plan)

            outcome = report.outcomes[0]
            self.assertEqual(outcome.status, "cancelled", outcome.message)
            self.assertIn("源文件未删除", outcome.message)
            self.assertEqual(spy.call_count, 0, "校验阶段取消却去删源了")
            self.assertTrue(source.exists(), "源目录被删除")
            self.assertEqual(
                (source / "a.txt").read_bytes(), b"PAYLOAD" * 1000
            )


# -------------------- F4：删源闸门必须比指纹，不能只比名字集合

class TestDeleteGateComparesFingerprints(unittest.TestCase):
    """审计 F4：升级为比对 ``(size, mtime_ns)`` 后的删源闸门。

    旧实现只比名字集合：攻击者把某文件改写、再扮回来（同长度 + 把 mtime_ns 改回
    原值，**不需要管理员**），工具看不见 → 删源 → 只剩旧副本、新版本被销毁。
    补上指纹比对后，「大小不同 / 时间戳不同」这两类改写会在最后一刻被拦下。
    """

    USF = USER_SHELL_FOLDERS_SUBKEY

    def _execute_with_intrusion(self, tmp, intruder):
        """跑完整链路，并在 verify 返回之后、删源之前执行 ``intruder(source)``。"""
        base = Path(tmp)
        source = base / "old" / "文档"
        source.mkdir(parents=True)
        (source / "a.txt").write_bytes(b"A" * 1000)
        (source / "b.txt").write_bytes(b"B" * 1000)

        engine = TransferEngine(
            store=RegistryStore(
                MemoryBackend({self.USF: {"Personal": (str(source), 1)}})
            )
        )
        with mock.patch.object(fsops, "validate_destination", return_value=(True, "")):
            plan = engine.build_plan(
                ["documents"], str(base / "new"), create_user_dir=False
            )

        real_verify = fsops.verify_copy

        def verify_then_intrude(src, dst, result, **kwargs):
            ok, detail = real_verify(src, dst, result, **kwargs)
            intruder(Path(src))
            return ok, detail

        with mock.patch.object(
            fsops, "try_atomic_move", return_value=(False, "强制复制路径")
        ), mock.patch.object(
            fsops, "verify_copy", side_effect=verify_then_intrude
        ), mock.patch.object(
            fsops, "safe_remove_directory", wraps=fsops.safe_remove_directory
        ) as spy:
            report = engine.execute(plan)
        return report.outcomes[0], source, spy

    def test_untouched_source_is_still_deleted(self):
        """回归：源没被改动时闸门不得误拦（否则转移永远完不成）。"""
        with tempfile.TemporaryDirectory() as tmp:
            outcome, source, spy = self._execute_with_intrusion(tmp, lambda src: None)
            self.assertEqual(outcome.status, "ok", outcome.message)
            self.assertEqual(spy.call_count, 1)
            self.assertFalse(source.exists(), "正常路径下源目录应被清理")

    def test_size_change_after_verify_blocks_deletion(self):
        """同名字、体积变了 → 必须拦下并保留源目录。"""
        with tempfile.TemporaryDirectory() as tmp:
            def intruder(src):
                (src / "a.txt").write_bytes(b"A" * 2000)   # 名字没变，体积变了

            outcome, source, spy = self._execute_with_intrusion(tmp, intruder)
            self.assertEqual(outcome.status, "failed", outcome.message)
            self.assertIn("发生变化", outcome.message)
            self.assertIn("a.txt", outcome.message)
            self.assertEqual(spy.call_count, 0, "源被改动却照样删源")
            self.assertTrue(source.exists())
            self.assertEqual((source / "a.txt").read_bytes(), b"A" * 2000)

    def test_mtime_change_after_verify_blocks_deletion(self):
        """体积没变、时间戳变了 → 同样必须拦下。"""
        with tempfile.TemporaryDirectory() as tmp:
            def intruder(src):
                # 内容逐字节不变，只把 mtime 往前挪一秒
                path = src / "b.txt"
                info = os.stat(path)
                os.utime(path, ns=(info.st_atime_ns, info.st_mtime_ns - 1_000_000_000))

            outcome, source, spy = self._execute_with_intrusion(tmp, intruder)
            self.assertEqual(outcome.status, "failed", outcome.message)
            self.assertIn("发生变化", outcome.message)
            self.assertEqual(spy.call_count, 0, "时间戳被改了却照样删源")
            self.assertTrue(source.exists())

    def test_residual_window_same_size_and_mtime_is_not_detected(self):
        """诚实记录仍然存在的残余窗口（勿据此认为 F4 已完全闭环）。

        攻击者若把文件改写成**相同长度**并把 ``mtime_ns`` **精确改回原值**，
        ``(size, mtime_ns)`` 指纹与清单完全一致，删源闸门（以及 ``verify_copy``
        自己的那一行比对）都看不见 —— 必须在此时重算**内容摘要**才能发现，而
        全树再读一遍摘要正是当初刻意避开的代价。

        本用例把这个尚未覆盖的形态**显式钉住**：一旦将来有人在删源前补了内容级
        复核，本用例会变红，提示此处需要同步更新注释与 README 的风险说明。
        注意窗口已大幅收窄：**改动必须发生在「校验通过之后、删源之前」这几十
        毫秒内**，而不是旧实现的「从该文件被读完一直到整树删完」的分钟级。
        """
        with tempfile.TemporaryDirectory() as tmp:
            def intruder(src):
                path = src / "a.txt"
                info = os.stat(path)
                (path).write_bytes(b"Z" * 1000)          # 同长度、内容已变
                os.utime(path, ns=(info.st_atime_ns, info.st_mtime_ns))  # mtime 还原

            outcome, source, spy = self._execute_with_intrusion(tmp, intruder)
            self.assertEqual(outcome.status, "ok", outcome.message)
            self.assertTrue(spy.called)
            self.assertFalse(source.exists(), "前提变了：本残余窗口已被堵上")


# ------------------- B-2：复制失败必须落日志（README 承诺只写文件）

class RecordingLogger:
    """收集 ``TransferEngine._log`` 的调用，替代真实 logging。"""

    def __init__(self):
        self.records = []

    def _add(self, level: str, message: str) -> None:
        self.records.append((level, message))

    def info(self, message, *args):
        self._add("info", message)

    def warning(self, message, *args):
        self._add("warning", message)

    def error(self, message, *args):
        self._add("error", message)

    def debug(self, message, *args):
        self._add("debug", message)


class TestCopyFailureIsLogged(unittest.TestCase):
    """审计 B-2：复制失败曾是 ``_execute_item`` 里唯一不落日志的失败出口。"""

    USF = USER_SHELL_FOLDERS_SUBKEY

    def test_copy_failure_writes_full_error_list_to_log(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")

            def failing_enumerate(root, skip_names=()):
                tree = fsops.SourceTree()
                for index in range(7):
                    tree.errors.append(f"注入的枚举失败 {index}")
                return tree

            logger = RecordingLogger()
            engine = TransferEngine(
                store=RegistryStore(
                    MemoryBackend({self.USF: {"Personal": (str(source), 1)}})
                ),
                logger=logger,
            )
            with mock.patch.object(fsops, "validate_destination", return_value=(True, "")):
                plan = engine.build_plan(
                    ["documents"], str(base / "new"), create_user_dir=False
                )

            with mock.patch.object(
                fsops, "try_atomic_move", return_value=(False, "强制复制路径")
            ), mock.patch.object(
                fsops, "enumerate_source", side_effect=failing_enumerate
            ):
                report = engine.execute(plan)

            outcome = report.outcomes[0]
            self.assertEqual(outcome.status, "failed")
            # UI 仍只截 3 条，但要说清总数
            self.assertIn("（共 6 项，以下仅列前 3 条）", outcome.message)
            # 日志里必须是**全量**：第 5 条原错误不在 UI 上，必须在日志里找到
            logged = [text for level, text in logger.records if level == "error"]
            self.assertTrue(logged, "复制失败没有落日志（B-2 回归）")
            joined = "\n".join(logged)
            self.assertIn("注入的枚举失败 4", joined, "日志未包含被 UI 截断掉的错误")
            self.assertIn("共 6 项", joined)


# --------------------------------------------------- 源禁区随系统盘动态化 (A1)

class TestSourceBanFollowsSystemDrive(unittest.TestCase):
    """审计 A1：源禁区名单必须随系统盘动态化（旧实现写死 ``C:\\``）。

    旧实现里 ``transfer`` 的 ``_RESTORE_SOURCE_FORBIDDEN_TREES/_EXACT`` 用字面
    ``C:\\Windows`` / ``C:\\Users`` 等，而 ``constants.FORBIDDEN_TARGET_ROOTS`` 已按
    ``%SystemDrive%`` 动态化（审计 A4）。后果：系统盘非 C: 的机器（改过盘符 / 精简
    装机）上，注册表 ``current`` 被改成系统盘上的 ``\\Windows`` 时，源侧闸门
    **fail-open**，会把整个系统目录搬走再删源。

    这里通过 monkeypatch ``constants._SYSTEM_DRIVE`` 模拟「系统盘 = D:」来驱动
    :func:`transfer._unsafe_source_tree`（名单在判定时现算，故即时生效）。
    """

    def test_non_c_system_drive_tree_is_rejected(self):
        """系统盘为 D: 时，D:\\Windows 下的源路径必须被判为系统目录。"""
        with mock.patch.object(constants, "_SYSTEM_DRIVE", "D:"):
            kind, reason = _unsafe_source_tree(r"D:\Windows\System32")
        self.assertEqual(kind, "system", f"源侧闸门 fail-open：{kind!r}/{reason!r}")
        self.assertIn("系统目录", reason)

    def test_non_c_system_drive_exact_users_is_rejected(self):
        """系统盘为 D: 时，源恰好是 D:\\Users 本身（精确匹配）也必须被拒。"""
        with mock.patch.object(constants, "_SYSTEM_DRIVE", "D:"):
            kind, _ = _unsafe_source_tree("D:\\Users")
        self.assertEqual(kind, "system")

    def test_non_c_system_drive_keeps_legit_custom_dir(self):
        """D:\\Users 之下的自定义目录仍须放行（\\Users 只做精确匹配，不拉整棵树）。"""
        with mock.patch.object(constants, "_SYSTEM_DRIVE", "D:"):
            kind, _ = _unsafe_source_tree(r"D:\Users\me\MyDocs")
        self.assertEqual(kind, "", "\\Users 下的用户自有目录被误拒")

    def test_c_system_drive_behavior_unchanged(self):
        """系统盘为 C: 时的既有口径不得改变（现有测试的基线）。"""
        self.assertEqual(_unsafe_source_tree(r"C:\Windows")[0], "system")
        self.assertEqual(_unsafe_source_tree(r"C:\Users")[0], "system")
        self.assertEqual(_unsafe_source_tree(r"C:\Users\me\MyDocs")[0], "")
        self.assertEqual(_unsafe_source_tree(r"D:\UserData\me")[0], "")


if __name__ == "__main__":
    unittest.main(verbosity=2)
