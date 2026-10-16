"""注册表读写与快照回滚。

设计要点：真实读写被隔离在 ``RegistryBackend`` 之后，测试可注入内存后端，
从而在**完全不触碰真实注册表**的前提下验证全部流程。
"""

from __future__ import annotations

import logging
import os
import re
from dataclasses import dataclass, field
from typing import Any, Dict, Iterable, List, Optional, Protocol, Tuple

try:
    import winreg
except ImportError:  # 非 Windows 平台（仅用于静态检查/测试）
    winreg = None  # type: ignore[assignment]

REG_SZ = 1
REG_EXPAND_SZ = 2

HKCU_PATH_PREFIX = "HKEY_CURRENT_USER\\"

# 审计 P2-2：``RegistryBackend`` 的主契约（read_value/write_value/...）一律相对
# **HKCU**——本工具的迁移对象（User Shell Folders）确实都在 HKCU。但少数**只读**的
# 系统开关位于 HKLM（如 ``Winlogon\\AutoRestartShell``），需要一个显式的、不会与
# 写入路径混淆的只读入口，故另立 ``read_value_hklm``（见下）。
HIVE_HKCU = "HKEY_CURRENT_USER"
HIVE_HKLM = "HKEY_LOCAL_MACHINE"

# 审计 P2-3：注册表读取的告警统一走这里，便于在无 logger 场景也能落 stderr。
_LOGGER = logging.getLogger("docmover.registry")


class RegistryReadError(OSError):
    """读取注册表时遇到 ``FileNotFoundError`` 之外的错误（权限 / 句柄 / 损坏等）。

    审计 P2-3：旧实现把这类错误和「值不存在」一视同仁地返回 ``None``，导致
    ``resolve_current`` 静默回落到系统默认路径、``exists()`` 误判别名不存在。
    这里把「真不存在」与「读失败」区分开：后者抛出本异常，由 ``RegistryStore``
    记录并告警——**宁可显式告警，不可静默降级**。
    """


def _expand(text: str) -> str:
    """展开 ``%USERPROFILE%`` 形式的环境变量。"""
    if not text:
        return ""
    if winreg is not None:
        try:
            return winreg.ExpandEnvironmentStrings(text)
        except (AttributeError, OSError):
            pass
    return os.path.expandvars(text)


# 审计 F10：``%VAR%\...`` 形式的可展开路径。必须**以**环境变量开头，且后面紧跟
# 分隔符或结束——这是 Windows 自己写 ``%USERPROFILE%\Documents`` 时的形态。
#
# 旧实现是 ``if "%" in value``：只要值里**任意位置**出现 ``%`` 就写成
# REG_EXPAND_SZ。于是 ``D:\a%USERPROFILE%b\UserData``（目录名里恰好含百分号）
# 被系统再次展开成 ``D:\aC:\Users\me\UserData`` —— 路径静默失真。
_EXPANDABLE_PREFIX = re.compile(r"^%[A-Za-z_][A-Za-z0-9_()]*%(?:\\[^\x00]*)?$")


def _is_expandable(value: str) -> bool:
    """值是否应写成 ``REG_EXPAND_SZ``（审计 F10）。

    * ``%USERPROFILE%\\Documents`` → True（系统默认路径，必须保持可展开）
    * ``%USERPROFILE%``            → True
    * ``D:\\a%USERPROFILE%b\\UserData`` → False（``%`` 出现在中间）
    * ``100%`` / ``%FOO`` / ``%FOO%bar`` → False
    """
    return bool(_EXPANDABLE_PREFIX.match(value))


class RegistryBackend(Protocol):
    """注册表访问抽象。

    ``read_value`` / ``write_value`` / ``delete_value`` / ``enum_values`` 的子键
    路径**一律相对 HKCU**（迁移目标全在 HKCU，写入面不收窄就不安全）。

    ``read_value_hklm`` 是**只读**的例外通道（审计 P2-2）：仅用于读取位于 HKLM 的
    系统开关（如 ``Winlogon\\AutoRestartShell``）。它是只读的——本工具**从不**写
    HKLM，故不存在误写到机器级配置的路径。
    """

    def read_value(self, subkey: str, name: str) -> Optional[Tuple[Any, int]]:
        ...

    def read_value_hklm(self, subkey: str, name: str) -> Optional[Tuple[Any, int]]:
        ...

    def write_value(self, subkey: str, name: str, value: Any, regtype: int) -> None:
        ...

    def delete_value(self, subkey: str, name: str) -> None:
        ...

    def enum_values(self, subkey: str) -> Dict[str, Tuple[Any, int]]:
        ...


class WinRegBackend:
    """真实注册表后端：写路径一律 HKCU，另有一条只读的 HKLM 通道。"""

    @staticmethod
    def _require_winreg() -> None:
        if winreg is None:
            raise RuntimeError("winreg 不可用：本程序仅支持 Windows")

    def _read_from(
        self, root: Any, subkey: str, name: str, hive_label: str = ""
    ) -> Optional[Tuple[Any, int]]:
        """按给定根键读取一个值；``hive_label`` 仅用于把 HKLM 写进错误消息。"""
        try:
            with winreg.OpenKey(root, subkey, 0, winreg.KEY_READ) as key:
                return winreg.QueryValueEx(key, name)
        except FileNotFoundError:
            # 键不存在 / 值不存在：这是「真的没有」，返回 None 合法
            return None
        except OSError as exc:
            # 审计 P2-3：其余 OSError（权限 / 句柄 / 损坏）不是「不存在」，
            # 抛给上层显式告警，绝不在此静默吞掉。
            raise RegistryReadError(
                f"读取注册表 {hive_label}{subkey}\\{name} 失败：{exc}"
            ) from exc

    def read_value(self, subkey: str, name: str) -> Optional[Tuple[Any, int]]:
        self._require_winreg()
        return self._read_from(winreg.HKEY_CURRENT_USER, subkey, name)

    def read_value_hklm(self, subkey: str, name: str) -> Optional[Tuple[Any, int]]:
        """只读 HKLM（审计 P2-2）。读 HKLM 不需要管理员权限。"""
        self._require_winreg()
        return self._read_from(
            winreg.HKEY_LOCAL_MACHINE, subkey, name, hive_label="HKEY_LOCAL_MACHINE\\"
        )

    def write_value(self, subkey: str, name: str, value: Any, regtype: int) -> None:
        if winreg is None:
            raise RuntimeError("winreg 不可用：本程序仅支持 Windows")
        with winreg.CreateKeyEx(
            winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_SET_VALUE
        ) as key:
            winreg.SetValueEx(key, name, 0, regtype, value)

    def delete_value(self, subkey: str, name: str) -> None:
        if winreg is None:
            raise RuntimeError("winreg 不可用：本程序仅支持 Windows")
        try:
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_SET_VALUE
            ) as key:
                winreg.DeleteValue(key, name)
        except FileNotFoundError:
            pass

    def enum_values(self, subkey: str) -> Dict[str, Tuple[Any, int]]:
        if winreg is None:
            raise RuntimeError("winreg 不可用：本程序仅支持 Windows")
        result: Dict[str, Tuple[Any, int]] = {}
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, subkey, 0, winreg.KEY_READ) as key:
                index = 0
                while True:
                    try:
                        name, value, regtype = winreg.EnumValue(key, index)
                    except OSError as exc:
                        # ERROR_NO_MORE_ITEMS(259)：枚举正常走到末尾 → 结束循环。
                        # 其余 OSError（权限 / 句柄 / 损坏）属真失败，向上抛给
                        # 外层统一处理，不在此静默吞掉。
                        if getattr(exc, "winerror", None) != 259:
                            raise
                        break
                    result[name] = (value, regtype)
                    index += 1
        except FileNotFoundError:
            # 子键不存在：这是「真的没有」→ 空结果（与 read_value 返回 None 同义）
            return {}
        except OSError as exc:
            # 审计 P2-3 / 问题#3：其余 OSError（权限 / 句柄 / 损坏）不是「不存在」，
            # 与 read_value 保持**同一语义**：抛 RegistryReadError 给上层显式告警，
            # 绝不静默返回一个「看起来正常」的空 dict（旧实现会一路冒到顶层兜底，
            # 使 ``run.py --scan`` 中途中断）。
            raise RegistryReadError(f"枚举注册表 {subkey} 失败：{exc}") from exc
        return result


class MemoryBackend:
    """内存后端，供单元测试与 ``--dry-run`` 规划阶段使用。

    ``initial`` 是 **HKCU** 的种子数据；``hklm`` 是 **HKLM** 的只读种子（审计 P2-2）。
    两个命名空间相互独立——这正是要测的性质：把 ``AutoRestartShell`` 只放进 ``initial``
    （旧的 HKCU 口径）**不应**让判定为真。
    """

    def __init__(
        self,
        initial: Optional[Dict[str, Dict[str, Tuple[Any, int]]]] = None,
        hklm: Optional[Dict[str, Dict[str, Tuple[Any, int]]]] = None,
    ):
        self._data: Dict[str, Dict[str, Tuple[Any, int]]] = {
            k: dict(v) for k, v in (initial or {}).items()
        }
        self._hklm: Dict[str, Dict[str, Tuple[Any, int]]] = {
            k: dict(v) for k, v in (hklm or {}).items()
        }
        self.writes: List[Tuple[str, str, Any, int]] = []

    def read_value(self, subkey: str, name: str) -> Optional[Tuple[Any, int]]:
        return self._data.get(subkey, {}).get(name)

    def read_value_hklm(self, subkey: str, name: str) -> Optional[Tuple[Any, int]]:
        """只读 HKLM 种子。本后端**没有**任何写 HKLM 的入口。"""
        return self._hklm.get(subkey, {}).get(name)

    def write_value(self, subkey: str, name: str, value: Any, regtype: int) -> None:
        self._data.setdefault(subkey, {})[name] = (value, regtype)
        self.writes.append((subkey, name, value, regtype))

    def delete_value(self, subkey: str, name: str) -> None:
        self._data.get(subkey, {}).pop(name, None)

    def enum_values(self, subkey: str) -> Dict[str, Tuple[Any, int]]:
        return dict(self._data.get(subkey, {}))


@dataclass
class RegistryEntry:
    """单个注册表值的快照。

    ``existed=False`` 表示快照时该值**确实不存在**（回滚时按「原本没有」删除）。

    ``unknown=True``（审计 R1）表示快照期**读取失败**——「原本是否有值」未知。
    这类条目**回滚时必须跳过、绝不能 delete**：若把读失败混同为「不存在」，
    回滚就会对用户**真实存在**的值执行 ``DeleteValue``，属不可自愈的注册表损坏。
    """

    subkey: str
    name: str
    value: Any = None
    regtype: Optional[int] = None
    existed: bool = False
    unknown: bool = False

    def to_dict(self) -> Dict[str, Any]:
        return {
            "subkey": self.subkey,
            "name": self.name,
            "value": self.value,
            "regtype": self.regtype,
            "existed": self.existed,
            "unknown": self.unknown,
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "RegistryEntry":
        return cls(
            subkey=data["subkey"],
            name=data["name"],
            value=data.get("value"),
            regtype=data.get("regtype"),
            existed=bool(data.get("existed")),
            # 兼容旧备份 JSON（无 unknown 键 → False，语义仍是「确实不存在」）
            unknown=bool(data.get("unknown", False)),
        )


@dataclass
class RegistrySnapshot:
    """一组注册表值的快照，用于一键回滚。"""

    entries: List[RegistryEntry] = field(default_factory=list)
    created_at: str = ""

    def to_dict(self) -> Dict[str, Any]:
        return {
            "created_at": self.created_at,
            "entries": [e.to_dict() for e in self.entries],
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "RegistrySnapshot":
        return cls(
            created_at=data.get("created_at", ""),
            entries=[RegistryEntry.from_dict(d) for d in data.get("entries", [])],
        )


class RegistryStore:
    """面向业务的高层封装。"""

    def __init__(self, backend: Optional[RegistryBackend] = None, logger=None):
        self.backend: RegistryBackend = backend or WinRegBackend()
        self.log = logger or _LOGGER
        # 审计 P2-3：最近一次「读取失败（非不存在）」的记录。上层可通过
        # consume_read_error() 取走并告警；对外仍保留返回 None 的兼容行为。
        self.last_read_error: Optional[str] = None
        # 审计 R1：最近一次 rollback() 因「快照期读失败（unknown）」而跳过的条目数。
        # 与 last_read_error 分开：前者是读取失败、后者是回滚跳过，语义不同。
        self.last_rollback_skipped: int = 0

    def _read(self, subkey: str, name: str) -> Optional[Tuple[Any, int]]:
        """统一的读取入口：把 ``RegistryReadError`` 转成告警 + ``None``。

        兼容旧返回契约（取不到即 ``None``），但**不再静默**——错误记入
        ``last_read_error`` 并写 warning，供 ``resolve_current`` / UI / CLI 感知。
        """
        try:
            return self.backend.read_value(subkey, name)
        except RegistryReadError as exc:
            self.last_read_error = str(exc)
            self.log.warning(
                "注册表读取失败，暂按“值不存在”继续（请留意位置可能不准）：%s", exc
            )
            return None

    def consume_read_error(self) -> Optional[str]:
        """取走并清空最近一次读取错误（供上层显式告警，审计 P2-3）。"""
        error = self.last_read_error
        self.last_read_error = None
        return error

    # ------------------------------------------------------------- 基本读写
    def get_raw(self, subkey: str, name: str) -> Optional[Tuple[Any, int]]:
        return self._read(subkey, name)

    def get_text(self, subkey: str, name: str, expand: bool = True) -> str:
        """读取文本值；``expand=True`` 时展开环境变量。"""
        found = self._read(subkey, name)
        if not found:
            return ""
        value = found[0]
        if not isinstance(value, str):
            return str(value)
        return _expand(value) if expand else value

    def exists(self, subkey: str, name: str) -> bool:
        """判断值是否存在（对外保持 ``bool`` 的兼容签名）。

        审计 问题#4：本方法走 ``_read`` 读包装——读取失败**不会**被伪装成一个
        干净的 ``False``：``_read`` 会把失败记入 ``last_read_error`` 并写 warning。
        但请注意：调用方若只拿到 ``False`` 无法区分「读失败」与「真的不存在」，
        因此**对一致性敏感的调用点必须消费 ``consume_read_error()``**（见
        ``TransferEngine._registry_targets`` 的调用方 ``build_plan``：一旦发生读
        失败就把该项标记为受阻，避免漏掉本该同步的别名而破坏 P1-4）。
        """
        return self._read(subkey, name) is not None

    def set_text(
        self, subkey: str, name: str, value: str, keep_expandable: bool = True
    ) -> int:
        """写入文本值。

        形如 ``%VAR%\\...`` 的才用 REG_EXPAND_SZ（系统原生行为，也是
        ``constants.FOLDERS[*].default`` 的正常形态）；**值中间**出现的 ``%``
        不再触发可展开类型——否则读回时会被二次展开成错误路径（审计 F10，
        见 :func:`_is_expandable`）。其余按调用方意愿写 REG_SZ。
        """
        regtype = REG_EXPAND_SZ if (keep_expandable and _is_expandable(value)) else REG_SZ
        self.backend.write_value(subkey, name, value, regtype)
        return regtype

    # --------------------------------------------------------------- 快照
    def snapshot(self, targets: Iterable[Tuple[str, str]]) -> RegistrySnapshot:
        """记录一组注册表值的当前状态，供 :meth:`rollback` 还原。

        审计 R1：此处**直接**调用 ``backend.read_value`` 并显式区分两种「取不到」：

        * ``RegistryReadError``（权限 / 句柄 / 损坏）→ 记为 ``unknown=True``；
        * 返回 ``None``（键或值确实不存在）→ 记为 ``existed=False``。

        旧实现走 ``_read``，把读失败也降级成 ``None``、进而记成
        ``existed=False``；回滚时就会对用户**真实存在**的值执行 ``DeleteValue``
        ——这不是「回滚」，而是制造不可自愈的注册表损坏。故改用直读并显式分流。
        """
        from datetime import datetime

        entries: List[RegistryEntry] = []
        seen = set()
        for subkey, name in targets:
            marker = (subkey.lower(), name.lower())
            if marker in seen:
                continue
            seen.add(marker)
            try:
                found = self.backend.read_value(subkey, name)
            except RegistryReadError as exc:
                # 读失败 ≠ 值不存在。若记成 existed=False，回滚会对用户真实存在的
                # 值执行 DeleteValue，属不可自愈的注册表损坏。
                self.last_read_error = str(exc)
                self.log.warning("快照读取失败，该项将不参与回滚：%s", exc)
                entries.append(
                    RegistryEntry(subkey=subkey, name=name, unknown=True)
                )
                continue
            if found is None:
                entries.append(RegistryEntry(subkey=subkey, name=name, existed=False))
            else:
                entries.append(
                    RegistryEntry(
                        subkey=subkey,
                        name=name,
                        value=found[0],
                        regtype=found[1],
                        existed=True,
                    )
                )
        return RegistrySnapshot(
            entries=entries, created_at=datetime.now().isoformat(timespec="seconds")
        )

    def rollback(self, snapshot: RegistrySnapshot) -> Tuple[int, int]:
        """按快照还原。返回 ``(恢复数, 删除数)``。

        审计 R1：``unknown=True`` 的条目（快照期读失败）**跳过**——它既不能恢复
        （不知道原值）也不能删除（不知道原本有没有），跳过是唯一安全的选择。

        跳过的事实通过两条通道暴露（二者都在本方法内更新）：

        * 实例属性 :attr:`last_rollback_skipped`（每次调用开头重置，供代码读取）；
        * 一条 ``warning`` 日志（供排障）。

        选择**同时**提供两者而非写进 ``last_read_error``：后者的语义是「读取失败」，
        把「回滚跳过」混进去会污染 ``consume_read_error`` 的消费者；而
        ``last_rollback_skipped`` 是纯计数，UI 文案接线由调用方负责。
        """
        restored = 0
        removed = 0
        skipped = 0
        for entry in snapshot.entries:
            if entry.unknown:
                skipped += 1
                continue
            if entry.existed:
                regtype = entry.regtype if entry.regtype is not None else REG_SZ
                self.backend.write_value(entry.subkey, entry.name, entry.value, regtype)
                restored += 1
            else:
                self.backend.delete_value(entry.subkey, entry.name)
                removed += 1
        self.last_rollback_skipped = skipped
        if skipped:
            self.log.warning(
                "回滚时有 %d 处注册表值因「快照读取失败、原值未知」被跳过，未做改动", skipped
            )
        return restored, removed

    def enum_values(self, subkey: str) -> Dict[str, Tuple[Any, int]]:
        """枚举子键下的全部值。

        审计 问题#3：与 ``_read`` 保持**同一兜底语义**——读失败不再直接冒出把
        ``run.py --scan`` 打断，而是记录到 ``last_read_error`` + warning，并返回
        空 dict（兼容旧返回契约），由调用方通过 ``consume_read_error()`` 感知，
        而不是拿到一个「看起来正常」的空结果。
        """
        try:
            return self.backend.enum_values(subkey)
        except RegistryReadError as exc:
            self.last_read_error = str(exc)
            self.log.warning(
                "枚举注册表失败，暂按“无值”继续（请留意结果可能不准）：%s", exc
            )
            return {}
