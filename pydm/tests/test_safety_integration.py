"""A1 / A2 两条 P1 的**端到端**回归：用真实 ``TransferEngine`` 跑完整链路
（``build_plan`` → ``execute`` = 复制 → 校验 → 写注册表 → 删源）。

为什么还要单独建这个文件（``test_safety_fsops.py`` 已覆盖同两条 P1）：
``test_safety_fsops.py`` 直接调用 ``fsops.copy_merge`` / ``validate_destination``，
验证的是「单元级」契约；但真正决定用户数据生死的，是 ``TransferEngine`` 把
各步骤**串起来**之后的整体行为——删源那一刀是否落下、注册表是否被写坏。
本文件全部通过引擎的公开入口（``build_plan`` / ``execute``）驱动，断言最终落盘
的**物理结果**（源目录还在不在、目标里有哪些字节），而不是内部计数器。

全部使用 ``tempfile`` 临时目录 + 内存注册表后端，**不触碰真实用户目录与注册表**。

运行：
    <python> -m unittest tests.test_safety_integration -v
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from docmover import fsops  # noqa: E402
from docmover.constants import USER_SHELL_FOLDERS_SUBKEY  # noqa: E402
from docmover.registry import MemoryBackend, RegistryStore  # noqa: E402
from docmover.transfer import ST_FAILED, ST_OK, TransferEngine  # noqa: E402


# ------------------------------------------------------------------ 工具

def _realign_mtime(dst_file: Path, src_file: Path) -> None:
    """把 ``dst_file`` 的 mtime 对齐成与 ``src_file`` 完全相同（A1 用例的前提）。"""
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


def _bytes_under(root: Path):
    """当前 ``root`` 下所有文件的字节内容（用于断言「内容是否还在」）。"""
    found = []
    for path in sorted(root.rglob("*")):
        if path.is_file() and not path.is_symlink():
            found.append(path.read_bytes())
    return found


class _EngineTestCase(unittest.TestCase):
    """公共装配：临时目录位于 ``<系统盘>\\Users\\...`` 下，会先被「禁止系统目录」
    这条规则拦下，故显式放开它——该规则本身由 ``TestValidateDestination`` /
    ``test_safety_fsops`` 单独覆盖。"""

    def setUp(self):
        patcher = mock.patch.object(fsops, "FORBIDDEN_TARGET_ROOTS", ())
        patcher.start()
        self.addCleanup(patcher.stop)

    @staticmethod
    def _engine(registry_seed=None, **kwargs):
        backend = MemoryBackend(registry_seed or {})
        store = RegistryStore(backend)
        engine = TransferEngine(store=store, **kwargs)
        return engine, backend


# ---------------------------------- A1 端到端：删源后源内容必须还能在目标找到

class TestA1ReuseGuardEndToEnd(_EngineTestCase):
    """审计 A1 端到端：目标已有「同名 + 同大小 + mtime 对齐」但**内容不同**的文件时，
    引擎必须把源内容另存保留，删源之后源的内容仍能在目标里找回。

    与单元级的差别：这里断言的是**引擎跑完整链路之后**的落盘结果。坏实现
    （``copy_merge`` 仅凭大小 + mtime 判「已复制」）会让 ``verify_copy`` 也
    跟着退化（复用项摘要为空 → 退化成只比大小 + mtime），于是校验"通过"、
    引擎删掉源目录，而目标里那份是**用户原有的另一份内容**——源内容永久丢失，
    本用例断言「目标中存在源内容」随即变红。
    """

    def test_source_content_survives_after_source_removal(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            src_bytes = b"SOURCE-VERSION"          # 14 字节
            (source / "report.txt").write_bytes(src_bytes)

            dest_root = base / "new"
            target = dest_root / "文档"
            target.mkdir(parents=True)
            tgt_bytes = b"PREEXISTINGXXX"          # 同为 14 字节、内容不同
            (target / "report.txt").write_bytes(tgt_bytes)
            _realign_mtime(target / "report.txt", source / "report.txt")
            self.assertEqual(
                os.stat(target / "report.txt").st_mtime_ns,
                os.stat(source / "report.txt").st_mtime_ns,
                "前提失效：两侧 mtime 未对齐",
            )

            engine, backend = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}},
                backup_dir=base / "backup",
            )
            plan = engine.build_plan(["documents"], str(dest_root), create_user_dir=False)
            self.assertEqual(len(plan), 1)
            self.assertFalse(plan[0].same)

            report = engine.execute(plan)
            outcome = report.outcomes[0]
            self.assertEqual(outcome.status, ST_OK, outcome.message)

            contents = _bytes_under(target)
            # ① 关键安全属性：源内容在目标里**确实存在**（删源之后仍能找回）
            self.assertIn(
                src_bytes,
                contents,
                "源内容在目标里找不到 —— 删源后即永久丢失（A1 回归）",
            )
            # ② 用户原有的那份内容也没被覆盖
            self.assertIn(tgt_bytes, contents, "目标原有内容被覆盖")
            # ③ 两份并存，而不是二选一
            self.assertEqual(len(contents), 2, contents)

            # ④ 引擎确实完成了「删源」这一步（这是"删源后还能找回"的前提）。
            #    注意：安全修复的正确取向是**保留两份内容后正常完成转移**，
            #    因此源目录会被删除；被保住的是源文件的字节，而不是源目录本身。
            self.assertFalse(source.exists(), "源目录未被清理，转移未完成")
            # ⑤ 注册表已指向新的用户文件夹
            moved = backend.read_value(USER_SHELL_FOLDERS_SUBKEY, "Personal")[0]
            self.assertEqual(moved, str(target))


# --------------------------- A2 端到端（第一层）：规划期就拒绝「同一物理位置」

class TestA2AliasRejectedAtPlanning(_EngineTestCase):
    """审计 A2 第一层端到端：目标目录本身就是指向源目录的 junction / 符号链接时，
    ``build_plan`` 里的 ``validate_destination``（其内部用 ``resolves_to_same``）
    必须直接判该项不可执行；``execute`` 随即失败，且**绝不调用删源**。

    坏实现（``validate_destination`` 只做字符串比较 / ``resolves_to_same`` 退化成
    ``same_path``）会让规划放行：目标（别名）与源字符串不同但物理同一，复制时
    每个文件都"已存在且内容一致"→ 全部复用 → 校验通过 → 写注册表 → 删源。
    源目录被删、别名指向悬空路径，用户唯一那份数据丢失 —— 本用例的「源内容完好」
    与「删源未被调用」两条断言随即变红。
    """

    def test_alias_target_is_rejected_and_source_untouched(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "keep.txt").write_bytes(b"IMPORTANT")

            dest_root = base / "new"
            dest_root.mkdir()
            alias = str(dest_root / "文档")
            if not _make_dir_alias(alias, str(source)):
                self.skipTest("当前环境无法创建 junction / 符号链接（需要权限或开发者模式）")
            try:
                # 前提：字符串比较拦不住，物理位置却相同 —— 这正是缺陷根源
                self.assertFalse(fsops.same_path(alias, str(source)))
                self.assertTrue(fsops.resolves_to_same(alias, str(source)))

                engine, backend = self._engine(
                    {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}},
                    backup_dir=base / "backup",
                )
                plan = engine.build_plan(["documents"], str(dest_root), create_user_dir=False)
                item = plan[0]
                self.assertFalse(item.same)
                self.assertIn("同一位置", item.invalid_reason,
                              f"规划期未拦截别名目标：{item.invalid_reason!r}")
                self.assertTrue(item.blocking_reason(), "受阻项却无阻断原因")

                with mock.patch.object(
                    fsops, "safe_remove_directory", wraps=fsops.safe_remove_directory
                ) as spy:
                    report = engine.execute(plan)

                outcome = report.outcomes[0]
                self.assertEqual(outcome.status, ST_FAILED, outcome.message)
                self.assertIn("同一位置", outcome.message)
                # 第一层命中：执行阶段根本没走到复制，删源更不可能被调用
                self.assertEqual(spy.call_count, 0, "别名场景下竟然调用了删源")
                self.assertEqual(backend.writes, [], "被拒绝的项不应写入注册表")
                # 源数据完好
                self.assertTrue((source / "keep.txt").exists())
                self.assertEqual((source / "keep.txt").read_bytes(), b"IMPORTANT")
            finally:
                _drop_dir_alias(alias)


# -------------------- A2 端到端（第二层）：规划后目标被换成别名 → copy_merge 兜底

class TestA2AliasGuardInCopyMerge(_EngineTestCase):
    """审计 A2 第二层端到端：规划时目标还是普通空目录（``validate_destination``
    合法放行），规划完成后、执行前目标被换成指向源目录的别名——即典型的 TOCTOU。
    此时第一层（规划期）已无从拦截，执行期闸门成为最后防线：**任何写操作之前**拒绝。

    命中层说明（P1-1 后）：执行结果为 ``ST_FAILED``，实际由**执行期最早**的那道闸门
    命中——最终审计 P1-1 在 ``transfer._execute_item`` 里、``ensure_directory`` 之前
    新增的 ``fsops.reparse_ancestor(item.target)`` 复查（目标此刻已是 junction）。
    在 P1-1 之前，本场景由 ``copy_merge`` 开头的 ``resolves_to_same`` 自保护命中
    （消息「源与目标指向同一位置…」）。两者同源、互为备份，故消息**两文案都接受**。

    坏实现会让本用例红：把 P1-1 的执行期闸门**与** ``copy_merge`` 的
    ``resolves_to_same``/``reparse_ancestor`` 自保护**同时**退化时，别名会被当合并
    目标，每个文件命中"已存在且内容一致"→ 复用 → 校验通过 → 写注册表 → 删源。
    本用例断言「执行失败」「删源未被调用」「源内容完好」「注册表未被写」四条。
    """

    def test_target_swapped_to_alias_after_planning_is_refused_by_copy_merge(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "keep.txt").write_bytes(b"IMPORTANT")

            dest_root = base / "new"
            target_dir = dest_root / "文档"
            target_dir.mkdir(parents=True)          # 规划期：普通空目录（合法）

            engine, backend = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}},
                backup_dir=base / "backup",
            )
            plan = engine.build_plan(["documents"], str(dest_root), create_user_dir=False)
            item = plan[0]
            # 前置：规划期确实放行了（否则本用例测不到 copy_merge 这一层）
            self.assertEqual(item.invalid_reason, "")
            self.assertEqual(item.blocking_reason(), "")

            # TOCTOU：规划之后、执行之前，把目标换成指向源的 junction
            os.rmdir(target_dir)
            if not _make_dir_alias(str(target_dir), str(source)):
                self.skipTest("当前环境无法创建 junction / 符号链接（需要权限或开发者模式）")
            try:
                with mock.patch.object(
                    fsops, "safe_remove_directory", wraps=fsops.safe_remove_directory
                ) as spy:
                    report = engine.execute(plan)

                outcome = report.outcomes[0]
                self.assertEqual(outcome.status, ST_FAILED, outcome.message)
                self.assertTrue(
                    ("同一位置" in outcome.message) or ("联接点" in outcome.message),
                    f"拒绝原因表述异常：{outcome.message}",
                )
                self.assertEqual(spy.call_count, 0, "别名场景下竟然调用了删源")
                self.assertEqual(backend.writes, [], "被拒绝的项不应写入注册表")
                # 源内容完好（别名指向它，绝不能把它删掉）
                self.assertTrue((source / "keep.txt").exists())
                self.assertEqual((source / "keep.txt").read_bytes(), b"IMPORTANT")
            finally:
                _drop_dir_alias(str(target_dir))


# ------- A2 端到端（第三层）：校验通过后目标才变别名 → 删源前闸门兜底

class TestA2DeleteGateInTransfer(_EngineTestCase):
    """审计 A2 **第三层**端到端：删源之前 ``transfer._execute_item`` 那道
    ``fsops.resolves_to_same(item.current, item.target)`` 闸门。

    与上面两条 A2 用例（第 1 层规划期、第 2 层 ``copy_merge`` 自保护）**刻意用完全
    不同的构造方式**，免得三份覆盖在同一处失效还一起变绿。

    本用例的场景：目标自始至终是**普通空目录**——
      * 规划期 ``validate_destination`` 合法放行（第 1 层不该也不会提前拦）；
      * ``copy_merge`` 开头的自保护此刻判定"不是同一位置"（第 2 层不命中）；
      * 复制、校验正常完成，注册表也已写入；
      * 直到"校验通过之后"这个窗口目标才"变成"指向源的联结点 —— 由删源前那道
        闸门兜底拦下。

    为了让"变真"精确发生在**校验通过这一刻**（而不是靠 ``side_effect`` 的调用次数，
    那种写法很脆），这里用一个**可变标志位**：包装 ``verify_copy``，在它返回**之后**
    翻转标志；``resolves_to_same`` 在标志未翻转时按真实判定、翻转后恒真。
    刻意**不在磁盘上真建 junction**：真联结点会让"复制"的物理结果不可控（目标指向
    源时复制会全部命中复用）——那正是这条闸门要防的形态，而不是本用例要构造的形态。

    坏实现会让本用例变红：把 ``transfer.py`` 里那道闸门整块去掉（或让
    ``resolves_to_same`` 退化），删源就会执行——而目标其实是源的别名，
    ``shutil.rmtree`` 删掉的正是用户唯一那份数据。本用例的
    「``safe_remove_directory`` 调用次数为 0」「源目录与文件完好」两条断言即变红。

    断言子串用第 3 层**专属**文案「源与目标实际指向同一位置」：
    ``copy_merge`` 自保护的文案是「源与目标**指向**同一位置，已拒绝执行」，两者都含
    「同一位置」——只断言后者会让本用例被第 2 层"糊"过去（这正是原 R2① 用例
    失效的根因）。故此处同时断言**不含**「已拒绝执行」，把层钉死。
    """

    def test_gate_fires_when_target_becomes_alias_after_verification(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "keep.txt").write_bytes(b"IMPORTANT")

            # 目标：普通空目录 → 复制路径生效，且前两层都不会命中
            dest_root = base / "new"
            target = dest_root / "文档"
            target.mkdir(parents=True)

            engine, backend = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}},
                backup_dir=base / "backup",
            )
            plan = engine.build_plan(["documents"], str(dest_root), create_user_dir=False)
            item = plan[0]
            # 前置：规划期放行、目标此刻是普通目录（第 1 层不命中）
            self.assertEqual(item.invalid_reason, "")
            self.assertFalse(fsops.resolves_to_same(item.current, item.target))

            # 可变标志位：只在"校验通过之后"才让 resolves_to_same 恒真
            armed = {"now": False}
            real_resolves = fsops.resolves_to_same
            real_verify = fsops.verify_copy

            def gated_resolves(a, b):
                return True if armed["now"] else real_resolves(a, b)

            def verify_then_arm(src, dst, result, **kwargs):
                ok, detail = real_verify(src, dst, result, **kwargs)
                armed["now"] = True      # 校验完成这一刻起，目标"变成"源的联结点
                return ok, detail

            with mock.patch.object(
                fsops, "resolves_to_same", side_effect=gated_resolves
            ), mock.patch.object(
                fsops, "verify_copy", side_effect=verify_then_arm
            ), mock.patch.object(
                fsops, "safe_remove_directory", wraps=fsops.safe_remove_directory
            ) as spy:
                report = engine.execute(plan)

            outcome = report.outcomes[0]
            self.assertEqual(outcome.status, ST_FAILED, outcome.message)
            # 第 3 层专属文案，且**不能**是第 2 层的文案
            self.assertIn("源与目标实际指向同一位置", outcome.message)
            self.assertNotIn("已拒绝执行", outcome.message, "命中的是第 2 层，不是删源闸门")

            # 走到这里说明复制/校验/写注册表都已完成 —— 正是第 3 层才该拦的时刻
            self.assertEqual(spy.call_count, 0, "删源闸门命中却仍调用了删源")
            self.assertTrue(backend.writes, "未走到写注册表窗口，本用例失去意义")

            # 物理结果：复制确实发生过，但源目录与文件必须完好
            self.assertEqual((target / "keep.txt").read_bytes(), b"IMPORTANT")
            self.assertTrue(source.is_dir(), "源目录被删除")
            self.assertEqual((source / "keep.txt").read_bytes(), b"IMPORTANT")


if __name__ == "__main__":
    unittest.main()
