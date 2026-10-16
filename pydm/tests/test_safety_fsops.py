"""审计批次 A1-A11（数据安全 / 重解析点 / 8.3 短名 / 提权调用绝对路径）的针对性回归。

全部使用 ``tempfile`` 临时目录，**不触碰真实用户目录、不写真实注册表**。仅有的外部
探测都是只读的：``C:\\PROGRA~1`` 这个 8.3 短名是否存在、``System32`` 下可执行文件
是否存在。每个用例的 docstring 都写明「坏实现为什么会让它变红」。

运行：
    <python> -m unittest tests.test_safety_fsops -v
"""

from __future__ import annotations

import ctypes
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from docmover import constants, fsops, shell  # noqa: E402
from docmover.constants import FORBIDDEN_TARGET_ROOTS  # noqa: E402


# ------------------------------------------------------------------ 工具

def _realign_mtime(dst_file: Path, src_file: Path) -> None:
    """把 ``dst_file`` 的 mtime 对齐成与 ``src_file`` **完全相同**。

    这是 A1 用例的关键前提：只有「大小 + mtime 都对上」才需要内容比对来兜底。
    """
    info = os.stat(src_file)
    os.utime(dst_file, ns=(info.st_atime_ns, info.st_mtime_ns))


def _make_dir_alias(alias: str, real: str) -> bool:
    """造一个指向目录 ``real`` 的别名（优先 junction，退化用符号链接）。

    两者都失败（无权限 / 未开开发者模式）时返回 ``False``，由调用方 skipTest。
    """
    if os.path.isdir(alias):
        return True
    try:
        done = subprocess.run(
            ["cmd", "/c", "mklink", "/J", alias, real],
            capture_output=True,
            timeout=30,
        )
        if done.returncode == 0 and os.path.isdir(alias):
            return True
    except (OSError, subprocess.SubprocessError):
        pass
    try:
        os.symlink(real, alias, target_is_directory=True)
        return True
    except (OSError, NotImplementedError):
        return False


def _drop_dir_alias(alias: str) -> None:
    """只删链接本身（不跟随到目标目录去删内容）。"""
    try:
        if os.path.isdir(alias):
            os.rmdir(alias)
    except OSError:
        pass


# ------------------------------------------------- A1：复用必须比对内容

class TestReuseRequiresContentMatch(unittest.TestCase):
    """审计 A1：仅凭「大小 + mtime」判定"已复制"，删源后源内容会永久丢失。"""

    def test_same_size_and_mtime_but_different_content_is_not_reused(self):
        """坏实现会让本用例变红：旧代码在 ``same_size and same_time`` 时直接
        ``reused = True``，于是几乎没有 ``conflicts``、目标 ``report.txt`` 仍是
        旧的 ``BBBBB``，源的 ``AAAAA`` 在校验"通过"后被删掉。本用例断言
        ``conflicts`` 含该文件、且两份内容都还在。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            dst.mkdir()
            (src / "report.txt").write_bytes(b"AAAAA")
            (dst / "report.txt").write_bytes(b"BBBBB")     # 同为 5 字节、内容不同
            _realign_mtime(dst / "report.txt", src / "report.txt")
            self.assertEqual(
                os.stat(dst / "report.txt").st_mtime_ns,
                os.stat(src / "report.txt").st_mtime_ns,
            )

            result = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(result.ok, result.errors)
            self.assertIn("report.txt", result.conflicts, "内容不同的既有文件被当成副本")
            self.assertFalse(
                any(item.reused for item in result.items),
                "内容不同却判为复用 —— 删源即丢内容",
            )
            # 两份都保留：既有文件没被覆盖，源的另存为 (1)
            self.assertEqual((dst / "report.txt").read_bytes(), b"BBBBB")
            self.assertEqual((dst / "report (1).txt").read_bytes(), b"AAAAA")
            # 源未被触碰；两份都在，因此校验通过、删源是安全的
            self.assertEqual((src / "report.txt").read_bytes(), b"AAAAA")
            self.assertTrue(fsops.verify_copy(str(src), str(dst), result)[0])

    def test_identical_content_is_still_reused(self):
        """正向用例：内容确实一致时必须**仍然复用**（不能把合并优化改坏）。
        坏实现（一律另存 / 不复用）会让本用例变红。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            dst.mkdir()
            (src / "a.txt").write_bytes(b"IDENTICAL" * 8)
            (dst / "a.txt").write_bytes(b"IDENTICAL" * 8)
            _realign_mtime(dst / "a.txt", src / "a.txt")

            result = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(result.ok, result.errors)
            self.assertEqual(result.conflicts, [])
            self.assertEqual(len(result.items), 1)
            self.assertTrue(result.items[0].reused, "内容一致的文件没有被复用")
            self.assertTrue(
                result.items[0].fingerprint.digest,
                "复用项没有带上摘要 —— 校验会退化成只比大小+时间戳",
            )
            self.assertTrue(fsops.verify_copy(str(src), str(dst), result)[0])


# ------------------------------------------- A2：重解析点 / 盘符别名绕过

class TestReparsePointAlias(unittest.TestCase):
    """审计 A2：字符串不同但物理位置相同的别名，会让"删源"删掉唯一一份数据。"""

    def test_alias_pointing_at_source_is_rejected_by_validation(self):
        """坏实现会让本用例变红：旧 ``validate_destination`` 只做字符串比较，
        对指向源目录的 junction 返回 ``True``（放行）。"""
        with tempfile.TemporaryDirectory() as tmp:
            real = Path(tmp) / "real_docs"
            real.mkdir()
            (real / "keep.txt").write_text("IMPORTANT", encoding="utf-8")
            alias = str(Path(tmp) / "Docs")
            if not _make_dir_alias(alias, str(real)):
                self.skipTest("当前环境无法创建 junction / 符号链接（需要权限或开发者模式）")
            try:
                # 前提：字符串比较确实拦不住 —— 这正是缺陷的根源
                self.assertFalse(fsops.same_path(alias, str(real)))
                self.assertTrue(
                    fsops.resolves_to_same(alias, str(real)),
                    "别名未被识别为同一物理位置",
                )
                # 本机 TEMP 位于 <系统盘>\Users 下，会先被"禁止系统目录"拦下，
                # 故临时清空禁止根，专测"同一位置"这条闸门。
                with mock.patch.object(fsops, "FORBIDDEN_TARGET_ROOTS", ()):
                    ok, msg = fsops.validate_destination(alias, [str(real)])
                self.assertFalse(ok, f"指向源目录的别名被放行：{msg}")
                self.assertIn("同一位置", msg)
            finally:
                _drop_dir_alias(alias)

    def test_copy_merge_refuses_self_copy(self):
        """坏实现会让本用例变红：旧 ``copy_merge(p, p)`` 会把每个文件判为
        "目标已存在且内容一致"→ 复用 → ``ok=True``，调用方随后删掉源，而源就是
        唯一的那份数据。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            src.mkdir()
            (src / "a.txt").write_text("KEEP", encoding="utf-8")
            before = (src / "a.txt").read_bytes()

            result = fsops.copy_merge(str(src), str(src))
            self.assertFalse(result.ok)
            self.assertTrue(result.errors)
            self.assertTrue(
                any("同一位置" in item for item in result.errors), result.errors
            )
            self.assertEqual((src / "a.txt").read_bytes(), before)
            self.assertEqual(sorted(os.listdir(src)), ["a.txt"])

    def test_copy_merge_refuses_alias_of_source(self):
        """坏实现会让本用例变红：即使调用方漏了 ``validate_destination``，
        旧代码也会把"目标"（其实指向源）当成合并目标一路复制成功。"""
        with tempfile.TemporaryDirectory() as tmp:
            real = Path(tmp) / "real_docs"
            real.mkdir()
            (real / "keep.txt").write_text("IMPORTANT", encoding="utf-8")
            alias = str(Path(tmp) / "Docs")
            if not _make_dir_alias(alias, str(real)):
                self.skipTest("当前环境无法创建 junction / 符号链接（需要权限或开发者模式）")
            try:
                result = fsops.copy_merge(str(real), alias)
                self.assertFalse(result.ok, "源与其别名之间的自我复制未被拒绝")
                self.assertTrue(
                    any("同一位置" in item for item in result.errors), result.errors
                )
                self.assertTrue((real / "keep.txt").exists(), "源数据被破坏")
            finally:
                _drop_dir_alias(alias)


# ------------------------------------------- F1：目标侧重解析点零检测
#
# 三层既有闸门只比较「目标根 vs 源」，从不看目标自身或其祖先是不是 junction。
# 而 ``os.makedirs(exist_ok=True)`` 对已存在的 junction 静默成功、``open(dst,"wb")``
# 与校验侧的 ``isdir`` 都会**跟随**它，于是攻击者只需一条 ``mklink /J``（**不需要
# 管理员**）就能让本工具把数据写进任意目录、写注册表、删源，界面还报"转移成功"。

class TestDestinationReparsePoint(unittest.TestCase):
    """审计 F1：目标自身 / 目标的任一上级目录是链接时必须拒绝。"""

    def setUp(self):
        # 临时目录位于 <系统盘>\Users 下，会先被「禁止搬到系统目录」拦下，
        # 故先放开；那条规则由 TestShortNameGate / test_core 单独覆盖。
        patcher = mock.patch.object(fsops, "FORBIDDEN_TARGET_ROOTS", ())
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_junction_as_target_root_is_rejected(self):
        """目标根本身是 junction → 拒绝。"""
        with tempfile.TemporaryDirectory() as tmp:
            real = Path(tmp) / "victim_root"
            real.mkdir()
            (real / "existing.txt").write_text("VICTIM", encoding="utf-8")
            alias = str(Path(tmp) / "Docs")
            if not _make_dir_alias(alias, str(real)):
                self.skipTest("当前环境无法创建 junction / 符号链接")
            try:
                self.assertTrue(fsops.is_reparse_point(alias), "前提：别名未建成")
                ok, msg = fsops.validate_destination(alias, [])
                self.assertFalse(ok, f"junction 目标被放行：{msg}")
                self.assertIn("联接点", msg)
            finally:
                _drop_dir_alias(alias)

    def test_junction_ancestor_is_rejected(self):
        """目标自身还不存在、但**上级目录**是 junction → 拒绝。

        这一条是原漏洞的核心：目标那一级由工具新建，看似"干净"，可它父亲是个
        联接点，于是所有后续写入都会落到链接指向的位置。
        """
        with tempfile.TemporaryDirectory() as tmp:
            real = Path(tmp) / "victim_root"
            real.mkdir()
            alias = str(Path(tmp) / "UserData")
            if not _make_dir_alias(alias, str(real)):
                self.skipTest("当前环境无法创建 junction / 符号链接")
            try:
                target = str(Path(alias) / "me" / "Documents")   # 末端不存在
                self.assertFalse(os.path.exists(target))
                ok, msg = fsops.validate_destination(target, [])
                self.assertFalse(ok, f"上级是联接点的目标被放行：{msg}")
                self.assertIn("联接点", msg)
            finally:
                _drop_dir_alias(alias)

    def test_normal_new_target_still_accepted(self):
        """回归：正常新建目录（各级都不存在、都不是链接）必须照旧放行。"""
        with tempfile.TemporaryDirectory() as tmp:
            target = str(Path(tmp) / "UserData" / "me" / "Documents")
            ok, msg = fsops.validate_destination(target, [])
            self.assertTrue(ok, f"正常目标被误伤：{msg}")

    def test_existing_plain_subdirectory_is_accepted(self):
        """回归：目标已存在且是**普通目录**（合并语义的日常场景）也照旧放行。"""
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "UserData"
            (target / "already").mkdir(parents=True)
            (target / "already" / "keep.txt").write_text("USER", encoding="utf-8")
            ok, msg = fsops.validate_destination(str(target), [])
            self.assertTrue(ok, f"普通既有目录被误伤：{msg}")

    def test_copy_merge_refuses_junction_subdirectory(self):
        """执行期第二道：目标**子目录**已存在且是 junction → 整体失败、不得写入。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            (src / "sub").mkdir(parents=True)
            (src / "sub" / "a.txt").write_bytes(b"SECRET" * 100)
            dst = Path(tmp) / "dst"
            dst.mkdir()
            victim = Path(tmp) / "victim"
            victim.mkdir()
            alias = str(dst / "sub")
            if not _make_dir_alias(alias, str(victim)):
                self.skipTest("当前环境无法创建 junction / 符号链接")
            try:
                result = fsops.copy_merge(str(src), str(dst))
                self.assertFalse(result.ok, "目标子目录是联接点却被放行")
                self.assertTrue(
                    any("联接点" in item for item in result.errors), result.errors
                )
                # 关键：数据一个字节都没落进链接指向的目录
                self.assertEqual(list(victim.iterdir()), [], "数据被写进了联接目标")
                self.assertEqual((src / "sub" / "a.txt").read_bytes(), b"SECRET" * 100)
            finally:
                _drop_dir_alias(alias)

    def test_copy_merge_refuses_junction_file_target(self):
        """目标位置已存在且是符号链接文件 → 整体失败，链接目标内容不被改写。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            src.mkdir()
            (src / "a.txt").write_bytes(b"NEW" * 100)
            dst = Path(tmp) / "dst"
            dst.mkdir()
            victim = dst / "victim.txt"
            victim.write_bytes(b"OLD" * 100)
            alias = str(dst / "a.txt")
            try:
                os.symlink(str(victim), alias)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f"当前环境不允许创建符号链接：{exc}")
            try:
                self.assertTrue(fsops.is_reparse_point(alias))
                result = fsops.copy_merge(str(src), str(dst))
                self.assertFalse(result.ok, "目标文件是符号链接却被放行")
                self.assertTrue(
                    any("联接点" in item for item in result.errors), result.errors
                )
                self.assertEqual(victim.read_bytes(), b"OLD" * 100, "链接目标被改写")
            finally:
                try:
                    os.unlink(alias)
                except OSError:
                    pass

    def test_copy_merge_refuses_junction_as_dst_root(self):
        """执行期第一道：目标根**自身**是 junction → 在任何写入前拒绝。

        坏实现：把 :func:`fsops.copy_merge` 的 ``blocked = reparse_ancestor(dst)``
        整段删掉（或退回只看 ``is_reparse_point(dst)`` 却漏传 dst）——目标根是链接
        时 ``os.makedirs(exist_ok=True)`` 静默成功、随后每个文件都写进链接目标，
        本用例会变红。现有 ``test_junction_as_target_root_is_rejected`` 只调用了
        ``validate_destination``，**根本没走到 copy_merge**，故不能替代本条。
        """
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            src.mkdir()
            (src / "a.txt").write_bytes(b"SECRET" * 100)
            victim = Path(tmp) / "victim"
            victim.mkdir()
            alias = str(Path(tmp) / "Docs")
            if not _make_dir_alias(alias, str(victim)):
                self.skipTest("当前环境无法创建 junction / 符号链接")
            try:
                self.assertTrue(fsops.is_reparse_point(alias), "前提：别名未建成")
                result = fsops.copy_merge(str(src), alias)
                self.assertFalse(result.ok, "目标根是联接点却被放行")
                self.assertTrue(
                    any("联接点" in item for item in result.errors), result.errors
                )
                self.assertEqual(list(victim.iterdir()), [], "数据漏进了链接目标")
                self.assertEqual((src / "a.txt").read_bytes(), b"SECRET" * 100)
            finally:
                _drop_dir_alias(alias)

    def test_copy_merge_refuses_junction_ancestor_of_dst(self):
        """执行期第一道（最终审计 P1-1 的核心）：目标那一级**还不存在**，但它的
        **上级目录**是 junction → 必须在任何写入前拒绝。

        这正是规划 → 执行之间时间窗被利用的形态：``validate_destination`` 规划期
        早已通过，攻击者随后把目标某一级上级换成联接点。旧 ``copy_merge`` 只查
        ``is_reparse_point(dst)``，而 dst 尚不存在 → 返回 False，闸门形同虚设。

        坏实现：把 ``blocked = reparse_ancestor(dst)`` 退回 ``if is_reparse_point(dst):``
        → 本用例变红（数据会落进 victim、dst 被建出来）。
        """
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            src.mkdir()
            (src / "a.txt").write_bytes(b"SECRET" * 100)
            victim = Path(tmp) / "victim"
            victim.mkdir()
            alias = str(Path(tmp) / "UserData")
            if not _make_dir_alias(alias, str(victim)):
                self.skipTest("当前环境无法创建 junction / 符号链接")
            try:
                dst = str(Path(alias) / "me" / "Documents")   # 末端不存在
                self.assertFalse(os.path.exists(dst), "前提：目标末端应不存在")
                self.assertFalse(fsops.is_reparse_point(dst), "前提：dst 自身不是链接")
                result = fsops.copy_merge(str(src), dst)
                self.assertFalse(result.ok, "上级是联接点却被放行")
                self.assertTrue(
                    any("联接点" in item for item in result.errors), result.errors
                )
                self.assertEqual(list(victim.iterdir()), [], "数据漏进了链接指向的目录")
                self.assertEqual((src / "a.txt").read_bytes(), b"SECRET" * 100, "源被改动")
                self.assertFalse(os.path.exists(dst), "目标被建了出来")
            finally:
                _drop_dir_alias(alias)

    def test_try_atomic_move_refuses_junction_ancestor(self):
        """执行期闸门（最终审计 P1-1）：目标不存在但上级是 junction → 原子搬移必须
        拒绝，否则 ``os.rename`` 会把整棵源目录搬进链接目标。

        坏实现：删掉 :func:`fsops.try_atomic_move` 开头的 ``blocked = reparse_ancestor(dst)``
        → ``os.path.exists(dst)`` 因末端不存在而为 False，搬移照做，本用例变红。
        """
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            (src / "sub").mkdir(parents=True)
            (src / "sub" / "a.txt").write_bytes(b"DATA")
            victim = Path(tmp) / "victim"
            victim.mkdir()
            alias = str(Path(tmp) / "UserData")
            if not _make_dir_alias(alias, str(victim)):
                self.skipTest("当前环境无法创建 junction / 符号链接")
            try:
                dst = str(Path(alias) / "Documents")
                ok, msg = fsops.try_atomic_move(str(src), dst)
                self.assertFalse(ok, f"上级是联接点却允许原子搬移：{msg}")
                self.assertIn("联接点", msg)
                self.assertTrue(src.is_dir(), "源目录被搬走了")
                self.assertTrue((src / "sub" / "a.txt").exists(), "源内容丢失")
                self.assertEqual(list(victim.iterdir()), [], "数据漏进了链接目标")
            finally:
                _drop_dir_alias(alias)

    def test_copy_merge_still_works_over_plain_structure(self):
        """回归：普通目标（含同级既有目录）照旧能复制并通过校验。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            (src / "sub").mkdir(parents=True)
            (src / "sub" / "a.txt").write_bytes(b"PLAIN" * 100)
            (src / "keep.txt").write_bytes(b"TOP")
            dst = Path(tmp) / "dst"
            (dst / "sub").mkdir(parents=True)
            (dst / "sub" / "old.txt").write_bytes(b"OLD")
            result = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(result.ok, result.errors)
            self.assertEqual((dst / "sub" / "a.txt").read_bytes(), b"PLAIN" * 100)
            self.assertEqual((dst / "keep.txt").read_bytes(), b"TOP")
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertTrue(ok, detail)


# ------------------------------------------------- A3：8.3 短名绕过闸门

class TestShortNameGate(unittest.TestCase):
    """审计 A3：``C:\\PROGRA~1`` 与 ``C:\\Program Files`` 是同一目录。"""

    def test_long_path_restores_existing_prefix(self):
        """纯函数：已存在的前缀被还原成长名，不存在的尾部原样拼回。
        坏实现（``long_path`` 直接 return path）会让本用例变红。"""
        if not os.path.isdir(r"C:\PROGRA~1"):
            self.skipTest("本机没有 C:\\PROGRA~1 短名（8.3 名称生成被禁用）")
        self.assertEqual(fsops.long_path(r"C:\PROGRA~1").lower(), r"c:\program files")
        self.assertEqual(
            fsops.long_path(r"C:\PROGRA~1\no_such_sub").lower(),
            r"c:\program files\no_such_sub",
        )
        # 整条都不存在时原样返回（逐级回退到盘根，拼回不存在的尾部）
        self.assertEqual(
            fsops.long_path(r"C:\no_such_a\no_such_b"), r"C:\no_such_a\no_such_b"
        )

    def test_short_name_system_root_is_rejected(self):
        """坏实现会让本用例变红：旧代码对 ``C:\\PROGRA~1\\SomeFolder`` 返回
        ``True``（放行），用户即可把用户文件夹搬进 Program Files。"""
        if not os.path.isdir(r"C:\PROGRA~1"):
            self.skipTest("本机没有 C:\\PROGRA~1 短名（8.3 名称生成被禁用）")
        ok, msg = fsops.validate_destination(r"C:\PROGRA~1\SomeFolder", [])
        self.assertFalse(ok, "8.3 短名绕过了系统目录闸门")
        self.assertIn("系统目录", msg)


# ------------------------------------------ A4：禁止根跟随系统盘

class TestForbiddenRootsFollowDrive(unittest.TestCase):

    def test_forbidden_roots_use_system_drive(self):
        """坏实现会让本用例变红：写死 ``C:`` 的实现里没有 ``_SYSTEM_DRIVE``，
        且条目不会等于环境里的 SystemDrive（在系统盘不是 C: 的机器上整道守卫失效）。"""
        drive = os.environ.get("SystemDrive", "C:").rstrip("\\")
        self.assertEqual(constants._SYSTEM_DRIVE, drive)
        self.assertTrue(FORBIDDEN_TARGET_ROOTS, "禁止根清单为空")
        for root in FORBIDDEN_TARGET_ROOTS:
            self.assertTrue(root.startswith(drive), f"{root} 未跟随 SystemDrive {drive}")


# --------------------------------- A5：跳过项不得让源目录被删

class TestSkippedEntriesBlockVerification(unittest.TestCase):

    def test_skipped_entries_block_verification(self):
        """坏实现会让本用例变红：旧 ``verify_copy`` 完全不看 ``result.skipped``，
        校验返回 ``True``，调用方随后连同源目录一起删掉被跳过的文件。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "keep.txt").write_text("KEEP", encoding="utf-8")
            (src / "desktop.ini").write_text("CONFIG", encoding="utf-8")

            result = fsops.copy_merge(str(src), str(dst), skip_names=["desktop.ini"])
            self.assertTrue(result.ok, result.errors)
            self.assertIn("desktop.ini", result.skipped)
            self.assertFalse((dst / "desktop.ini").exists())

            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertFalse(ok, "被跳过的文件没复制却仍通过了删源校验")
            self.assertIn("跳过", detail)
            self.assertTrue((src / "desktop.ini").exists(), "源目录不应被删除")


# --------------------------------- A6：悬空符号链接不得让校验恒失败

class TestDanglingSymlink(unittest.TestCase):

    def test_dangling_symlink_does_not_block_verification(self):
        """坏实现会让本用例变红：``os.path.exists`` 会跟随链接，悬空链接在目标
        侧被重建后仍被判为"缺失"，该文件夹永远报"校验未通过"、永远搬不完。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "real.txt").write_text("x", encoding="utf-8")
            try:
                os.symlink(str(src / "no_such_target.txt"), str(src / "dangling.lnk"))
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f"当前环境不允许创建符号链接：{exc}")

            result = fsops.copy_merge(str(src), str(dst))
            if not result.ok:
                # 环境不允许重建链接（无权限等）→ 本用例无法验证该修复
                self.assertTrue(
                    any("链接" in item for item in result.errors), result.errors
                )
                self.skipTest("本环境无法重建链接，跳过")

            self.assertIn("dangling.lnk", result.links)
            self.assertTrue(
                os.path.lexists(str(dst / "dangling.lnk")), "目标侧链接未重建"
            )
            self.assertFalse(
                os.path.exists(str(dst / "dangling.lnk")),
                "前提失效：该链接不再是悬空的，本用例失去意义",
            )
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertTrue(ok, detail)


# -------------------------------------- A7：st_ino=0 时不得做硬链接复用

class TestHardlinkInodeKey(unittest.TestCase):

    def test_zero_ino_disables_hardlink_reuse(self):
        """坏实现会让本用例变红：``st_ino`` 恒为 0 的部分卷（SMB/exFAT）上，
        两个无关文件共用 ``(dev, 0)`` 键，第二个会被 ``os.link`` 成第一个的内容
        （``b.bin`` 变成 ``AAAA``）。本用例用假 stat 伪造该退化。"""
        real_stat = os.stat

        class _FakeHardlinkStat:
            """只覆盖 st_nlink / st_ino，其余字段透传真实 stat 结果。"""

            def __init__(self, real):
                self._real = real
                self.st_nlink = 2
                self.st_ino = 0

            def __getattr__(self, name):
                return getattr(self._real, name)

        def fake_stat(path, *args, **kwargs):
            result = real_stat(path, *args, **kwargs)
            try:
                name = os.path.basename(str(path))
            except (TypeError, ValueError):
                name = ""
            if name in ("a.bin", "b.bin"):
                return _FakeHardlinkStat(result)
            return result

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "a.bin").write_bytes(b"AAAA")
            (src / "b.bin").write_bytes(b"BBBB")

            with mock.patch.object(fsops.os, "stat", fake_stat):
                result = fsops.copy_merge(str(src), str(dst))

            self.assertTrue(result.ok, result.errors)
            self.assertEqual(
                result.hardlinked_files, 0, "st_ino=0 时不应做硬链接复用"
            )
            self.assertEqual((dst / "a.bin").read_bytes(), b"AAAA")
            self.assertEqual((dst / "b.bin").read_bytes(), b"BBBB")


# ------------------------------------------- A8：上标保留名

class TestSuperscriptReservedNames(unittest.TestCase):

    def test_superscript_reserved_names_are_rejected(self):
        """坏实现会让本用例变红：只列 ASCII 1-9 时 ``COM¹`` / ``LPT²`` 会通过
        前置校验，而按 Microsoft「Naming Files, Paths, and Namespaces」的规则，
        ``COM¹``/``COM²``/``COM³``（上标数字）与 ``COM1``/``COM2``/``COM3`` 等价，
        属设备保留名 —— 放行等于把一个"看似合法、实际用不了"的目标交给后续创建。

        口径说明（待确认项）：上标变体的等价关系来自官方文档，本机无法复验 ——
        当前沙箱会虚拟化文件系统写入（对照组 ``CON``/``COM1`` 这两个**必然**保留的
        名字在这里也报"创建成功"）。故本用例只断言 ``validate_destination`` 这一
        确定性契约（把上标变体一并拒绝，属 fail-closed 的保守取向），不对 OS 行为下结论。
        """
        for bad in ("COM\u00b9", "LPT\u00b2", "com\u00b3.txt", "com\u2074", "lpt\u2079"):
            ok, msg = fsops.validate_destination("D:\\UserData\\" + bad, [])
            self.assertFalse(ok, f"{bad} 未被拒绝")
            self.assertIn("保留名", msg)


# -------------------------------------- A9：提权进程调用系统程序用绝对路径

class TestSystemExecutablePaths(unittest.TestCase):

    def test_system32_returns_absolute_existing_path(self):
        """坏实现会让本用例变红：``["cmd", ...]`` 之类相对名会被 CreateProcess
        的"当前目录优先"搜索命中，提权进程可能执行到投放的同名 exe。"""
        for name in ("cmd.exe", "tasklist.exe", "taskkill.exe"):
            path = fsops.system32(name)
            self.assertTrue(os.path.isabs(path), path)
            self.assertTrue(os.path.exists(path), f"{name} 不在 {path}")
        # explorer.exe 装在 %SystemRoot% 而不是 System32，helper 必须能退回
        explorer = fsops.system32("explorer.exe")
        self.assertTrue(os.path.isabs(explorer), explorer)
        self.assertTrue(os.path.exists(explorer), explorer)


# ------------------------------------------- A10：广播结果如实上报

@unittest.skipUnless(sys.platform == "win32", "仅 Windows 有 SendMessageTimeoutW")
class TestBroadcastReportsTruthfully(unittest.TestCase):

    def test_no_response_is_reported_as_failure(self):
        """坏实现会让本用例变红：旧代码无视返回值，超时/无响应时也报"已广播"。"""
        with mock.patch.object(
            ctypes.windll.user32, "SendMessageTimeoutW", return_value=0
        ):
            ok, msg = shell.broadcast_environment_change()
        self.assertFalse(ok, msg)
        self.assertIn("未确认送达", msg)

    def test_delivered_is_reported_as_success(self):
        with mock.patch.object(
            ctypes.windll.user32, "SendMessageTimeoutW", return_value=1
        ):
            ok, msg = shell.broadcast_environment_change()
        self.assertTrue(ok, msg)


# -------------------------------- A11：Desktop.ini 的文件夹属性应为 READONLY

class TestDesktopIniFolderAttribute(unittest.TestCase):

    def test_folder_gets_readonly_attribute(self):
        """坏实现会让本用例变红：旧代码给文件夹设的是 SYSTEM(0x04) 而非
        READONLY(0x01)，资源管理器很可能不认 ``LocalizedResourceName``。"""
        tmp = tempfile.mkdtemp(prefix="dm_desktopini_")
        try:
            ok, msg = shell.write_desktop_ini(tmp, "我的文档")
            self.assertTrue(ok, msg)
            self.assertTrue((Path(tmp) / "Desktop.ini").exists())
            attrs = os.stat(tmp).st_file_attributes
            self.assertTrue(attrs & 0x01, f"文件夹未设置 READONLY 属性：0x{attrs:x}")
        finally:
            try:
                os.chmod(tmp, 0o700)          # 清掉 READONLY，保证能删干净
            except OSError:
                pass
            shutil.rmtree(tmp, ignore_errors=True)


# ------------------------------- P3-5：enumerate_source 入口显式去长路径前缀

class TestEnumerateSourceLongPrefix(unittest.TestCase):
    """最终审计 P3-5：调用方若传入带 ``\\\\?\\`` 前缀的根路径，入口须先 ``_unlp``。

    坏实现（入口不做 ``root = _unlp(root)``）：遍历里 ``_unlp(current)`` 得到的是
    不带前缀的逻辑路径，而 ``root`` 仍带前缀，``os.path.relpath(full, root)`` 会在
    Windows 上抛 ``ValueError: path is on mount 'D:', start on mount '\\\\?\\D:'``。
    """

    def test_long_prefix_root_is_accepted(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            (src / "sub").mkdir(parents=True)
            (src / "sub" / "a.txt").write_bytes(b"X")
            (src / "top.txt").write_bytes(b"Y")
            # 显式带 ``\\?\`` 前缀（临时目录短，_lp 不会自动加，故手工拼）
            rooted = "\\\\?\\" + os.path.abspath(str(src))
            tree = fsops.enumerate_source(rooted)
            self.assertEqual(tree.errors, [], tree.errors)
            self.assertIn("sub", tree.dirs)
            self.assertIn(os.path.join("sub", "a.txt"), tree.files)
            self.assertIn("top.txt", tree.files)


if __name__ == "__main__":
    unittest.main()
