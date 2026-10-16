"""转移业务编排：规划 → 执行 → 校验 → 回滚。

与 UI 完全解耦，通过回调上报进度，便于单元测试与命令行复用。

安全序列（2026-10 审计 P0-4 修复后的顺序）：

    复制（含空目录与链接重建）→ **源 ↔ 目标全量校验** → 写注册表 → 删源

先写注册表再删源，是为了避免「源目录已删、注册表仍指向旧路径」的孤儿状态；
每项的执行阶段都登记在意图日志里，进程被中断后启动时可检测并提示。
"""

from __future__ import annotations

import json
import os
import time
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import (
    Any,
    Callable,
    Dict,
    Iterable,
    List,
    Optional,
    Sequence,
    Tuple,
)

from . import fsops
from .constants import (
    FOLDERS_BY_KEY,
    FolderSpec,
    SHELL_FOLDERS_SUBKEY,
    SPACE_MARGIN_BYTES,
    SPACE_MARGIN_RATIO,
    USER_SHELL_FOLDERS_SUBKEY,
    forbidden_source_exact,
    forbidden_source_trees,
)
from .registry import RegistrySnapshot, RegistryStore

ItemCallback = Callable[["PlanItem"], None]
ProgressCallback = Callable[[int, int, str], None]
CancelCallback = Callable[[], bool]

# 状态常量
ST_PENDING = "pending"
ST_OK = "ok"
ST_SKIPPED = "skipped"
ST_FAILED = "failed"
ST_CANCELLED = "cancelled"

STATUS_LABELS = {
    ST_PENDING: "待处理",
    ST_OK: "完成",
    ST_SKIPPED: "跳过",
    ST_FAILED: "失败",
    ST_CANCELLED: "已取消",
}

# 审计 P2-13：除权威键 ``User Shell Folders`` 外，是否仍同步写 ``Shell Folders``
# 中**已存在**的同名值。该子键由 Explorer 维护、属缓存性质，主动写它并非官方
# 推荐，系统可能在下次刷新时覆盖回去——但覆盖回旧值只影响「当前会话是否立即
# 生效」，不影响数据安全；而免重启资源管理器即可生效是核心体验，故**默认开启**。
# 如需退化为「只写权威键」，把本开关置 False 即可。
SYNC_SHELL_FOLDERS = True


@dataclass
class PlanItem:
    """一项待执行的转移计划。"""

    spec: FolderSpec
    current: str
    target: str
    registry_value: str = ""
    target_exists: bool = False
    merge: bool = False
    same: bool = False
    scanned: bool = False
    stat: fsops.TreeStat = field(default_factory=fsops.TreeStat)
    registry_targets: List[Tuple[str, str]] = field(default_factory=list)
    invalid_reason: str = ""   # 引擎层目标校验的结论（P1-5）
    # 恢复默认专用：源目录已不存在，只剩注册表要修（见 _execute_registry_only）。
    # 转移路径**从不**设置该标志，故对既有流程完全惰性。
    registry_only: bool = False

    @property
    def key(self) -> str:
        return self.spec.key

    @property
    def display(self) -> str:
        return self.spec.display

    @property
    def needs_move(self) -> bool:
        return not self.same and bool(self.current) and bool(self.target)

    def describe(self) -> str:
        """状态列文案：必须短到能放进表格「状态」列（宽度约 160 逻辑像素）。

        历史教训：未扫描态曾写作「待转移（执行时统计规模）」，比列宽还长，
        在界面上被硬裁成「待转移（执行时统计规模」——文案只留结论，不要定语。
        """
        if self.same:
            return "路径相同，无需转移"
        if self.registry_only:
            return "仅更新注册表"
        if self.merge:
            return f"将合并（{self.stat.describe()}）"
        if not self.scanned:
            return "待转移"
        return f"待转移，{self.stat.describe()}"

    def blocking_reason(self) -> str:
        """返回不能执行的原因；空字符串表示可以执行。"""
        if self.same:
            return ""
        if not self.current:
            return "未能确定当前位置"
        # 审计 B-1：这里曾漏加 ``fsops._lp()``，源路径 ≥240 字符时
        # ``os.path.isdir`` 会被 Win32 的 MAX_PATH 截断，把明明存在的源误判成
        # 「当前位置不存在」而阻断 —— 与 README 承诺的「超长路径自动加
        # ``\\?\`` 前缀」不符。同文件其余 7 处 FS 调用点（引擎各处与 CLI）都套了。
        if not os.path.isdir(fsops._lp(self.current)):
            return "当前位置不存在"
        if self.invalid_reason:
            return self.invalid_reason
        if fsops.same_path(self.target, self.current):
            return ""
        if self.stat.unreadable:
            # 统计期间有读不到的项 → 规模被低估，不能拿它当「空间充足」的依据
            return (
                f"有 {self.stat.unreadable} 项内容无法读取（被占用或无权限），"
                "无法确认能否安全转移"
            )
        # 审计 R6：同卷且目标不存在时会走 ``try_atomic_move`` 的 ``os.rename``
        # ——瞬间完成、**零字节需求**。空间闸门不该把这种搬迁误判成「空间不足」。
        # 旧实现一律按「复制 needed 字节」估算，导致同卷搬大目录（如 Desktop 已
        # 在 D:、目标也在 D:）而 D: 剩余空间小于目录自身大小时被无谓阻断。
        # 若 rename 失败退回复制，仍会在复制时因 ENOSPC 失败并保留源目录，
        # 不会丢数据——即空间检查的**兜底**仍在复制路径里。
        if (
            not os.path.exists(fsops._lp(self.target))
            and fsops.volume_key(self.current) == fsops.volume_key(self.target)
        ):
            return ""
        needed = self.stat.bytes
        available = fsops.free_space(self.target)
        if available is None:
            # 审计 P1-3：「未知」不再等同于「无需检查」
            return "无法读取目标磁盘可用空间，为避免写满磁盘已停止"
        # 审计 B-6：5% 与 64 MB 这两个余量数字被 README 写成产品承诺，散在代码里
        # 时改了实现会连带让文档失真；统一取自 ``constants.SPACE_MARGIN_*``。
        required = int(needed * SPACE_MARGIN_RATIO) + SPACE_MARGIN_BYTES
        if needed and required > available:
            return (
                f"目标磁盘空间不足：内容 {fsops._human(needed)}，"
                f"建议预留 {fsops._human(required)}，可用 {fsops._human(available)}"
            )
        return ""


@dataclass
class ItemOutcome:
    key: str
    display: str
    status: str
    message: str = ""
    source: str = ""
    target: str = ""
    files: int = 0
    copied_bytes: int = 0

    @property
    def label(self) -> str:
        return STATUS_LABELS.get(self.status, self.status)


@dataclass
class TransferReport:
    outcomes: List[ItemOutcome] = field(default_factory=list)
    snapshot_path: Optional[str] = None
    started_at: str = ""
    finished_at: str = ""
    cancelled: bool = False
    # 审计 R4：附加提示（如「注册表备份未能落盘」）。会并入 summary()，
    # 因此**无需改动界面**即可显示在最显眼的结果标题里；为空时 summary()
    # 的输出与旧版逐字相同。
    notes: List[str] = field(default_factory=list)

    @property
    def failed(self) -> List[ItemOutcome]:
        return [o for o in self.outcomes if o.status == ST_FAILED]

    @property
    def succeeded(self) -> List[ItemOutcome]:
        return [o for o in self.outcomes if o.status == ST_OK]

    @property
    def ok(self) -> bool:
        return not self.failed and not self.cancelled

    def summary(self) -> str:
        done = len(self.succeeded)
        failed = len(self.failed)
        skipped = len([o for o in self.outcomes if o.status == ST_SKIPPED])
        parts = [f"完成 {done} 项"]
        if failed:
            parts.append(f"失败 {failed} 项")
        if skipped:
            parts.append(f"跳过 {skipped} 项")
        if self.cancelled:
            parts.append("用户取消")
        text = "，".join(parts)
        if self.notes:
            text += "（" + "；".join(self.notes) + "）"
        return text


# ---------------------------------------------------------------- 意图日志

JOURNAL_FILENAME = "intent.json"

STAGE_LABELS: Dict[str, str] = {
    "pending": "未开始",
    "copying": "正在复制（目标可能只写入一部分）",
    "copied": "复制校验完成，源目录未删除",
    "registry-written": "注册表已更新，源目录未删除",
    "source-removed": "源目录已删除",
    "cancelled": "已取消",
    "copy-failed": "复制失败",
    "verify-failed": "复制校验未通过",
    "registry-failed": "注册表写入失败",
    "source-removal-failed": "源目录删除失败",
    "unexpected-error": "发生未预期错误",
}

# 最近一次读取意图日志时的失败原因（损坏 / 不可读）；空字符串表示读取正常。
# 由 ``IntentJournal.pending()`` 写入、经 ``consume_read_error()`` 暴露给上层。
# 存在的理由（审计 B4）：「解析失败」必须是一个**可见**事件，而不能等同于
# 「没有残留记录」——否则唯一的收尾信号会被静默吞掉。
_JOURNAL_READ_ERROR: str = ""


@dataclass
class IntentJournal:
    """事务意图日志（审计 P0-4）。

    每项在执行前登记、在安全收尾后注销；**只有全部项都注销才删除日志文件**。
    因此「文件还在」就等于「上次运行被中断，可能有半完成的项目」——启动时
    据此提示用户，并可用当时保存的快照把注册表回滚回去。

    ``dry_run`` 或没有备份目录时完全不落盘。
    """

    path: Optional[Path] = None
    enabled: bool = False
    logger: Any = None
    _data: Dict[str, Any] = field(default_factory=dict)

    @classmethod
    def open(
        cls,
        backup_dir: Optional[Path],
        enabled: bool = True,
        logger: Any = None,
    ) -> "IntentJournal":
        path = (Path(backup_dir) / JOURNAL_FILENAME) if backup_dir else None
        return cls(path=path, enabled=bool(enabled and path is not None), logger=logger)

    # ------------------------------------------------------------- 写入侧
    def begin(self, plan: Sequence[PlanItem], snapshot: Optional[str]) -> None:
        if not self.enabled:
            return
        self._data = {
            "started_at": datetime.now().isoformat(timespec="seconds"),
            "snapshot": snapshot or "",
            "items": {
                item.key: {
                    "display": item.display,
                    "source": item.current,
                    "target": item.target,
                    "stage": "pending",
                    # 审计 R8：记录本项涉及的注册表 (子键, 值名)，使日志本身就能
                    # 回答「哪些注册表值属于哪一项」——回滚时据此只回滚**未完成**
                    # 的项，避免把本轮已成功（源目录已删除）的项一起滚回旧路径。
                    "registry": [[sk, name] for sk, name in item.registry_targets],
                }
                for item in plan
            },
        }
        self._flush()

    def mark(self, item: PlanItem, stage: str) -> None:
        if not self.enabled:
            return
        entries = self._data.setdefault("items", {})
        entry = entries.get(item.key)
        if entry is None:
            entries[item.key] = {
                "display": item.display,
                "source": item.current,
                "target": item.target,
                "stage": stage,
                # 与 begin() 保持一致：补登记项也要带上注册表目标（审计 R8）
                "registry": [[sk, name] for sk, name in item.registry_targets],
            }
        else:
            entry["stage"] = stage
        self._flush()

    def finish(self, item: PlanItem) -> None:
        """该项已安全收尾，注销登记；全部注销后删除日志文件。"""
        if not self.enabled:
            return
        self._data.get("items", {}).pop(item.key, None)
        if self._data.get("items"):
            self._flush()
        else:
            self.discard()

    def close_run(self) -> None:
        """一次运行收尾：未开始的项与**用户取消**的项直接注销，真正半完成的项保留日志。

        审计 B2（本轮 P1 之一）：``cancelled`` 必须与 ``pending`` 一并清理。
        取消是用户主动发起的**正常收尾**——源目录未删除、注册表未写入，产品
        文案也承诺「已完成的项目保留、未完成的源文件不删除」。若把 ``cancelled``
        留在日志里，下次启动 ``pending()`` 会把它当作「上次转移未正常收尾」，
        并附上**本轮整批项**的注册表快照建议回滚；一旦用户照做，本轮已经成功
        完成（源目录已删除）的项会被一起滚回旧路径，反而制造出指向**不存在目录**
        的孤儿注册表值——即「取消一次 + 重启」这种日常操作就能触发的错误状态。
        """
        if not self.enabled:
            return
        entries = self._data.get("items", {})
        for key in [
            k for k, v in entries.items()
            if v.get("stage") in ("pending", "cancelled")
        ]:
            entries.pop(key, None)
        if entries:
            self._flush()
        else:
            self.discard()

    def discard(self) -> None:
        self._data = {}
        if self.path is not None:
            try:
                self.path.unlink(missing_ok=True)
            except OSError:
                pass

    def _flush(self) -> None:
        if not self.enabled or self.path is None:
            return
        # 审计 B4：先写同目录临时文件，再 ``os.replace`` 原子替换。
        # 直写（``write_text``）会先截断再写入，若恰在这两步之间被硬杀
        # （例如退出收尾超时后 daemon 线程被直接杀掉）就留下半截 JSON ——
        # 而「进程被硬杀」正是本日志要覆盖的场景，所以绝不能非原子写。
        # 临时文件名固定为 ``intent.json.tmp``：本进程内**单实例写入、无并发**
        # ——每个 ``TransferEngine`` 只被单个工作线程使用，引擎各自持有自己的
        # ``IntentJournal``；同一路径由第二个实例并发写属于不受支持的用法。
        tmp_path = self.path.with_name(self.path.name + ".tmp")
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            tmp_path.write_text(
                json.dumps(self._data, ensure_ascii=False, indent=2), encoding="utf-8"
            )
            os.replace(tmp_path, self.path)
        except OSError as exc:
            # 写不进去就停用，避免拖垮主流程（删源仍由校验把关，不依赖本日志）
            self.enabled = False
            try:
                tmp_path.unlink(missing_ok=True)   # 不留半成品临时文件
            except OSError:
                pass
            if self.logger:
                self.logger.warning("意图日志写入失败，已停用：%s", exc)

    # ------------------------------------------------------------- 读取侧
    @staticmethod
    def journal_path(backup_dir: Optional[Path]) -> Optional[Path]:
        return (Path(backup_dir) / JOURNAL_FILENAME) if backup_dir else None

    @staticmethod
    def consume_read_error() -> str:
        """取出并清空最近一次读取意图日志的失败原因（无失败返回 ``""``）。

        与 ``RegistryStore.consume_read_error`` 同构：读走即清空，避免错误
        被归到下一次无关的读取上。
        """
        global _JOURNAL_READ_ERROR
        error = _JOURNAL_READ_ERROR
        _JOURNAL_READ_ERROR = ""
        return error

    @staticmethod
    def last_read_error() -> str:
        """**只读不清**的失败探测：返回最近一次读取意图日志的失败原因（无则不返回 ``""``）。

        用途限定（裁决项）：这是给**测试 / 诊断**用的**无副作用探针**——它
        **不消费**错误状态，可反复调用而不会改变后续行为。**生产环境的错误上报
        一律走 ``consume_read_error()``**（读走即清空），因为上报必须「每个错误恰好
        被发现一次」，用本方法会让同一错误被重复上报、或在错误与下一次读取之间错配。
        换言之：``consume_read_error`` 负责**上报**，本方法只负责**观察**——生产路径
        请勿用本方法替代前者。
        """
        return _JOURNAL_READ_ERROR

    @staticmethod
    def pending(backup_dir: Optional[Path]) -> List[Dict[str, Any]]:
        """读取残留的未完成项；任何读取失败都**不抛异常**，返回空列表。

        但「读不出」分两类、语义完全不同（审计 B4）：

        * ``FileNotFoundError``：没有日志文件 == 干净，正常返回 ``[]``；
        * ``ValueError``（JSON 损坏）/ 其它 ``OSError``（不可读）：**不能**
          等同于「无记录」。本日志存在的意义就是「进程被硬杀后留痕」，而
          被硬杀恰好会在非原子直写下留下半截 JSON；此时若静默返回 ``[]``，
          上层就再也收不到「可能有半完成项目」的信号（那是唯一的收尾信号）。
          因此这类失败写入 ``_JOURNAL_READ_ERROR``，由
          :meth:`consume_read_error` / :meth:`last_read_error` 暴露给上层，
          以便记日志并提示用户。

        每次调用都会先清空上一次的错误，保证错误只归属本次读取。
        """
        global _JOURNAL_READ_ERROR
        _JOURNAL_READ_ERROR = ""

        path = IntentJournal.journal_path(backup_dir)
        if path is None:
            return []
        try:
            text = path.read_text(encoding="utf-8")
        except FileNotFoundError:
            return []                      # 没有文件 ≠ 异常
        except OSError as exc:
            _JOURNAL_READ_ERROR = f"无法读取意图日志 {path}：{exc}"
            return []
        try:
            data = json.loads(text)
        except ValueError as exc:          # 含 json.JSONDecodeError
            _JOURNAL_READ_ERROR = (
                f"意图日志已损坏、无法确认是否有半完成项目（{path}）：{exc}"
            )
            return []
        if not isinstance(data, dict):
            # 合法 JSON 但结构不对（例如被写成数组/数字）：同样算损坏
            _JOURNAL_READ_ERROR = (
                f"意图日志结构异常、无法确认是否有半完成项目（{path}）"
            )
            return []
        snapshot = data.get("snapshot", "")
        result: List[Dict[str, Any]] = []
        for key, entry in (data.get("items") or {}).items():
            if not isinstance(entry, dict):
                continue
            result.append(
                {
                    "key": key,
                    "display": entry.get("display", key),
                    "source": entry.get("source", ""),
                    "target": entry.get("target", ""),
                    "stage": entry.get("stage", "pending"),
                    "snapshot": snapshot,
                    # 审计 R8：该项涉及的注册表 (子键, 值名)；旧日志没有此键 → 空列表
                    "registry": entry.get("registry") or [],
                    # 审计 R9-1：区分「本项没有注册表目标（registry 为空列表）」与
                    # 「这条日志由设 registry 字段之前的老版本写入（压根没这个键）」。
                    # 二者归一后都是 []，调用方据此无法判断能否只回滚未完成项。
                    "registry_known": "registry" in entry,
                }
            )
        return result

    @staticmethod
    def unfinished_registry_targets(backup_dir: Optional[Path]) -> set:
        """返回**未完成**项涉及的注册表 ``(子键, 值名)`` 集合（审计 R8）。

        「已完成」的判定标准是 stage 为 ``source-removed``（源目录已落实，R7 补齐
        了重命名路径与 registry_only 的该阶段）。回滚时只应还原这些**未完成**项
        涉及的注册表值，否则会把本轮**已经成功**（源目录已删除）的项也滚回旧路径
        ——那会让注册表指向一个不存在的目录。

        安全取向：**日志缺失/损坏、或旧日志里没有 ``registry`` 字段时返回空集合**。
        空集合 = 调用方不过滤，保持既有行为，绝不放大破坏面。
        """
        targets: set = set()
        for entry in IntentJournal.pending(backup_dir):
            if entry.get("stage") == "source-removed":
                continue
            for pair in entry.get("registry") or []:
                if isinstance(pair, (list, tuple)) and len(pair) == 2:
                    targets.add((str(pair[0]), str(pair[1])))
        return targets


def _dir_has_entries(path: str) -> bool:
    """目标目录是否已有内容（只探测第一项，不物化整张列表，见审计 P2-8）。"""
    try:
        with os.scandir(fsops._lp(path)) as entries:
            return next(iter(entries), None) is not None
    except OSError:
        return False


# 恢复默认时对「源目录」的防御性拒绝名单。
#
# 为什么需要它：``current`` 是从注册表读出来的，而注册表是**外部可写**的。
# 万一某个值被指到盘根或系统目录，按早期版本的做法（整目录复制后删除）
# 就是把整个盘或系统盘上的 ``\Windows`` 搬进文档目录再删掉——灾难级后果。
# 转移路径不需要这套判断（目标由用户显式选择，且已有 validate_destination 把关），
# 恢复路径的目标是固定的默认位置、无法再拦，所以只能拦源。
#
# 审计 A1：名单**不再写死 C:\**，改为从 ``constants`` 派生（唯一真源 = 系统盘 +
# 相对目录名，见 ``constants.SYSTEM_SENSITIVE_TREES``）。旧实现目标侧已按系统盘
# 动态化（审计 A4），源侧却仍写死 ``C:\Windows`` 等，于是系统盘非 C: 的机器上
# 整道源侧闸门 fail-open。现在两处同源：``_unsafe_source_tree`` 在每次判定时调用
# ``forbidden_source_trees()`` / ``forbidden_source_exact()`` **现算**，故
# ``mock.patch.object(constants, "_SYSTEM_DRIVE", "D:")`` 会立即生效（可测试）。
#
# 注意 ``\Users`` 只做**精确匹配**：用户把自己配置文件下的自定义目录当作资料位置
# 是完全合法的状态，不能因为它在系统盘 ``\Users`` 之下就拒绝。


def _strip_extended_prefix(path: str) -> str:
    r"""剥掉 ``\\?\`` / ``\\?\UNC\`` / ``\\.\`` 之类的 Win32 扩展前缀。

    审计 F3：带前缀的路径**不做** ``.``/``..`` 归一化，``os.path.realpath`` 对
    ``\\?\C:\Windows`` 更是**原样返回**，于是 ``normalize()`` 得到
    ``\\?\c:\windows``，与禁区 ``c:\windows`` 既不等也不满足 ``startswith``
    —— ``C:\Windows`` 被拦、``\\?\C:\Windows`` 却放行。所有参与比较的字符串
    必须先剥掉前缀、回到同一形态。
    """
    cleaned = fsops._unlp(path)
    if cleaned.startswith("\\\\.\\"):
        cleaned = cleaned[4:]
    return cleaned


def _unsafe_source_tree(path: str) -> Tuple[str, str]:
    """源位置**自身**的禁区判定；返回 ``(kind, reason)``，``kind == ""`` 表示可以。

    审计 R3：转移路径与恢复路径的 ``current`` **都**来自外部可写的注册表，威胁
    模型完全相同——注册表被改成 ``C:\\Windows`` 就会被「整目录搬走再删掉」。
    故把判定抽出来共用，**不复制逻辑**；两条路径只是动作措辞不同（「搬运」/
    「搬回」），由调用方按 ``kind`` 自行拼接。

    审计 F3：**在构造候选之前**先把 ``\\?\\`` 前缀剥掉。旧实现直接用原始字符串
    比较，于是 ``\\?\\C:\\Windows`` / ``\\?\\C:\\Program Files`` / ``\\?\\C:\\Users``
    全部被放行；且 ``normalize(realpath(path)) == normalize(path)``（realpath 对
    ``\\?\\`` 路径原样返回）还会误判「无需追加第二候选」，让 R10 的 realpath
    加固**完全空转**。剥前缀后再比较，两个候选就落在同一形态上了。

    kind：``""`` 可以 / ``"invalid"`` 不是合法路径 / ``"root"`` 磁盘根 /
    ``"system"`` 系统目录（含只做精确匹配的 ``\\Users``，名单按系统盘现算，审计 A1）。
    """
    if not path:
        return "", ""
    plain = _strip_extended_prefix(path)
    try:
        absolute = os.path.abspath(plain)
    except (OSError, ValueError):
        return "invalid", f"当前位置不是合法路径（{path}）"

    # 审计 R10：``current`` 也可能是一个**指向禁区**的重解析点（junction / 符号
    # 链接）。``is_within`` / ``same_path`` 都是纯字符串比较，不展开链接——注册表
    # 被改成「指向 C:\Windows 的 junction」时会被误判为「不是系统目录」而放行。
    # 威胁模型与 R3（把 current 改成 C:\Windows）完全相同，故对 ``realpath`` 展开
    # 后的路径再判一遍。展开失败则退回只用原路径（fail-safe：既不因展开失败放行，
    # 也不抛异常）；展开结果与 ``normalize(plain)`` 相同时不重复加候选。
    checks: List[Tuple[str, str]] = [(plain, absolute)]
    try:
        resolved = os.path.realpath(plain)
    except (OSError, ValueError):
        resolved = ""
    if resolved:
        resolved = _strip_extended_prefix(resolved)
        if fsops.normalize(resolved) != fsops.normalize(plain):
            try:
                checks.append((resolved, os.path.abspath(resolved)))
            except (OSError, ValueError):
                pass

    # 审计 F3：8.3 短名同样要在**剥前缀之后**还原长名，否则 ``\\?\C:\PROGRA~1``
    # 这类混合形态会绕过「不许搬系统目录」的闸门。
    for candidate, candidate_abs in list(checks):
        try:
            long_form = _strip_extended_prefix(fsops.long_path(candidate))
        except (OSError, ValueError):
            continue
        if fsops.normalize(long_form) != fsops.normalize(candidate):
            try:
                checks.append((long_form, os.path.abspath(long_form)))
            except (OSError, ValueError):
                pass

    for candidate, candidate_abs in checks:
        if os.path.dirname(candidate_abs) == candidate_abs:
            return "root", f"当前位置是磁盘根目录（{path}）"
        # 审计 A1：名单按**当前**系统盘现算（与 constants.FORBIDDEN_TARGET_ROOTS
        # 同源），不再写死 C:\——系统盘非 C: 的机器上也拦得住。
        for root in forbidden_source_trees():
            if fsops.is_within(candidate, root):
                return "system", f"当前位置在系统目录 {root} 内"
        for root in forbidden_source_exact():
            if fsops.same_path(candidate, root):
                return "system", f"当前位置就是系统目录 {root}"
    return "", ""


def _unsafe_restore_source(path: str, target: str = "") -> str:
    """返回拒绝搬回的原因；空字符串表示可以搬。

    ``target`` 为本次恢复的目标（系统默认目录）。传入时还会拒绝**源与目标
    互为祖先/后代**的组合——这类组合会自我嵌套复制（目标在源内）或在删源时
    连带删掉默认目录（源在目标内）。这是 ``validate_destination`` 里最关键的
    一条检查；恢复路径因默认目标位于 ``C:\\Users\\<用户>`` 下而**不能**整体
    套用 ``validate_destination``（那个目录对转移路径是禁区），但**不能因此
    丢掉这条互斥检查**。

    源自身的禁区判定复用 :func:`_unsafe_source_tree`（审计 R3）。
    """
    kind, reason = _unsafe_source_tree(path)
    if kind == "invalid":
        return f"{reason}，拒绝搬回"
    if kind == "root":
        return f"{reason}，不能整体搬回系统默认位置"
    if kind:
        return f"{reason}，不能搬回"
    if target and not fsops.same_path(path, target):
        if fsops.is_within(target, path):
            return (
                f"系统默认位置 {target} 位于当前位置 {path} 内部，"
                "搬回会自我嵌套复制，不能执行"
            )
        if fsops.is_within(path, target):
            return (
                f"当前位置 {path} 位于系统默认位置 {target} 内部，"
                "搬回后删源会连带删除默认位置的内容，不能执行"
            )
    return ""


class TransferEngine:
    """转移引擎。所有注册表读写经 ``RegistryStore``，便于替换为内存后端。"""

    def __init__(
        self,
        store: Optional[RegistryStore] = None,
        backup_dir: Optional[Path] = None,
        dry_run: bool = False,
        logger=None,
    ):
        self.store = store or RegistryStore()
        self.backup_dir = Path(backup_dir) if backup_dir else None
        self.dry_run = dry_run
        self.log = logger
        self.journal = IntentJournal.open(
            self.backup_dir, enabled=not dry_run, logger=logger
        )

    # ------------------------------------------------------------- 内部工具
    def _log(self, message: str, level: str = "info") -> None:
        if self.log:
            getattr(self.log, level, self.log.info)(message)

    def resolve_current(self, spec: FolderSpec) -> str:
        """确定某项当前的实际路径。

        优先级：权威值名 → 别名（GUID）→ 系统默认值。

        审计 P2-3：读取失败（非「值不存在」）时 ``RegistryStore`` 会记录并告警；
        这里把该告警转成本引擎日志，让「静默回落到系统默认路径」变得可见。
        对外仍保留回退行为（不阻断），只是不再无声无息。
        """
        self.store.consume_read_error()   # 清掉上一项遗留，保证错误归属本次读取
        raw = self.store.get_raw(spec.write_subkey, spec.value_name)
        if raw and isinstance(raw[0], str) and raw[0].strip():
            return fsops.expand(raw[0])

        for alias in spec.aliases:
            value = self.store.get_raw(spec.write_subkey, alias)
            if value and isinstance(value[0], str) and value[0].strip():
                return fsops.expand(value[0])

        error = self.store.consume_read_error()
        if error:
            self._log(
                f"[{spec.display}] 注册表读取异常，已回退到系统默认位置"
                f"（{spec.default}）：{error}",
                "warning",
            )
        return fsops.expand(spec.default)

    def _registry_targets(self, spec: FolderSpec) -> List[Tuple[str, str]]:
        """列出需要写入的 ``(子键, 值名)``。

        规则：
            * 权威值名总是写入；
            * 环境变量项（TEMP/TMP）的别名**无条件**同步 —— 二者必须成对，
              否则会出现「TEMP 已改、TMP 未改」的不一致状态
              （审计 P3-1：当前 ``FOLDERS`` 无 environment 项，此分支为**预留**
              能力，将来新增 TEMP/TMP 项即自动生效，故保留而非删除）；
            * 其余别名仅在注册表中已存在时同步（不凭空创建）；
            * Explorer 项额外同步 ``Shell Folders`` 中已存在的同名值，
              使改动无需重启资源管理器即可生效。

        审计 P2-13（保留此行为的理由与风险）：``Shell Folders`` 由 Explorer 维护、
        属缓存性质，权威键是 ``User Shell Folders``；主动写它并非官方推荐，系统
        可能在下次刷新时**覆盖回去**。但仍保留该行为，因为：
            * 它是「无需重启资源管理器即可生效」的关键（否则要注销/重启）；
            * 只写**已存在**的值、绝不凭空创建，暴露面最小；
            * 即使被系统覆盖，也只是「当前会话回退到旧位置」，不涉及数据丢失。
        如将来需要停用，把模块开关 ``SYNC_SHELL_FOLDERS`` 置 ``False`` 即可
        （届时只写权威键，需重启资源管理器生效）。**不要**改成只写权威键——
        那会破坏免重启生效这一核心体验。
        """
        targets: List[Tuple[str, str]] = [(spec.write_subkey, spec.value_name)]

        for alias in spec.aliases:
            if spec.is_environment or self.store.exists(spec.write_subkey, alias):
                targets.append((spec.write_subkey, alias))

        if SYNC_SHELL_FOLDERS and not spec.is_environment:
            if self.store.exists(SHELL_FOLDERS_SUBKEY, spec.value_name):
                targets.append((SHELL_FOLDERS_SUBKEY, spec.value_name))
            for alias in spec.aliases:
                if self.store.exists(SHELL_FOLDERS_SUBKEY, alias):
                    targets.append((SHELL_FOLDERS_SUBKEY, alias))

        # 去重且保持顺序
        seen = set()
        unique: List[Tuple[str, str]] = []
        for subkey, name in targets:
            marker = (subkey.lower(), name.lower())
            if marker in seen:
                continue
            seen.add(marker)
            unique.append((subkey, name))
        return unique

    # --------------------------------------------------------------- 规划
    def build_plan(
        self,
        selected: Sequence[str],
        destination: str,
        create_user_dir: bool = True,
        scan: bool = True,
        stat_cache: Optional[Dict[str, fsops.TreeStat]] = None,
    ) -> List[PlanItem]:
        """为选中的项目生成可审阅的计划（只读，不改动任何东西）。

        ``scan=False`` 时跳过目录规模统计（界面渲染用，避免大目录卡顿）。

        ``stat_cache``（审计 P2-7）是**按文件夹 key** 缓存的规模统计，通常由界面
        在预统计阶段（主线程）算好、执行前在主线程读好再传入。命中即直接复用其
        ``TreeStat``，未命中的项照常扫描。复用不会削弱安全语义：
            * 规模统计只用于「展示」与「空间估算」，真正的复制集合由
              :func:`fsops.copy_merge` 在执行时**权威重扫**决定，删源前还要
              :func:`fsops.verify_copy` 再对照一次源与目标——**数据安全最终由这
              两步兜底，与是否命中缓存无关**；
            * ``blocking_reason()`` 在执行前的复核是「部分实时」的：目标盘可用空间
              （``fsops.free_space``）每次**现查**；而 ``unreadable`` 计数取自已命中
              的缓存快照（缓存项的不可读项**不会**被重新枚举，故可能偏旧）。
              这只影响「是否放行」的前置判断——一旦放行，``copy_merge`` 的权威重扫
              遇到任何读不到的项仍会整体失败，源目录不会被删除，数据不会丢。

        目标路径规则：

        * 勾选「创建当前用户目录」→ ``<目标根>/<用户名>/<文件夹基名>``；
        * 未勾选 → ``<目标根>/<文件夹基名>``。
        """
        dest_root = fsops.expand(destination).strip().rstrip("\\")
        username = os.environ.get("USERNAME", "") if create_user_dir else ""
        plan: List[PlanItem] = []

        for key in selected:
            spec = FOLDERS_BY_KEY.get(key)
            if spec is None:
                continue

            current = self.resolve_current(spec)
            if dest_root:
                leaf = fsops.tail_name(current) or spec.display
                target = os.path.join(dest_root, username, leaf) if username else os.path.join(dest_root, leaf)
            else:
                target = current

            item = PlanItem(
                spec=spec,
                current=current,
                target=target,
                registry_value=target,
                registry_targets=self._registry_targets(spec),
            )
            item.same = fsops.same_path(current, target)
            item.target_exists = os.path.isdir(fsops._lp(target))

            # 审计 P2-3 / 问题#4：_registry_targets 期间若发生注册表读取失败，
            # 说明「GUID 别名是否已存在」无法确认——若此时照常执行，本该同步的
            # 别名会被漏掉，直接削弱 P1-4「权威值名与别名同进同退」。取向是
            # 「宁可显式失败，不可静默降级」：把该项标为受阻，执行前由
            # blocking_reason() 拦下，绝不留下「权威值名已改、别名未改」的
            # 不一致状态。消费错误也让 _registry_targets 内部的 exists() 读失败
            # 不再被伪装成一个干净的 False。
            residual = self.store.consume_read_error()
            if residual:
                self._log(f"[{spec.display}] 注册表读取告警：{residual}", "warning")
                item.invalid_reason = (
                    "读取注册表失败，无法确认需要同步的别名，"
                    "已停止该项以免留下不一致状态"
                )

            if dest_root and not item.same:
                # P1-5：引擎层也做一次目标校验。原先这条防线只存在于 UI 的一次
                # 调用里，run.py --dry-run 与任何直接构造引擎的脚本都能绕过。
                ok, reason = fsops.validate_destination(target, [current])
                if not ok:
                    item.invalid_reason = reason

            # 审计 R3：**源位置**的禁区校验（与恢复路径共用 `_unsafe_source_tree`）。
            # current 同样来自注册表、属外部可写数据；旧实现只在恢复路径设防，
            # 转移路径会把注册表被改成 `C:\Windows` 之类的值照常
            # 复制 → 校验 → 写注册表 → **删源**。
            if not item.same and not item.invalid_reason:
                kind, reason_text = _unsafe_source_tree(current)
                if kind:
                    self._log(f"[{spec.display}] {reason_text}，拒绝搬运", "warning")
                    item.invalid_reason = f"{reason_text}，拒绝搬运（可能是注册表被改动）"

            if not item.same and os.path.isdir(fsops._lp(current)):
                item.merge = item.target_exists and _dir_has_entries(target)
                if scan:
                    # P2-7：命中预统计缓存则直接复用，避免执行前再全量扫一遍
                    cached = stat_cache.get(spec.key) if stat_cache else None
                    item.stat = cached if cached is not None else fsops.scan_tree(current)
                    item.scanned = True

            plan.append(item)

        return plan

    # --------------------------------------------------------------- 执行
    def _write_registry(self, item: PlanItem, value: str) -> Tuple[bool, str, int]:
        """写入该项目的全部注册表值。返回 ``(成功, 消息, 写入条数)``。

        审计 P1-4：同一文件夹的权威值名与其 GUID 别名必须同进同退，否则会
        出现「资源管理器显示 A、系统实际认 B」。这里写入前先取本项快照，
        任一值写入失败即把本项**已写入的值就地回滚**。
        """
        snapshot = self.store.snapshot(item.registry_targets)
        written = 0
        for subkey, name in item.registry_targets:
            try:
                self.store.set_text(subkey, name, value)
                written += 1
            except OSError as exc:
                try:
                    restored, removed = self.store.rollback(snapshot)
                    rolled = restored + removed
                except OSError as rollback_exc:
                    # 注册表整体不可写时回滚也会失败，此时如实上报而不是抛出去
                    return (
                        False,
                        f"写入注册表失败 {subkey}\\{name}：{exc}"
                        f"（回滚同样失败：{rollback_exc}）",
                        written,
                    )
                note = f"，已回滚本项 {rolled} 处注册表值" if rolled else ""
                # 审计 R11：回滚时可能因「快照期读失败、原值未知」而跳过若干处
                # （registry.rollback() 记在 store.last_rollback_skipped，调用开头
                # 已重置，此处直接读）。不报出来就是「报成功但没做」——用户会以为
                # 全回滚到位。为 0 时**文案逐字不变**，不引入空段。
                skipped = self.store.last_rollback_skipped
                if skipped:
                    note += f"，另有 {skipped} 处因快照读取失败被跳过、未做改动"
                return False, f"写入注册表失败 {subkey}\\{name}：{exc}{note}", written
        return True, f"已更新 {written} 处注册表值", written

    @staticmethod
    def _registry_payload(item: PlanItem) -> str:
        """真正写入注册表的值。

        转移时是目标的绝对路径；恢复默认时保留 ``%USERPROFILE%`` 形式，
        与系统原生写法一致（用户改名或再次迁移后仍然正确）。
        """
        return item.registry_value or item.target

    def _save_snapshot(self, targets: Sequence[Tuple[str, str]]) -> Optional[str]:
        """保存注册表快照到磁盘，供一键回滚；失败时**不抛**，返回 ``None``。

        审计 R4：快照是**附加保险**，不该否决一次已经完成的搬运。旧实现让
        ``mkdir``/``write_text`` 的 ``OSError`` 直接冒到 ``execute`` 之外，于是
        「程序所在盘只读/占满」会让整轮转移在开始前就中断（哪怕只是想搬一项）。
        这里与 ``IntentJournal._flush`` 的「停用并继续」口径保持一致：记 warning、
        返回 ``None``，并让调用方把「注册表备份未能落盘」告知用户。

        注意：删源安全**不依赖**本快照——它由 ``verify_copy`` 把关。
        """
        if self.dry_run or self.backup_dir is None:
            return None
        snapshot = self.store.snapshot(targets)
        try:
            self.backup_dir.mkdir(parents=True, exist_ok=True)
            stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
            path = self.backup_dir / f"registry-{stamp}.json"
            path.write_text(
                json.dumps(snapshot.to_dict(), ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
        except OSError as exc:
            self._log(
                f"注册表快照写入失败，本次将无法一键回滚（不影响搬运本身）：{exc}",
                "warning",
            )
            return None
        self._log(f"注册表快照已保存：{path}")
        return str(path)

    def execute(
        self,
        plan: Sequence[PlanItem],
        on_item_start: Optional[ItemCallback] = None,
        on_item_done: Optional[ItemCallback] = None,
        on_progress: Optional[ProgressCallback] = None,
        is_cancelled: Optional[CancelCallback] = None,
    ) -> TransferReport:
        """执行计划。每项都走「移动 → 校验 → 写注册表」的安全序列。"""
        report = TransferReport(started_at=datetime.now().isoformat(timespec="seconds"))

        snapshot_targets: List[Tuple[str, str]] = []
        for item in plan:
            snapshot_targets.extend(item.registry_targets)
        if not self.dry_run:
            report.snapshot_path = self._save_snapshot(snapshot_targets)
            if report.snapshot_path is None and self.backup_dir is not None:
                # 审计 R4：走到这里说明「尝试写备份但失败」（dry_run / 无备份目录
                # 会在 _save_snapshot 里直接返回 None，不算失败）。备份是附加保险，
                # 不阻断搬运，但必须让用户知道本次无法一键回滚。
                report.notes.append("注册表备份未能落盘，本次无法一键回滚")
            # P0-4：登记本次事务。文件残留即表示上次运行被中断
            self.journal.begin(plan, report.snapshot_path)

        for index, item in enumerate(plan):
            if is_cancelled and is_cancelled():
                report.cancelled = True
                for rest in plan[index:]:
                    report.outcomes.append(
                        ItemOutcome(rest.key, rest.display, ST_CANCELLED, "用户取消")
                    )
                break

            if on_item_start:
                on_item_start(item)

            try:
                outcome = self._execute_item(item, on_progress, is_cancelled)
            except Exception as exc:   # 单项异常不应中止整轮，也不应吞掉其余项
                self._log(
                    f"[{item.display}] 处理时发生未预期错误：{type(exc).__name__}: {exc}",
                    "error",
                )
                outcome = ItemOutcome(
                    key=item.key,
                    display=item.display,
                    status=ST_FAILED,
                    message=f"未预期错误：{type(exc).__name__}: {exc}",
                    source=item.current,
                    target=item.target,
                )
                self.journal.mark(item, "unexpected-error")
            report.outcomes.append(outcome)

            if on_item_done:
                on_item_done(item)

            if outcome.status == ST_CANCELLED:
                report.cancelled = True
                for rest in plan[index + 1:]:
                    report.outcomes.append(
                        ItemOutcome(rest.key, rest.display, ST_CANCELLED, "用户取消")
                    )
                break

        self.journal.close_run()
        report.finished_at = datetime.now().isoformat(timespec="seconds")
        return report

    def _execute_item(
        self,
        item: PlanItem,
        on_progress: Optional[ProgressCallback],
        is_cancelled: Optional[CancelCallback],
    ) -> ItemOutcome:
        outcome = ItemOutcome(
            key=item.key,
            display=item.display,
            status=ST_PENDING,
            source=item.current,
            target=item.target,
        )

        if item.same:
            outcome.status = ST_SKIPPED
            outcome.message = "当前位置与目标相同，无需转移"
            return outcome

        if item.registry_only:
            # 恢复默认专用（转移路径从不设置该标志）。放在 blocking_reason()
            # 之前：此分支的前提**正是**「源目录不存在」，所以它不是错误条件，
            # 而是「没有文件要搬、只剩注册表要修」这一情形的入口。
            return self._execute_registry_only(item)

        reason = item.blocking_reason()
        if reason:
            outcome.status = ST_FAILED
            outcome.message = reason
            self._log(f"[{item.display}] 前置检查未通过：{reason}", "warning")
            return outcome

        if self.dry_run:
            outcome.status = ST_OK
            outcome.message = f"[试运行] 将转移 {item.stat.describe()} 至 {item.target}"
            return outcome

        # 最终审计 P1-1：**执行期**复查目标祖先链是否为重解析点。
        #
        # 规划期已由 ``validate_destination``（build_plan 内）查过一遍，但两次调用
        # 之间隔着 ``build_plan(scan=True)`` 的全量枚举——48.7 万文件规模下是**分钟级**
        # 的窗口。攻击者只在这段时间里用 ``mklink /J``（**不需要管理员**）把目标的
        # 某一级上级换成联接点，接下来的建目录 / 原子重命名 / 复制就都会跟随链接落到
        # 别处，最后连源目录一起删掉。``copy_merge`` 原先只看目标**自身**与其**子目录**，
        # ``try_atomic_move`` 只看「目标是否存在」，都不看祖先链。
        #
        # 闸门放在 ``ensure_directory`` **之前**：一旦建了目录，路径就已经穿越链接了。
        # 此处失败与其余前置失败同口径——**不删源、不写注册表**，且因为登记阶段仍是
        # ``begin()`` 写下的 ``pending``，``close_run()`` 会把它当「未开始」正常注销，
        # 不会在下次启动时变成一条假的「上次未正常收尾」提示。
        blocked = fsops.reparse_ancestor(item.target)
        if blocked:
            outcome.status = ST_FAILED
            outcome.message = (
                f"目标路径或其上级目录成了链接/联接点（{blocked}）："
                "数据会被写到链接指向的位置，已停止该项（源目录未改动）"
            )
            self._log(f"[{item.display}] {outcome.message}", "error")
            return outcome

        ok, message = fsops.ensure_directory(os.path.dirname(os.path.abspath(item.target)))
        if not ok:
            outcome.status = ST_FAILED
            outcome.message = message
            return outcome

        stat = fsops.TreeStat()
        by_rename = False
        copy_note = ""   # 审计 P2-5：稀疏/硬链接的显式提示，避免用户误判体积
        # 每项执行前先登记意图：进程被杀后能据此发现半完成的项目（P0-4）
        self.journal.mark(item, "pending")

        # 路径 A：同盘且目标不存在 —— 原子重命名，最快且无中间态
        ok, message = fsops.try_atomic_move(item.current, item.target)
        if ok:
            by_rename = True
            stat = item.stat
            self.journal.mark(item, "copied")
            self._log(f"[{item.display}] {message}")
        else:
            self._log(f"[{item.display}] {message}")
            self.journal.mark(item, "copying")

            # 路径 B：复制 → **源 ↔ 目标全量校验** → 写注册表 → 删源
            result = fsops.copy_merge(
                item.current,
                item.target,
                on_progress=on_progress,
                is_cancelled=is_cancelled,
            )
            if result.cancelled:
                outcome.status = ST_CANCELLED
                outcome.message = "用户取消，源文件未删除"
                self.journal.mark(item, "cancelled")
                return outcome

            if not result.ok:
                outcome.status = ST_FAILED
                outcome.message = "复制失败：" + "；".join(result.errors[:3])
                if len(result.errors) > 3:
                    # 审计 B-3：界面仍只截 3 条，但要说清「共多少条」
                    outcome.message += f"（共 {len(result.errors)} 项，以下仅列前 3 条）"
                # 审计 B-2：README 承诺「过程详情只写入 logs 文件」，而这个分支曾
                # 是全函数**唯一**不落日志的失败出口 —— 第 4 条起的错误只在 UI 上
                # 出现、日志里查不到。这里写全量。
                self._log(
                    f"[{item.display}] 复制失败，共 {len(result.errors)} 项："
                    + "；".join(result.errors),
                    "error",
                )
                self.journal.mark(item, "copy-failed")
                return outcome

            # 审计 P1：进度与取消**必须**传进校验阶段。旧实现这里空着两个形参，
            # 于是几十分钟的全量校验期间进度条冻结、「停止」按钮毫无反应。
            try:
                verified, detail = fsops.verify_copy(
                    item.current,
                    item.target,
                    result,
                    on_progress=on_progress,
                    is_cancelled=is_cancelled,
                )
            except fsops.CopyCancelled:
                # 与「复制阶段取消」完全同口径：不删源、按 ST_CANCELLED 收尾
                outcome.status = ST_CANCELLED
                outcome.message = "用户取消，源文件未删除"
                self.journal.mark(item, "cancelled")
                return outcome

            if not verified:
                outcome.status = ST_FAILED
                outcome.message = f"复制校验未通过，已保留源目录（{detail}）"
                self.journal.mark(item, "verify-failed")
                self._log(f"[{item.display}] 校验失败：{detail}", "error")
                return outcome

            stat = fsops.TreeStat(files=result.copied_files, bytes=result.copied_bytes)
            # 审计 P2-5：显式告知用户「有稀疏文件/硬链接被特殊处理」，绝不静默
            hints: List[str] = []
            if result.sparse_files:
                hints.append(f"{result.sparse_files} 个稀疏文件（零区不占实体空间）")
            if result.hardlinked_files:
                hints.append(f"{result.hardlinked_files} 个硬链接（未重复占用空间）")
            if hints:
                copy_note = "；" + "，".join(hints)
                self._log(f"[{item.display}] " + "，".join(hints))
            self.journal.mark(item, "copied")

        # 先写注册表（此时目标已校验通过），成功之后才删除源目录（P0-4）：
        # 顺序反过来会留下「源已删、注册表仍指向旧路径」的孤儿状态。
        ok, message, written = self._write_registry(item, self._registry_payload(item))
        outcome.files = stat.files
        outcome.copied_bytes = stat.bytes

        if not ok:
            # 复制路径下源目录还在，无需回滚（两边都有数据，只是注册表没改）
            rollback_note = self._rollback_move(item) if by_rename else ""
            outcome.status = ST_FAILED
            if by_rename:
                outcome.message = f"{message}{rollback_note}"
            else:
                outcome.message = (
                    f"{message}；源目录已保留，目标副本位于 {item.target}，"
                    "修正后重试即可"
                )
            self.journal.mark(item, "registry-failed")
            return outcome
        self.journal.mark(item, "registry-written")

        if by_rename:
            # 审计 R7：重命名路径下源目录在 rename 那一刻就已被移走（等价于已删除）。
            # 此前这里跳过 source-removed 阶段，导致「已彻底完成」的项在日志里与
            # 「注册表已写、源未删」的半成品无法区分——而 R8 的
            # ``unfinished_registry_targets`` 正是靠 source-removed 判定「已完成、
            # 无需回滚」。
            self.journal.mark(item, "source-removed")
        else:
            # 审计 R2：删源前的最后两道闸门。verify_copy 只覆盖到「它自己那次枚举」
            # 为止，此后还有「写注册表」这段时间没有任何把关。
            #
            # ① 源与目标是否实际指向**同一物理位置**（穿透 junction/符号链接/subst）
            #    —— 若成立，复制会因为「目标已有一致内容」而全部命中复用、校验通过，
            #    随后删源就会把用户真实目录删掉、注册表指向悬空路径。
            if fsops.resolves_to_same(item.current, item.target):
                outcome.status = ST_FAILED
                outcome.message = (
                    "源与目标实际指向同一位置，为安全起见保留源目录"
                    f"（{item.current}）；注册表已指向 {item.target}，"
                    "如需回退可用备份快照回滚"
                )
                self.journal.mark(item, "source-removal-failed")
                self._log(f"[{item.display}] {outcome.message}", "error")
                return outcome

            # ② 源目录的**名字集合 + (大小, 修改时间)** 必须仍与清单一致。
            #
            #    审计 F4：旧实现只比名字集合，于是「校验读完某文件 → 该文件被原地
            #    改写为同长度内容、并把 mtime_ns 改回原值（**不需要管理员**）→
            #    删源」这一串操作完全看不见，最后只剩旧副本、新版本被销毁。窗口
            #    宽度 = 从该文件被读完到整树删完，几十 GB 文档目录上是分钟级。
            #    补上指纹比对后，这类改写会在此被拦下、源目录保留。
            #
            #    次数持平（没有新增第五次全量枚举）。**刻意没有**按"复用校验阶段
            #    那次枚举"去做：那会让本闸门的观测时刻回退到 verify 内部，之后
            #    「写注册表」这段时间里发生的**任何**改动都再也看不见，等于把这道
            #    闸门降级成 verify 的一个重复断言（枚举集合完全相同、连观测时刻也
            #    相同，抓不到 R2② 原本要抓的东西）。详细权衡见交付报告 §P2(d)。
            fresh = fsops.enumerate_source(item.current)
            if fresh.errors:
                outcome.status = ST_FAILED
                outcome.message = (
                    "删源前无法完整重新清点源目录，为安全起见保留源目录："
                    f"{item.current}；注册表已指向 {item.target}，"
                    "如需回退可用备份快照回滚"
                )
                self.journal.mark(item, "source-removal-failed")
                self._log(f"[{item.display}] {outcome.message}", "error")
                return outcome

            expected = {
                i.rel: (i.fingerprint.size, i.fingerprint.mtime_ns)
                for i in result.items
            }
            actual = {rel: (fp.size, fp.mtime_ns) for rel, fp in fresh.files.items()}
            added = sorted(set(actual) - set(expected))
            removed = sorted(set(expected) - set(actual))
            rewritten = sorted(
                rel
                for rel in set(expected) & set(actual)
                if expected[rel] != actual[rel]
            )
            if added or removed or rewritten:
                bits: List[str] = []
                if added:
                    bits.append(f"新增 {len(added)} 项（如 {added[:2]}）")
                if removed:
                    bits.append(f"少了 {len(removed)} 项（如 {removed[:2]}）")
                if rewritten:
                    bits.append(f"{len(rewritten)} 项被改写（如 {rewritten[:2]}）")
                outcome.status = ST_FAILED
                outcome.message = (
                    "源目录在复制期间发生变化（" + "，".join(bits) + "），"
                    f"为安全起见保留源目录：{item.current}；"
                    f"注册表已指向 {item.target}，如需回退可用备份快照回滚"
                )
                self.journal.mark(item, "source-removal-failed")
                self._log(f"[{item.display}] {outcome.message}", "warning")
                return outcome

            removed, remove_message = fsops.safe_remove_directory(item.current)
            if not removed:
                outcome.status = ST_FAILED
                outcome.message = (
                    f"{message}；数据已完整复制并校验通过，但清理源目录失败："
                    f"{remove_message}。可手动删除 {item.current}"
                )
                self.journal.mark(item, "source-removal-failed")
                self._log(f"[{item.display}] {outcome.message}", "warning")
                return outcome
            self.journal.mark(item, "source-removed")

        outcome.status = ST_OK
        if stat.files:
            outcome.message = f"{message}；已转移 {stat.describe()}{copy_note}"
        else:
            outcome.message = f"{message}{copy_note}"
        self.journal.finish(item)
        self._log(f"[{item.display}] 完成：{outcome.message}")
        return outcome

    def _execute_registry_only(self, item: PlanItem) -> ItemOutcome:
        """只修正注册表，**不触碰任何文件**（``registry_only`` 项专用）。

        什么时候走到这里：恢复默认时注册表指向的目录已经不存在。最常见的成因
        是用户早已手工把资料搬回了原位、只是注册表还留着旧的转移路径。

        与早期版本的差别（有意为之）：早期版本在这种情况下若「当前路径不存在」
        就直接跳过整个项目，注册表于是**永远修不回来**，系统一直指着一个
        不存在的目录。这里选择把注册表改对——这不会移动或删除任何文件。
        """
        outcome = ItemOutcome(
            key=item.key,
            display=item.display,
            status=ST_PENDING,
            source=item.current,
            target=item.target,
        )
        if self.dry_run:
            outcome.status = ST_OK
            outcome.message = (
                f"[试运行] 将把注册表指回 {item.target}"
                "（当前位置已不存在，没有文件需要搬回）"
            )
            return outcome

        ok, message, _written = self._write_registry(item, self._registry_payload(item))
        if ok:
            # 审计 R7：registry_only 的前提**正是**「源目录已不存在」，故用
            # `source-removed` 表示该项已完全落实——R8 以此判定「无需回滚」。
            # 失败时记为 `registry-failed`（注册表可能只写了一半，必须参与回滚）。
            self.journal.mark(item, "source-removed")
            outcome.status = ST_OK
            outcome.message = f"{message}；当前位置已不存在，没有文件需要搬回"
            self._log(f"[{item.display}] 完成：{outcome.message}")
        else:
            self.journal.mark(item, "registry-failed")
            outcome.status = ST_FAILED
            outcome.message = message
            self._log(f"[{item.display}] {message}", "warning")
        return outcome

    def _rollback_move(self, item: PlanItem) -> str:
        """原子重命名路径下，注册表写入失败时把目录移回原位。

        审计 P2-12：合并项的目标里含有用户原有内容，整目录搬回会把那些内容
        一起带走，因此对合并项不做整目录回滚（复制路径本来也不需要回滚，
        因为源目录一直保留到最后一步）。

        审计 R5：**任何分支都不再返回空串**。重命名成功后源目录已经被移动到
        目标，用户此时收到「注册表写入失败」却不知道数据去了哪里，可能误以为
        数据丢失并重复搬迁。回滚成功、无法回滚、合并项三种情形都给出确切去向。
        """
        if item.merge:
            return (
                f"；数据已在 {item.target}（合并项不做整目录回滚），"
                "注册表未改动，请勿重复搬迁"
            )
        try:
            if os.path.isdir(item.target) and not os.path.exists(item.current):
                parent = os.path.dirname(os.path.abspath(item.current))
                if fsops.volume_key(item.target) == fsops.volume_key(parent):
                    os.makedirs(parent, exist_ok=True)
                    os.rename(item.target, item.current)
                    return f"；已把 {item.target} 改回 {item.current}"
        except OSError as exc:
            return f"；自动回滚失败，数据仍位于 {item.target}（{exc}）"
        return (
            f"；数据已在 {item.target}，注册表未改动，请勿重复搬迁"
        )

    # ------------------------------------------------------- 恢复系统默认
    def build_restore_plan(
        self,
        selected: Sequence[str],
        scan: bool = True,
        stat_cache: Optional[Dict[str, fsops.TreeStat]] = None,
    ) -> List[PlanItem]:
        """为「恢复系统默认值」生成计划（只读，不改动任何东西）。

        与早期版本（整目录复制后删除、把文件搬回）的差别，以及
        为什么这样设计：

        * **目标是系统出厂默认路径**（``spec.default``，含 ``%USERPROFILE%``），
          不做「目标根目录」拼接，因此**不适用** ``validate_destination``——
          默认位置本来就在 ``C:\\Users\\<当前用户>`` 下，而那正是转移时必须
          拒绝写入的系统目录。
        * 注册表写入值保留 ``%USERPROFILE%`` 形式，见 :meth:`_registry_payload`。
        * ``current`` 取自注册表、属**外部可写**数据，先过
          :func:`_unsafe_restore_source` 做防御性拒绝（盘根 / 系统目录 /
          **源与目标互为祖先或后代**）；早期版本没有这道闸门。
        * 源目录不存在时标记 ``registry_only``——没有文件要搬，只修注册表
          （早期版本在这种情况下会跳过整个项目，注册表永远修不回来）。
        * 文件是否搬得回去由 :meth:`PlanItem.blocking_reason` 逐项把关：
          空间不足的项**只阻断该项**，其余项照常执行（产品决策：宁可少搬一项，
          也不因一项放不下而全部停下）。
        """
        plan: List[PlanItem] = []

        for key in selected:
            spec = FOLDERS_BY_KEY.get(key)
            if spec is None:
                continue

            current = self.resolve_current(spec)
            target = fsops.expand(spec.default)

            item = PlanItem(
                spec=spec,
                current=current,
                target=target,
                registry_value=spec.default,
                registry_targets=self._registry_targets(spec),
            )
            # 修正一处历史缺陷：旧实现**从未计算** item.same，于是
            # 「已经是系统默认位置」的分支是死代码、每次都重写一遍注册表。
            item.same = fsops.same_path(current, target)
            item.target_exists = os.path.isdir(fsops._lp(target))

            residual = self.store.consume_read_error()
            if residual:
                self._log(f"[{spec.display}] 注册表读取告警：{residual}", "warning")
                item.invalid_reason = (
                    "读取注册表失败，无法确认需要同步的别名，"
                    "已停止该项以免留下不一致状态"
                )

            if not item.same and not item.invalid_reason:
                unsafe = _unsafe_restore_source(current, target)
                if unsafe:
                    item.invalid_reason = unsafe

            if not item.same and not item.invalid_reason:
                if not os.path.isdir(fsops._lp(current)):
                    item.registry_only = True
                else:
                    item.merge = item.target_exists and _dir_has_entries(target)
                    if scan:
                        cached = stat_cache.get(spec.key) if stat_cache else None
                        item.stat = (
                            cached if cached is not None else fsops.scan_tree(current)
                        )
                        item.scanned = True

            plan.append(item)

        return plan

    def restore_defaults(
        self,
        keys: Sequence[str],
        on_item_start: Optional[ItemCallback] = None,
        on_item_done: Optional[ItemCallback] = None,
        on_progress: Optional[ProgressCallback] = None,
        is_cancelled: Optional[CancelCallback] = None,
        stat_cache: Optional[Dict[str, fsops.TreeStat]] = None,
    ) -> TransferReport:
        """把选中的项**完整恢复**为系统默认：注册表 + 文件搬回。

        执行体直接复用 :meth:`execute`，因此天然继承全部安全语义：

            复制（含空目录与链接重建）→ **源 ↔ 目标全量校验** → 写注册表 → 删源

        也就是说，**校验没通过就绝不删源**——这正是早期版本「整目录复制后不看
        返回码就删除源」的丢数据缺陷所在，本实现把它补上。任一项失败时源目录与
        注册表都保持原样，不会留下「注册表已改、文件没搬」的半成品。

        每项在动手前先做本地空间检查（``blocking_reason``，含 5% + 64 MB 余量），
        空间不足的项单独阻断并报出缺口，**不影响其余项**。
        """
        plan = self.build_restore_plan(keys, scan=True, stat_cache=stat_cache)
        return self.execute(
            plan,
            on_item_start=on_item_start,
            on_item_done=on_item_done,
            on_progress=on_progress,
            is_cancelled=is_cancelled,
        )


def load_snapshot(path: str | Path) -> RegistrySnapshot:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    return RegistrySnapshot.from_dict(data)


def restrict_registry_snapshot(
    snapshot: RegistrySnapshot, targets: Iterable[Tuple[str, str]]
) -> Tuple[RegistrySnapshot, int]:
    """把快照裁剪为只含 ``targets`` 里的条目；返回 ``(裁剪后的快照, 被排除的条目数)``。

    ``targets`` 的元素是 ``(子键, 值名)``，比较时**统一小写**（注册表键名大小写不敏感）。
    返回的新快照保留原 ``created_at``。被排除的通常是「本轮已成功、源目录已删除」的项
    ——它们不该被滚回旧路径。

    审计 R9-2：**纯函数**——不读日志、不做 IO、无副作用。整轮快照回滚
    （``ui/main_window.py::_rollback_from_snapshot``）在回滚前用它把快照裁到
    `IntentJournal.unfinished_registry_targets()` 给出的「未完成项」集合上，
    从而避免把本轮已成功的项也滚回旧路径、让注册表指向不存在的目录。
    """
    wanted = {(str(subkey).lower(), str(name).lower()) for subkey, name in targets}
    kept = [
        entry
        for entry in snapshot.entries
        if (entry.subkey.lower(), entry.name.lower()) in wanted
    ]
    excluded = len(snapshot.entries) - len(kept)
    return (
        RegistrySnapshot(entries=kept, created_at=snapshot.created_at),
        excluded,
    )
