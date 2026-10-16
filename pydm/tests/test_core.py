"""核心逻辑单元测试：配置、路径校验、复制校验、注册表快照、转移编排。

全部使用内存注册表后端 + 临时目录，**不触碰真实注册表与用户文件**。

运行：
    <python> -m unittest discover -s pydm/tests -v
"""

from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from docmover import fsops  # noqa: E402
from docmover.config import (  # noqa: E402
    AppConfig,
    default_target_root,
)
from docmover.constants import (  # noqa: E402
    FOLDER_KEYS,
    FOLDERS_BY_KEY,
    SHELL_FOLDERS_SUBKEY,
    USER_SHELL_FOLDERS_SUBKEY,
    FolderSpec,
)
from docmover.registry import (  # noqa: E402
    MemoryBackend,
    RegistryReadError,
    RegistrySnapshot,
    RegistryStore,
)
from docmover import transfer  # noqa: E402
from docmover.transfer import (  # noqa: E402
    JOURNAL_FILENAME,
    ST_FAILED,
    ST_OK,
    ST_SKIPPED,
    IntentJournal,
    PlanItem,
    TransferEngine,
)


def _force_cross_volume(one: str, two: str):
    """让 ``fsops.volume_key`` 认为 ``one`` / ``two`` 分属不同卷。

    审计 R6 之后，「同卷 + 目标不存在」会走原子重命名（零空间需求），空间闸门
    被**有意**跳过。因此需要检验空间闸门的用例必须显式构造**异卷**场景，否则
    会误判成「闸门失效」。返回可直接放进 ``with`` 的 patcher。
    """
    mapping = {fsops.normalize(one): "c:", fsops.normalize(two): "d:"}
    return mock.patch.object(
        fsops,
        "volume_key",
        side_effect=lambda p: mapping.get(fsops.normalize(p), "c:"),
    )


# --------------------------------------------------------------- 配置

class TestConfigDefaults(unittest.TestCase):

    def test_default_config_is_select_all_and_drive_root(self):
        """默认值：全选所有项目、目标根目录 D:\\UserData、创建当前用户目录。"""
        cfg = AppConfig().normalize()
        self.assertEqual(cfg.destination, "D:\\UserData")
        self.assertEqual(cfg.selected, list(FOLDER_KEYS))
        self.assertIs(cfg.create_user_dir, True)

    def test_normalize_filters_unknown_selection(self):
        """未知 key 会被过滤，去重并保持 FOLDERS 顺序。"""
        cfg = AppConfig(selected=["documents", "not_a_key", "cookies", "temp", "documents"])
        cfg.normalize()
        self.assertEqual(cfg.selected, ["documents"])

    def test_normalize_fixes_bad_values(self):
        cfg = AppConfig(
            destination=12345,
            selected="documents",
            create_user_dir="yes",
        ).normalize()
        self.assertEqual(cfg.destination, "")
        self.assertEqual(cfg.selected, ["documents"])
        self.assertIs(cfg.create_user_dir, True)

    def test_default_target_root(self):
        """目标根目录固定为 <盘>:\\UserData，与用户名无关。"""
        self.assertEqual(default_target_root("D:"), "D:\\UserData")
        self.assertEqual(default_target_root("d:"), "D:\\UserData")
        self.assertEqual(default_target_root("e"), "E:\\UserData")


# ------------------------------------------------------------------ 路径工具

class TestPathHelpers(unittest.TestCase):

    def test_same_path_ignores_case_and_separator(self):
        self.assertTrue(fsops.same_path(r"D:\A\B", "d:/a/b/"))
        self.assertFalse(fsops.same_path(r"D:\A\B", r"D:\A\BC"))

    def test_is_within(self):
        self.assertTrue(fsops.is_within(r"D:\A\B\C", r"D:\A"))
        self.assertTrue(fsops.is_within(r"D:\A", r"D:\A"))
        self.assertFalse(fsops.is_within(r"D:\AB", r"D:\A"))
        self.assertFalse(fsops.is_within(r"D:\A", r"D:\A\B"))

    def test_tail_name(self):
        self.assertEqual(fsops.tail_name(r"D:\UserData\me\文档"), "文档")
        self.assertEqual(fsops.tail_name("C:\\"), "")


class TestValidateDestination(unittest.TestCase):
    """磁盘列表为空时曾会拼出非法正则 ``(?i)[]:\\...``；这里改为显式校验。"""

    def test_rejects_empty(self):
        ok, msg = fsops.validate_destination("", ["D:\\x"])
        self.assertFalse(ok)
        self.assertIn("不能为空", msg)
        ok, _ = fsops.validate_destination("   ", [])
        self.assertFalse(ok)

    def test_rejects_illegal_chars(self):
        ok, msg = fsops.validate_destination("D:\\bad<name>", [])
        self.assertFalse(ok)
        self.assertIn("非法字符", msg)

    def test_rejects_relative_path(self):
        ok, msg = fsops.validate_destination("UserData\\x", [])
        self.assertFalse(ok)
        # P3-6：断言到具体消息，而不是只看 ok
        self.assertIn("绝对路径", msg)

    def test_rejects_forbidden_system_roots(self):
        for bad in (r"C:\Windows", r"C:\Windows\System32", r"C:\Program Files"):
            ok, msg = fsops.validate_destination(bad, [])
            self.assertFalse(ok, msg=bad)
            self.assertIn("系统目录", msg)

    def test_rejects_nested_relationship(self):
        ok, msg = fsops.validate_destination(r"D:\A\B", [r"D:\A"])
        self.assertFalse(ok)
        self.assertIn("内部", msg)
        # 用户把「⊂」当成乱码（U+2282 真子集）：文案一律用中文表述
        self.assertNotIn("⊂", msg, f"拒绝理由里仍有数学符号乱码嫌疑：{msg}")

        ok, msg = fsops.validate_destination(r"D:\A", [r"D:\A\B"])
        self.assertFalse(ok)
        self.assertIn("内部", msg)
        self.assertNotIn("⊂", msg, f"拒绝理由里仍有数学符号乱码嫌疑：{msg}")

    def test_accepts_plain_target(self):
        ok, msg = fsops.validate_destination(r"D:\UserData\me", [r"C:\Users\me\Documents"])
        self.assertTrue(ok, msg)


# ---------------------------------------------------------------- 文件操作

class TestTreeOperations(unittest.TestCase):

    def _make_tree(self, root: Path) -> None:
        (root / "sub").mkdir(parents=True)
        (root / "a.txt").write_text("hello", encoding="utf-8")
        (root / "sub" / "b.bin").write_bytes(b"\x00" * 2048)

    def test_scan_tree(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            self._make_tree(src)
            stat = fsops.scan_tree(str(src))
            self.assertEqual(stat.files, 2)
            self.assertEqual(stat.dirs, 1)
            self.assertEqual(stat.bytes, 5 + 2048)

    def test_copy_verify_remove_cycle(self):
        """核心安全序列：复制 → **源↔目标全量校验** → 校验通过才删除源。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            self._make_tree(src)

            result = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(result.ok, result.errors)
            self.assertEqual(result.copied_files, 2)
            self.assertEqual(result.dirs, ["sub"])

            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertTrue(ok, detail)

            removed, _ = fsops.safe_remove_directory(str(src))
            self.assertTrue(removed)
            self.assertFalse(src.exists())
            self.assertTrue((dst / "sub" / "b.bin").exists())

    @staticmethod
    def _copy(src: Path, dst: Path) -> "fsops.CopyResult":
        result = fsops.copy_merge(str(src), str(dst))
        assert result.ok, result.errors
        return result

    # ---- 审计 P0-1 回归：校验必须是「源 ↔ 目标」，而不是「目标 vs 目标」 ----

    def test_verify_detects_same_size_content_change(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "a.txt").write_bytes(b"A" * 1000)
            (src / "b.txt").write_bytes(b"B" * 1000)
            result = self._copy(src, dst)

            # 篡改成**同尺寸不同内容**：旧实现（只比目标自身大小）会判通过
            (dst / "a.txt").write_bytes(b"C" * 1000)
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertFalse(ok, "同尺寸内容篡改未被发现")
            self.assertIn("内容与源不一致", detail)

    def test_verify_detects_truncated_target(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "a.txt").write_bytes(b"A" * 500)
            result = self._copy(src, dst)

            (dst / "a.txt").write_bytes(b"A" * 10)
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertFalse(ok)
            self.assertIn("大小不符", detail)

    def test_verify_detects_missing_target_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "a.txt").write_text("abc", encoding="utf-8")
            result = self._copy(src, dst)

            (dst / "a.txt").unlink()
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertFalse(ok)
            self.assertIn("目标缺失", detail)

    def test_verify_detects_source_changed_after_copy(self):
        """源在复制期间被改动 → 校验失败，不能把用户的新内容静默丢掉。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "a.txt").write_bytes(b"A" * 1000)
            result = self._copy(src, dst)

            (src / "a.txt").write_bytes(b"Z" * 1000)      # 同尺寸，但时间戳变了
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertFalse(ok)
            self.assertIn("源在复制期间被改动", detail)

    # ---- 审计 P0-2 回归：结构/枚举遗漏必须阻止删源 ----

    def test_empty_directory_is_copied_and_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "empty").mkdir()
            (src / "a.txt").write_text("a", encoding="utf-8")

            result = self._copy(src, dst)
            self.assertTrue((dst / "empty").is_dir(), "空目录没有被复制")
            self.assertEqual(result.dirs, ["empty"])
            self.assertTrue(fsops.verify_copy(str(src), str(dst), result)[0])

            # 目标里的空目录被删掉 → 校验必须失败（旧实现对结构丢失完全无感）
            os.rmdir(dst / "empty")
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertFalse(ok)
            self.assertIn("目录", detail)
            self.assertTrue((src / "empty").is_dir(), "源目录已受损")

    def test_unlisted_source_entry_blocks_verification(self):
        """源里有、清单里没有的条目 → 校验失败（旧实现对此完全无感）。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "a.txt").write_text("a", encoding="utf-8")
            result = self._copy(src, dst)

            (src / "late.txt").write_text("出现得太晚", encoding="utf-8")
            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertFalse(ok)
            self.assertIn("未进入清单", detail)

    def test_symlink_is_rebuilt_or_item_fails(self):
        """符号链接必须按原样重建；重建不了就整体失败（绝不静默丢弃后删源）。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "real").mkdir()
            (src / "real" / "inner.txt").write_text("x", encoding="utf-8")
            try:
                os.symlink(str(src / "real"), str(src / "link"), target_is_directory=True)
            except (OSError, NotImplementedError) as exc:
                self.skipTest(f"当前环境不允许创建符号链接：{exc}")

            result = fsops.copy_merge(str(src), str(dst))
            if result.ok:
                self.assertEqual(result.links, ["link"])
                self.assertTrue(fsops.is_reparse_point(str(dst / "link")))
                self.assertTrue(fsops.verify_copy(str(src), str(dst), result)[0])
            else:
                self.assertTrue(
                    any("链接" in item for item in result.errors), result.errors
                )

    # ---- 审计 P1-2 回归：同名合并绝不能静默覆盖 ----

    def test_merge_does_not_overwrite_existing_target(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            dst.mkdir()
            (src / "same.txt").write_bytes(b"NEW-FROM-SOURCE")
            (dst / "same.txt").write_bytes(b"OLD-USER-CONTENT")

            result = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(result.ok, result.errors)
            self.assertEqual(result.conflicts, ["same.txt"])
            self.assertEqual((dst / "same.txt").read_bytes(), b"OLD-USER-CONTENT")
            self.assertEqual((dst / "same (1).txt").read_bytes(), b"NEW-FROM-SOURCE")

            ok, detail = fsops.verify_copy(str(src), str(dst), result)
            self.assertTrue(ok, detail)
            self.assertIn("另存为", detail)

    def test_merge_reuses_identical_target_file(self):
        """目标已有内容一致的文件 → 复用（合并可重入，不重复刷盘）。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            src.mkdir()
            (src / "a.txt").write_bytes(b"A" * 64)

            first = self._copy(src, dst)
            second = fsops.copy_merge(str(src), str(dst))
            self.assertTrue(second.ok, second.errors)
            self.assertEqual(len(second.items), 1)
            self.assertTrue(second.items[0].reused)
            self.assertEqual(second.conflicts, [])
            self.assertTrue(fsops.verify_copy(str(src), str(dst), second)[0])
            self.assertTrue(first.items[0].fingerprint.digest)

    def test_copy_merge_does_not_touch_source(self):
        """复制阶段绝不能删除源——这是早期版本 P2-3 数据丢失风险的直接对照。"""
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            self._make_tree(src)
            fsops.copy_merge(str(src), str(dst))
            self.assertTrue((src / "a.txt").exists())
            self.assertTrue((src / "sub" / "b.bin").exists())

    def test_copy_merge_skips_named_entries(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            self._make_tree(src)
            (src / "desktop.ini").write_text("x", encoding="utf-8")
            result = fsops.copy_merge(str(src), str(dst), skip_names=["desktop.ini"])
            self.assertTrue(result.ok)
            self.assertFalse((dst / "desktop.ini").exists())
            self.assertTrue((dst / "a.txt").exists())

    def test_safe_remove_refuses_non_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "file.txt"
            target.write_text("x", encoding="utf-8")
            ok, msg = fsops.safe_remove_directory(str(target))
            self.assertFalse(ok)
            self.assertTrue(target.exists())

    def test_atomic_move_same_volume(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dst = Path(tmp) / "dst"
            self._make_tree(src)
            moved, msg = fsops.try_atomic_move(str(src), str(dst))
            self.assertTrue(moved, msg)
            self.assertFalse(src.exists())
            self.assertTrue((dst / "a.txt").exists())


# ---------------------------------------------------------------- 注册表

class TestRegistryStore(unittest.TestCase):

    def _store(self):
        backend = MemoryBackend(
            {
                USER_SHELL_FOLDERS_SUBKEY: {
                    "Personal": (r"%USERPROFILE%\Documents", 2),
                    "Desktop": (r"D:\Keep\Desktop", 2),
                    "{F42EE2D3-909F-4907-8871-4C22FC0BF756}": (r"D:\Keep\Docs", 2),
                },
                SHELL_FOLDERS_SUBKEY: {
                    "Personal": (r"C:\Users\Tester\Documents", 1),
                },
            }
        )
        return RegistryStore(backend), backend

    def test_get_text_expands_default(self):
        store, _ = self._store()
        self.assertEqual(
            store.get_text(USER_SHELL_FOLDERS_SUBKEY, "Personal", expand=False),
            r"%USERPROFILE%\Documents",
        )
        expanded = store.get_text(USER_SHELL_FOLDERS_SUBKEY, "Personal")
        self.assertNotIn("%", expanded)
        self.assertTrue(expanded.endswith("Documents"))

    def test_set_text_uses_expandable_type_when_percent_present(self):
        store, backend = self._store()
        regtype = store.set_text(USER_SHELL_FOLDERS_SUBKEY, "Personal", r"%USERPROFILE%\NewDocs")
        self.assertEqual(regtype, 2)  # REG_EXPAND_SZ
        found = backend.read_value(USER_SHELL_FOLDERS_SUBKEY, "Personal")
        self.assertEqual(found[0], r"%USERPROFILE%\NewDocs")

    def test_set_text_uses_sz_for_plain_path(self):
        store, _ = self._store()
        regtype = store.set_text(USER_SHELL_FOLDERS_SUBKEY, "Personal", r"D:\UserData\Documents")
        self.assertEqual(regtype, 1)  # REG_SZ

    def test_snapshot_and_rollback_restores_and_deletes(self):
        store, backend = self._store()
        targets = [
            (USER_SHELL_FOLDERS_SUBKEY, "Personal"),
            (USER_SHELL_FOLDERS_SUBKEY, "BrandNew"),
        ]
        snapshot = store.snapshot(targets)
        self.assertEqual(len(snapshot.entries), 2)
        self.assertTrue(snapshot.entries[0].existed)
        self.assertFalse(snapshot.entries[1].existed)

        store.set_text(USER_SHELL_FOLDERS_SUBKEY, "Personal", r"D:\Changed")
        store.set_text(USER_SHELL_FOLDERS_SUBKEY, "BrandNew", r"D:\Fresh")

        restored, deleted = store.rollback(snapshot)
        self.assertEqual(restored, 1)
        self.assertEqual(deleted, 1)
        self.assertEqual(
            backend.read_value(USER_SHELL_FOLDERS_SUBKEY, "Personal")[0],
            r"%USERPROFILE%\Documents",
        )
        self.assertIsNone(backend.read_value(USER_SHELL_FOLDERS_SUBKEY, "BrandNew"))

    def test_snapshot_dict_roundtrip(self):
        from docmover.registry import RegistrySnapshot

        store, _ = self._store()
        snapshot = store.snapshot([(USER_SHELL_FOLDERS_SUBKEY, "Personal")])
        revived = RegistrySnapshot.from_dict(json.loads(json.dumps(snapshot.to_dict())))
        self.assertEqual(revived.entries[0].name, "Personal")
        self.assertTrue(revived.entries[0].existed)


class _FlakyReadBackend(MemoryBackend):
    """只让指定 ``(subkey, name)`` 的读取抛 ``RegistryReadError``，其余照常。"""

    def __init__(self, initial=None, failing=()):
        super().__init__(initial)
        self.failing = {(sk.lower(), name.lower()) for sk, name in failing}
        self.deleted = []

    def read_value(self, subkey, name):
        if (subkey.lower(), name.lower()) in self.failing:
            raise RegistryReadError(f"注入的读取失败 {subkey}\\{name}")
        return super().read_value(subkey, name)

    def delete_value(self, subkey, name) -> None:
        self.deleted.append((subkey, name))
        super().delete_value(subkey, name)


class TestSnapshotUnknownEntries(unittest.TestCase):
    """审计 R1：快照期「读取失败」必须记成 unknown，回滚时跳过而不是 DeleteValue。

    老实现把读失败降级成 ``existed=False``，回滚就对用户**真实存在**的值执行
    删除——那不是回滚，而是制造不可自愈的注册表损坏。
    """

    SUBKEY = USER_SHELL_FOLDERS_SUBKEY

    def test_snapshot_read_failure_marks_unknown(self):
        backend = _FlakyReadBackend(
            {self.SUBKEY: {"Personal": (r"%USERPROFILE%\Documents", 2)}},
            failing=[(self.SUBKEY, "Personal")],
        )
        store = RegistryStore(backend)
        snapshot = store.snapshot(
            [(self.SUBKEY, "Personal"), (self.SUBKEY, "BrandNew")]
        )
        by_name = {e.name: e for e in snapshot.entries}
        self.assertTrue(by_name["Personal"].unknown, "读失败没有被记成 unknown")
        self.assertFalse(by_name["Personal"].existed)
        # 真・不存在（读成功但值为 None）仍是 existed=False、unknown=False
        self.assertFalse(by_name["BrandNew"].unknown)
        self.assertFalse(by_name["BrandNew"].existed)

    def test_rollback_skips_unknown_and_keeps_real_value(self):
        """核心断言：回滚**不能**删掉用户真实存在的值。"""
        original = r"%USERPROFILE%\Documents"
        backend = _FlakyReadBackend(
            {self.SUBKEY: {"Personal": (original, 2)}},
            failing=[(self.SUBKEY, "Personal")],
        )
        store = RegistryStore(backend)
        snapshot = store.snapshot([(self.SUBKEY, "Personal")])

        # 模拟搬运期间被改成新路径
        backend.write_value(self.SUBKEY, "Personal", r"D:\Moved", 1)
        backend.deleted.clear()

        restored, removed = store.rollback(snapshot)

        self.assertEqual((restored, removed), (0, 0))
        self.assertEqual(store.last_rollback_skipped, 1)
        # 关键：老实现会在这里把用户的真实值删掉。
        # 这里用 enum_values 读（read_value 被故意注入为失败）。
        self.assertEqual(
            backend.enum_values(self.SUBKEY)["Personal"][0], r"D:\Moved"
        )
        self.assertNotIn((self.SUBKEY, "Personal"), backend.deleted)

    def test_rollback_skipped_counter_resets_each_call(self):
        backend = _FlakyReadBackend(
            {self.SUBKEY: {"Desktop": ("D:\\Keep", 1)}},
            failing=[(self.SUBKEY, "Desktop")],
        )
        store = RegistryStore(backend)
        failing_snap = store.snapshot([(self.SUBKEY, "Desktop")])
        store.rollback(failing_snap)
        self.assertEqual(store.last_rollback_skipped, 1)

        clean_snap = store.snapshot([(self.SUBKEY, "MissingValue")])
        store.rollback(clean_snap)
        self.assertEqual(store.last_rollback_skipped, 0, "计数没有按调用重置")

    def test_from_dict_accepts_legacy_backup_without_unknown(self):
        legacy = {
            "created_at": "2026-01-01T00:00:00",
            "entries": [
                {"subkey": self.SUBKEY, "name": "Personal",
                 "value": r"%USERPROFILE%\Documents", "regtype": 2, "existed": True},
                {"subkey": self.SUBKEY, "name": "Vanished",
                 "value": None, "regtype": None, "existed": False},
            ],
        }
        revived = RegistrySnapshot.from_dict(legacy)
        self.assertFalse(revived.entries[0].unknown, "旧备份不应被误判为 unknown")
        self.assertTrue(revived.entries[0].existed)
        self.assertFalse(revived.entries[1].unknown)
        self.assertFalse(revived.entries[1].existed)

    def test_to_dict_carries_unknown(self):
        entry = RegistrySnapshot.from_dict(
            {"entries": [{"subkey": self.SUBKEY, "name": "X", "unknown": True}]}
        ).entries[0]
        self.assertTrue(entry.unknown)
        self.assertTrue(entry.to_dict()["unknown"])


# ------------------------------------------------------------ 转移编排

class TestTransferEngine(unittest.TestCase):

    def setUp(self):
        # 测试用的临时目录位于 C:\Users\<用户>\AppData\Local\Temp 之下，而生产
        # 规则（P1-5 引擎层校验）禁止把用户文件夹放进 C:\Users —— 两者冲突，
        # 故在引擎测试内显式放开「禁止系统目录」这一条；该规则本身由
        # TestValidateDestination 单独覆盖。
        patcher = mock.patch.object(fsops, "FORBIDDEN_TARGET_ROOTS", ())
        patcher.start()
        self.addCleanup(patcher.stop)

    def _engine(self, registry_seed=None, **kwargs):
        backend = MemoryBackend(registry_seed or {})
        store = RegistryStore(backend)
        return TransferEngine(store=store, **kwargs), backend, store

    def test_build_plan_marks_same_path_as_noop(self):
        """目标与当前位置一致时应识别为「无需转移」，绝不能重复搬运。"""
        with tempfile.TemporaryDirectory() as tmp:
            docs = Path(tmp) / "me" / "文档"
            docs.mkdir(parents=True)
            (docs / "note.txt").write_text("hello", encoding="utf-8")

            engine, _, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(docs), 1)}}
            )
            plan = engine.build_plan(["documents"], str(Path(tmp) / "me"), create_user_dir=False)
            self.assertEqual(len(plan), 1)
            item = plan[0]
            self.assertTrue(item.same)
            self.assertFalse(item.needs_move)
            self.assertEqual(item.describe(), "路径相同，无需转移")

    def test_build_plan_inserts_username_when_enabled(self):
        """勾选「创建当前用户目录」：目标 = <根>/<用户名>/<基名>。"""
        import os as _os

        username = _os.environ.get("USERNAME", "Tester")
        with tempfile.TemporaryDirectory() as tmp:
            docs = Path(tmp) / "old" / "文档"
            docs.mkdir(parents=True)
            engine, _, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(docs), 1)}}
            )
            plan_on = engine.build_plan(
                ["documents"], str(Path(tmp) / "dest"), create_user_dir=True
            )
            self.assertEqual(
                plan_on[0].target, str(Path(tmp) / "dest" / username / "文档")
            )
            plan_off = engine.build_plan(
                ["documents"], str(Path(tmp) / "dest"), create_user_dir=False
            )
            self.assertEqual(plan_off[0].target, str(Path(tmp) / "dest" / "文档"))

    def test_describe_labels_stay_compact(self):
        """状态列文案必须短到能放进界面「状态」列（约 160 逻辑像素）。

        历史缺陷：未扫描态曾写作「待转移（执行时统计规模）」，比列宽还长，
        界面上被硬裁成「待转移（执行时统计规模」——文案只留结论，不要定语。
        """
        from unicodedata import east_asian_width

        def half_widths(text: str) -> int:
            return sum(2 if east_asian_width(ch) in ("W", "F") else 1 for ch in text)

        with tempfile.TemporaryDirectory() as tmp:
            docs = Path(tmp) / "old" / "文档"
            docs.mkdir(parents=True)
            (docs / "note.txt").write_text("hello", encoding="utf-8")
            engine, _, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(docs), 1)}}
            )
            dest = str(Path(tmp) / "dest")

            plan = engine.build_plan(["documents"], dest, create_user_dir=False, scan=False)
            self.assertEqual(plan[0].describe(), "待转移")

            plan = engine.build_plan(["documents"], dest, create_user_dir=False, scan=True)
            self.assertLessEqual(half_widths(plan[0].describe()), 26,
                                 msg=plan[0].describe())

    def test_execute_skips_noop_items(self):
        with tempfile.TemporaryDirectory() as tmp:
            docs = Path(tmp) / "me" / "文档"
            docs.mkdir(parents=True)
            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(docs), 1)}}
            )
            plan = engine.build_plan(["documents"], str(Path(tmp) / "me"), create_user_dir=False)
            report = engine.execute(plan)
            self.assertEqual(len(report.outcomes), 1)
            self.assertEqual(report.outcomes[0].status, ST_SKIPPED)
            self.assertEqual(backend.writes, [])

    def test_execute_moves_files_and_updates_registry(self):
        """完整链路：搬文件 + 按清单校验 + 删源 + 写注册表（含已存在的别名）。"""
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "report.docx").write_bytes(b"x" * 1000)

            guid = "{F42EE2D3-909F-4907-8871-4C22FC0BF756}"
            seed = {
                USER_SHELL_FOLDERS_SUBKEY: {
                    "Personal": (str(source), 1),
                    guid: (str(source), 1),  # 别名已存在 -> 应被同步
                },
                SHELL_FOLDERS_SUBKEY: {
                    "Personal": (str(source), 1),
                },
            }
            dest_root = base / "new"
            engine, backend, _ = self._engine(seed)

            plan = engine.build_plan(["documents"], str(dest_root), create_user_dir=False)
            item = plan[0]
            self.assertFalse(item.same)
            self.assertEqual(item.target, str(dest_root / "文档"))

            report = engine.execute(plan)
            outcome = report.outcomes[0]
            self.assertEqual(outcome.status, ST_OK, outcome.message)
            self.assertTrue(report.ok)

            # 文件已迁移且源已清理
            self.assertFalse(source.exists())
            self.assertEqual((dest_root / "文档" / "report.docx").read_bytes(), b"x" * 1000)

            # 权威值名 + 已存在的别名 + Shell Folders 同名值 都被更新
            written_names = {name for _, name, _, _ in backend.writes}
            self.assertIn("Personal", written_names)
            self.assertIn(guid, written_names)
            moved_value = backend.read_value(USER_SHELL_FOLDERS_SUBKEY, "Personal")[0]
            self.assertEqual(moved_value, str(dest_root / "文档"))

    def test_execute_does_not_create_absent_alias(self):
        """别名在注册表中不存在时不应凭空创建。"""
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")

            guid = "{F42EE2D3-909F-4907-8871-4C22FC0BF756}"
            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}}
            )
            plan = engine.build_plan(["documents"], str(base / "new"), create_user_dir=False)
            engine.execute(plan)

            written_names = {name for _, name, _, _ in backend.writes}
            self.assertIn("Personal", written_names)
            self.assertNotIn(guid, written_names)

    def test_execute_reports_missing_source_as_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            ghost = base / "does-not-exist"
            engine, _, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(ghost), 1)}}
            )
            plan = engine.build_plan(["documents"], str(base / "new"), create_user_dir=False)
            report = engine.execute(plan)
            self.assertEqual(report.outcomes[0].status, ST_FAILED)
            self.assertFalse(report.ok)

    def test_dry_run_touches_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")

            engine = TransferEngine(
                store=RegistryStore(MemoryBackend({USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}})),
                dry_run=True,
            )
            plan = engine.build_plan(["documents"], str(base / "new"), create_user_dir=False)
            report = engine.execute(plan)
            self.assertEqual(report.outcomes[0].status, ST_OK)
            self.assertTrue(source.exists())
            self.assertFalse((base / "new").exists())
            self.assertIn("试运行", report.outcomes[0].message)

    def test_cancel_keeps_source_intact(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")

            engine, _, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}}
            )
            plan = engine.build_plan(["documents"], str(base / "new"), create_user_dir=False)
            report = engine.execute(plan, is_cancelled=lambda: True)

            self.assertTrue(report.cancelled)
            self.assertEqual(report.outcomes[0].status, "cancelled")
            self.assertTrue(source.exists())

    # ---- 恢复系统默认值：完整恢复（注册表 + 文件搬回） ----

    @staticmethod
    def _redirect_default(key: str, default: str):
        """把某项的「系统默认路径」改到测试目录，避免动真实用户目录。

        返回可直接喂给 ``mock.patch.object(transfer, "FOLDERS_BY_KEY", ...)`` 的字典。
        """
        patched = dict(FOLDERS_BY_KEY)
        patched[key] = replace(FOLDERS_BY_KEY[key], default=default)
        return patched

    def test_restore_defaults_moves_files_back(self):
        """``恢复默认`` 必须真的把内容搬回默认位置，并在校验通过后删除源目录。

        用**可观察的物理结果**断言（目标文件内容 / 源目录是否还在），
        而不是内部计数器——否则实现被改坏时测试仍可能变绿。
        """
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            current = base / "moved" / "文档"
            (current / "sub").mkdir(parents=True)
            (current / "keep.txt").write_text("keep", encoding="utf-8")
            (current / "sub" / "deep.txt").write_text("deep", encoding="utf-8")

            # 目标目录预先存在（真实场景：C:\Users\<用户>\Documents 本来就有）
            # → 走「复制合并」路径而非原子重命名，顺带覆盖合并语义。
            target = base / "home" / "Documents"
            target.mkdir(parents=True)
            (target / "desktop.ini").write_text("[.ShellClassInfo]", encoding="utf-8")

            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(current), 1)}}
            )
            with mock.patch.object(
                transfer, "FOLDERS_BY_KEY",
                self._redirect_default("documents", str(target)),
            ):
                report = engine.restore_defaults(["documents"])

            self.assertEqual(report.outcomes[0].status, ST_OK, report.outcomes[0].message)
            self.assertEqual(
                (target / "keep.txt").read_text(encoding="utf-8"), "keep"
            )
            self.assertEqual(
                (target / "sub" / "deep.txt").read_text(encoding="utf-8"), "deep"
            )
            # 合并：目标原有内容不能被抹掉
            self.assertTrue((target / "desktop.ini").exists())
            # 源目录必须已被删除（这只有当校验通过才允许发生）
            self.assertFalse(current.exists())
            self.assertEqual(
                backend.read_value(USER_SHELL_FOLDERS_SUBKEY, "Personal")[0], str(target)
            )

    def test_restore_defaults_registry_value_keeps_expandable_form(self):
        """注册表写入值保留 ``%USERPROFILE%`` 形式，且计划阶段是只读的。"""
        with tempfile.TemporaryDirectory() as tmp:
            current = Path(tmp) / "moved" / "文档"
            current.mkdir(parents=True)
            (current / "keep.txt").write_text("keep", encoding="utf-8")

            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(current), 1)}}
            )
            # 这里刻意用**真实 spec**：build_restore_plan 只读，不碰真实注册表与文件
            plan = engine.build_restore_plan(["documents"])

            self.assertEqual(len(plan), 1)
            self.assertEqual(plan[0].registry_value, r"%USERPROFILE%\Documents")
            self.assertFalse(plan[0].registry_only)
            # 只读：文件一个没动，注册表一个没写
            self.assertTrue((current / "keep.txt").exists())
            self.assertEqual(backend.writes, [])

    def test_restore_defaults_blocks_item_when_no_space(self):
        """空间不足：**只阻断该项**，源文件保留、注册表不动。"""
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            current = base / "moved" / "文档"
            current.mkdir(parents=True)
            (current / "keep.txt").write_bytes(b"k" * 4096)
            target = base / "home" / "Documents"

            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(current), 1)}}
            )
            with mock.patch.object(
                transfer, "FOLDERS_BY_KEY",
                self._redirect_default("documents", str(target)),
            ), _force_cross_volume(str(current), str(target)), mock.patch.object(
                fsops, "free_space", return_value=1024
            ):
                report = engine.restore_defaults(["documents"])

            self.assertEqual(report.outcomes[0].status, ST_FAILED)
            self.assertIn("空间不足", report.outcomes[0].message)
            self.assertTrue((current / "keep.txt").exists())   # 源未删
            self.assertFalse((target / "keep.txt").exists())   # 目标未写
            self.assertEqual(backend.writes, [])               # 注册表未改

    def test_restore_defaults_keeps_source_when_verify_fails(self):
        """校验未通过绝不删源 —— 这正是早期版本「整目录复制后不看返回码就删源」的缺陷点。"""
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            current = base / "moved" / "文档"
            current.mkdir(parents=True)
            (current / "keep.txt").write_text("keep", encoding="utf-8")
            target = base / "home" / "Documents"
            target.mkdir(parents=True)     # 存在 → 走复制路径

            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(current), 1)}}
            )
            with mock.patch.object(
                transfer, "FOLDERS_BY_KEY",
                self._redirect_default("documents", str(target)),
            ), mock.patch.object(
                fsops, "verify_copy", return_value=(False, "模拟校验失败")
            ):
                report = engine.restore_defaults(["documents"])

            self.assertEqual(report.outcomes[0].status, ST_FAILED)
            self.assertIn("校验未通过", report.outcomes[0].message)
            self.assertTrue((current / "keep.txt").exists())
            self.assertEqual(backend.writes, [])

    def test_restore_defaults_registry_only_when_source_missing(self):
        """源目录已不存在（用户早已手工搬回）：只修注册表，不创建目录、不搬文件。"""
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            gone = base / "moved" / "文档"      # 故意不创建
            target = base / "home" / "Documents"

            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(gone), 1)}}
            )
            with mock.patch.object(
                transfer, "FOLDERS_BY_KEY",
                self._redirect_default("documents", str(target)),
            ):
                plan = engine.build_restore_plan(["documents"])
                self.assertTrue(plan[0].registry_only)
                report = engine.restore_defaults(["documents"])

            self.assertEqual(report.outcomes[0].status, ST_OK, report.outcomes[0].message)
            self.assertIn("没有文件需要搬回", report.outcomes[0].message)
            self.assertFalse(target.exists())   # 不凭空创建默认目录
            self.assertEqual(
                backend.read_value(USER_SHELL_FOLDERS_SUBKEY, "Personal")[0], str(target)
            )

    def test_restore_defaults_skips_when_already_default(self):
        """已经指向默认位置：跳过，且不重写注册表（旧实现漏算 item.same，总会重写）。"""
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            home = base / "home" / "Documents"
            home.mkdir(parents=True)
            (home / "a.txt").write_text("a", encoding="utf-8")

            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(home), 1)}}
            )
            with mock.patch.object(
                transfer, "FOLDERS_BY_KEY",
                self._redirect_default("documents", str(home)),
            ):
                report = engine.restore_defaults(["documents"])

            self.assertEqual(report.outcomes[0].status, ST_SKIPPED)
            self.assertTrue((home / "a.txt").exists())
            self.assertEqual(backend.writes, [])

    def test_restore_refuses_unsafe_source(self):
        """注册表指向盘根 / 系统目录时必须拒绝——早期版本对这种值会整目录搬走再删掉。"""
        self.assertEqual(transfer._unsafe_restore_source(""), "")
        self.assertEqual(
            transfer._unsafe_restore_source(r"D:\UserData\me\文档"), ""
        )
        # 用户配置目录下的自定义位置是合法状态，不能因为它在 C:\Users 下就拒绝
        self.assertEqual(
            transfer._unsafe_restore_source(r"C:\Users\me\MyDocs"), ""
        )
        self.assertIn("磁盘根目录", transfer._unsafe_restore_source("D:\\"))
        self.assertIn("系统目录", transfer._unsafe_restore_source(r"C:\Windows"))
        self.assertIn(
            "系统目录", transfer._unsafe_restore_source(r"C:\Program Files\App")
        )
        self.assertIn("系统目录", transfer._unsafe_restore_source(r"C:\Users"))

    def test_restore_refuses_nested_source_and_target(self):
        """源与目标互为祖先/后代时必须拒绝。

        真实风险：注册表若把 ``Personal`` 指向用户配置根 ``C:\\Users\\<用户>``，
        而系统默认正是它的子目录 ``...\\Documents`` —— 复制会自我嵌套、校验后
        再删源会毁掉整份用户配置。这是 ``validate_destination`` 的关键检查，
        恢复路径不能因「默认目标在 C:\\Users 下」就把它一并丢掉。
        """
        tgt = r"C:\Users\me\Documents"
        # 源是目标的祖先 → 目标落在源内部，会自我嵌套复制
        self.assertIn(
            "自我嵌套", transfer._unsafe_restore_source(r"C:\Users\me", tgt)
        )
        # 源在目标内部 → 删源会连带删除默认位置里的内容
        self.assertIn(
            "连带删除",
            transfer._unsafe_restore_source(r"C:\Users\me\Documents\sub", tgt),
        )
        # 同路径不算嵌套（由「已经是默认位置」分支处理，不在此拒绝）
        self.assertEqual(transfer._unsafe_restore_source(tgt, tgt), "")
        # 正常的异盘搬回不受影响
        self.assertEqual(
            transfer._unsafe_restore_source(r"D:\UserData\me\文档", tgt), ""
        )
        self.assertEqual(
            transfer._unsafe_restore_source(r"D:\UserData\me\文档", ""), ""
        )

    def test_restore_plan_rejects_unsafe_source_end_to_end(self):
        """闸门必须真的接进计划里：注册表指向系统目录 → 该项受阻且什么都不碰。

        执行部分刻意用 ``dry_run`` 引擎：万一将来这道闸门被改坏，本用例只会
        变红，**不会**真的去搬 ``C:\\Windows``。测试本身不能是危险操作。
        """
        engine, backend, _ = self._engine(
            {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (r"C:\Windows", 2)}},
            dry_run=True,
        )
        plan = engine.build_restore_plan(["documents"])
        self.assertEqual(len(plan), 1)
        self.assertIn("系统目录", plan[0].invalid_reason)

        report = engine.restore_defaults(["documents"])
        self.assertEqual(report.outcomes[0].status, ST_FAILED)
        self.assertIn("系统目录", report.outcomes[0].message)
        self.assertEqual(backend.writes, [])

    def test_restore_plan_rejects_nested_source_end_to_end(self):
        """嵌套闸门必须真的接进计划：注册表指向默认目录的父目录 → 受阻且不写注册表。

        用**临时目录**构造 spec 的默认位置，**绝不**使用真实的 ``%USERPROFILE%``：
        万一闸门被改坏，本用例这里只会扫到一个小目录就变红，而不会去递归扫描整份
        用户配置（曾实测：用真实用户配置根做该用例时，突变运行会挂住）。
        执行部分用 ``dry_run`` 引擎，闸门被改坏也**不会**真的去搬任何东西。
        """
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            parent = base / "dst"
            target_dir = parent / "文档"
            target_dir.mkdir(parents=True)
            (parent / "small.txt").write_bytes(b"x")   # 让父目录有内容

            spec = FolderSpec(
                key="documents", display="文档", section="explorer",
                value_name="Personal", default=str(target_dir),
            )
            engine, backend, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(parent), 1)}},
                dry_run=True,
            )
            with mock.patch.dict(transfer.FOLDERS_BY_KEY, {"documents": spec}):
                plan = engine.build_restore_plan(["documents"])
                self.assertEqual(len(plan), 1)
                self.assertIn("自我嵌套", plan[0].invalid_reason)

                report = engine.restore_defaults(["documents"])
            self.assertEqual(report.outcomes[0].status, ST_FAILED)
            self.assertEqual(backend.writes, [])

    # ---- 审计 P1-3 回归：空间「未知」不能等同于「无需检查」 ----

    def test_blocking_reason_handles_space_and_unreadable(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            src = base / "src"
            src.mkdir()
            (src / "a.txt").write_bytes(b"A" * 10)
            item = PlanItem(
                spec=FOLDERS_BY_KEY["documents"],
                current=str(src),
                target=str(base / "dst" / "文档"),
            )
            item.stat = fsops.TreeStat(files=1, bytes=1 << 20)

            # 审计 R6：同卷 + 目标不存在会走原子重命名（零空间需求），空间闸门会被
            # 有意跳过 → 这里让源/目标分属不同卷，以便继续检验空间闸门本身。
            with _force_cross_volume(str(src), str(base / "dst" / "文档")):
                with mock.patch.object(fsops, "free_space", return_value=None):
                    self.assertIn("可用空间", item.blocking_reason())
                with mock.patch.object(fsops, "free_space", return_value=1024):
                    self.assertIn("空间不足", item.blocking_reason())
                with mock.patch.object(fsops, "free_space", return_value=1 << 40):
                    self.assertEqual(item.blocking_reason(), "")

            # 统计期间有读不到的项 → 规模被低估，必须阻断而不是放行
            item.stat = fsops.TreeStat(files=1, bytes=10, unreadable=3)
            self.assertIn("无法读取", item.blocking_reason())

    def test_blocking_reason_skips_space_check_for_same_volume_rename(self):
        """审计 R6：同卷且目标不存在时走原子重命名，不该被「空间不足」误阻断。

        真实场景：Desktop 已在 D:、目标也在 D:，目录本身比 D: 剩余空间还大——
        重命名零字节需求，旧实现却按「复制目录大小」估算而拒绝执行。
        """
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            src = base / "src"
            src.mkdir()
            (src / "big.bin").write_bytes(b"B" * 4096)
            item = PlanItem(
                spec=FOLDERS_BY_KEY["documents"],
                current=str(src),
                target=str(base / "other" / "文档"),   # 同卷、不存在 → 走 rename
            )
            item.stat = fsops.TreeStat(files=1, bytes=1 << 30)

            with mock.patch.object(fsops, "volume_key", return_value="c:"):
                with mock.patch.object(fsops, "free_space", return_value=1024):
                    self.assertEqual(item.blocking_reason(), "")

            # 目标**已存在**时不再适用（会走复制路径）→ 空间闸门必须生效
            (base / "other" / "文档").mkdir(parents=True)
            with mock.patch.object(fsops, "volume_key", return_value="c:"):
                with mock.patch.object(fsops, "free_space", return_value=1024):
                    self.assertIn("空间不足", item.blocking_reason())

    # ---- 审计 P1-5 回归：引擎层校验（CLI / 脚本不再绕过唯一防线） ----

    def test_build_plan_rejects_destination_inside_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "文档"
            source.mkdir()
            (source / "a.txt").write_text("a", encoding="utf-8")

            engine, _, _ = self._engine(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}}
            )
            plan = engine.build_plan(
                ["documents"], str(source / "inner"), create_user_dir=False
            )
            item = plan[0]
            self.assertTrue(item.invalid_reason, "引擎层没有拦截嵌套目标")
            self.assertIn("内部", item.blocking_reason())

            report = engine.execute(plan)
            self.assertEqual(report.outcomes[0].status, ST_FAILED)
            self.assertTrue((source / "a.txt").exists())

    # ---- 审计 P0-4 / P1-4 回归：事务顺序与注册表原子性 ----

    def test_registry_failure_keeps_source_directory(self):
        """先写注册表、再删源：注册表失败时源目录必须完好。"""
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_bytes(b"x" * 2048)

            backend = FailingBackend(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}},
                fail_on_write=1,
            )
            engine = TransferEngine(store=RegistryStore(backend))
            plan = engine.build_plan(["documents"], str(base / "new"), create_user_dir=False)
            with mock.patch.object(fsops, "try_atomic_move", return_value=(False, "强制复制路径")):
                report = engine.execute(plan)

            outcome = report.outcomes[0]
            self.assertEqual(outcome.status, ST_FAILED, outcome.message)
            self.assertTrue(source.exists(), "注册表失败却删掉了源目录")
            self.assertEqual(
                (base / "new" / "文档" / "a.txt").read_bytes(), b"x" * 2048
            )

    def test_registry_partial_write_is_rolled_back(self):
        """同一文件夹的多个值名必须同进同退（否则资源管理器与实际位置不一致）。"""
        guid = "{F42EE2D3-909F-4907-8871-4C22FC0BF756}"
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")

            seed = {
                USER_SHELL_FOLDERS_SUBKEY: {
                    "Personal": (str(source), 1),
                    guid: (str(source), 1),
                }
            }
            backend = FailingBackend(seed, fail_on_write=2)
            engine = TransferEngine(store=RegistryStore(backend))
            plan = engine.build_plan(["documents"], str(base / "new"), create_user_dir=False)
            with mock.patch.object(fsops, "try_atomic_move", return_value=(False, "强制复制路径")):
                report = engine.execute(plan)

            outcome = report.outcomes[0]
            self.assertEqual(outcome.status, ST_FAILED)
            self.assertIn("回滚", outcome.message)
            self.assertEqual(
                backend.read_value(USER_SHELL_FOLDERS_SUBKEY, "Personal")[0],
                str(source),
            )
            self.assertEqual(
                backend.read_value(USER_SHELL_FOLDERS_SUBKEY, guid)[0], str(source)
            )
            self.assertTrue(source.exists())


class FailingBackend(MemoryBackend):
    """第 N 次写入时抛 OSError，用于验证注册表写入的原子性（P1-4）。"""

    def __init__(self, initial=None, fail_on_write: int = 0):
        super().__init__(initial)
        self.fail_on_write = fail_on_write
        self.attempts = 0

    def write_value(self, subkey, name, value, regtype) -> None:
        self.attempts += 1
        if self.fail_on_write and self.attempts == self.fail_on_write:
            raise OSError("注入的写入失败")
        super().write_value(subkey, name, value, regtype)


class TestIntentJournal(unittest.TestCase):
    """审计 P0-4：意图日志让「上一轮没走完」变成可发现的事件。"""

    def setUp(self):
        # 同 TestTransferEngine：临时目录位于 C:\Users 之下，显式放开系统目录规则
        patcher = mock.patch.object(fsops, "FORBIDDEN_TARGET_ROOTS", ())
        patcher.start()
        self.addCleanup(patcher.stop)

    def _item(self) -> PlanItem:
        return PlanItem(
            spec=FOLDERS_BY_KEY["documents"],
            current=r"C:\Users\Tester\Documents",
            target=r"D:\UserData\Tester\Documents",
        )

    def test_journal_tracks_stages_and_is_removed_when_clean(self):
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            journal = IntentJournal.open(backup, enabled=True)
            item = self._item()

            journal.begin([item], "registry-1.json")
            pending = IntentJournal.pending(backup)
            self.assertEqual(len(pending), 1)
            self.assertEqual(pending[0]["stage"], "pending")
            self.assertEqual(pending[0]["snapshot"], "registry-1.json")

            journal.mark(item, "copied")
            self.assertEqual(IntentJournal.pending(backup)[0]["stage"], "copied")

            journal.finish(item)
            self.assertEqual(IntentJournal.pending(backup), [])
            self.assertFalse((backup / JOURNAL_FILENAME).exists())

    def test_journal_keeps_half_done_item_but_drops_unstarted(self):
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            journal = IntentJournal.open(backup, enabled=True)
            started, untouched = self._item(), PlanItem(
                spec=FOLDERS_BY_KEY["desktop"],
                current=r"C:\Users\Tester\Desktop",
                target=r"D:\UserData\Tester\Desktop",
            )

            journal.begin([started, untouched], None)
            journal.mark(started, "registry-failed")
            journal.close_run()

            pending = IntentJournal.pending(backup)
            self.assertEqual([entry["key"] for entry in pending], [started.key])
            self.assertEqual(pending[0]["stage"], "registry-failed")

    def test_journal_disabled_never_writes(self):
        with tempfile.TemporaryDirectory() as tmp:
            backup = Path(tmp) / "backups"
            journal = IntentJournal.open(backup, enabled=False)
            journal.begin([self._item()], None)
            self.assertFalse(backup.exists())

    def test_execute_clears_journal_only_on_full_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            source = base / "old" / "文档"
            source.mkdir(parents=True)
            (source / "a.txt").write_text("a", encoding="utf-8")
            backup = base / "backups"

            engine = TransferEngine(
                store=RegistryStore(MemoryBackend({USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source), 1)}})),
                backup_dir=backup,
            )
            plan = engine.build_plan(["documents"], str(base / "new"), create_user_dir=False)
            with mock.patch.object(fsops, "try_atomic_move", return_value=(False, "强制复制路径")):
                report = engine.execute(plan)

            self.assertEqual(report.outcomes[0].status, ST_OK, report.outcomes[0].message)
            self.assertFalse((backup / JOURNAL_FILENAME).exists())

            # 失败的一轮必须留下痕迹，供下次启动提示
            source2 = base / "old2" / "文档"
            source2.mkdir(parents=True)
            (source2 / "a.txt").write_text("a", encoding="utf-8")
            backend = FailingBackend(
                {USER_SHELL_FOLDERS_SUBKEY: {"Personal": (str(source2), 1)}},
                fail_on_write=1,
            )
            engine2 = TransferEngine(store=RegistryStore(backend), backup_dir=backup)
            plan2 = engine2.build_plan(["documents"], str(base / "new2"), create_user_dir=False)
            with mock.patch.object(fsops, "try_atomic_move", return_value=(False, "强制复制路径")):
                engine2.execute(plan2)

            pending = IntentJournal.pending(backup)
            self.assertEqual(len(pending), 1)
            self.assertEqual(pending[0]["stage"], "registry-failed")


if __name__ == "__main__":
    unittest.main(verbosity=2)
