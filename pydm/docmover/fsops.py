"""文件系统操作：磁盘枚举、路径校验、安全复制、校验、删除。

安全原则（针对早期版本的数据丢失隐患）：
    1. 复制 → **源 ↔ 目标全量对照**校验 → 通过后才删除源目录；
    2. 同盘且目标不存在时走原子 rename，最快且无中间态；
    3. 绝不删除未被校验通过的内容；
    4. 「源里有、目标里没有」的东西一律算校验失败 —— 删源前必须集合对齐。

校验强度（2026-10 审计 P0-1/P0-2 修复）：
    * 清单记录的是**源**文件的大小/时间戳/内容摘要，而不是复制完成后目标的大小；
    * 目标文件逐个复算摘要（与复制时对源算出的摘要比对），内容被改动即使大小
      相同也会被发现；
    * 既有目标文件即使「大小 + mtime 都相同」也**先比对内容**才复用（审计 A1），
      故「内容被改动即使大小相同也会被发现」对复用项同样成立；
    * 校验时重新枚举源目录，目录/文件/链接三个集合与清单的差集必须为空，
      否则拒绝删除源目录（空目录、符号链接、权限漏列都在此被拦下）。
"""

from __future__ import annotations

import ctypes
import hashlib
import os
import re
import shutil
import stat
import string
import subprocess
import time
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Sequence, Tuple

from .constants import FORBIDDEN_TARGET_ROOTS

ProgressCallback = Callable[[int, int, str], None]
CancelCallback = Callable[[], bool]


# ------------------------------------------------------------ 长路径（审计 P2-4）
#
# Windows 的 Win32 路径 API 受 260 字符 MAX_PATH 限制，未加扩展前缀的超长路径
# 会被静默截断/漏列——与 P0-2「漏项看不见却照样删源」同源。修复原则：
#   * **只在真正调用文件系统 API 的那一刻**加 ``\\?\`` 前缀；
#   * 逻辑判断与 ``os.path.relpath`` / ``os.path.join`` 的输入输出一律保持普通
#     路径，保证 ``rel_path`` 是干净相对路径、界面展示不带前缀；
#   * ``\\?\`` 路径不做 ``.``/``..`` 归一化，故绝不把它用于展示或比较。

_LONG_PATH_THRESHOLD = 240          # 留出余量：达到该长度即加扩展前缀
_LONG_PATH_PREFIX = "\\\\?\\"


def _lp(path: str) -> str:
    r"""需要时给路径加 ``\\?\`` 扩展前缀以突破 260 字符上限（审计 P2-4）。

    * 已带 ``\\?\`` / ``\\.\\`` 前缀的原样返回（重复加前缀会失效）；
    * UNC 路径 ``\\\\server\\share\\...`` 转成 ``\\\\?\\UNC\\server\\share\\...``；
    * 其余超长路径转绝对路径后加 ``\\\\?\\``；
    * 未达阈值的普通路径**原样返回**，保证既有行为与性能不变。
    """
    if not path:
        return path
    if path.startswith(_LONG_PATH_PREFIX) or path.startswith("\\\\.\\"):
        return path
    if len(path) < _LONG_PATH_THRESHOLD:
        return path
    absolute = os.path.abspath(path)
    if absolute.startswith("\\\\"):
        return "\\\\?\\UNC\\" + absolute[2:]
    return _LONG_PATH_PREFIX + absolute


def _unlp(path: str) -> str:
    r"""去掉 ``\\?\`` 扩展前缀，得到可用于展示 / ``relpath`` 的普通路径。"""
    if path.startswith("\\\\?\\UNC\\"):
        return "\\\\" + path[8:]
    if path.startswith(_LONG_PATH_PREFIX):
        return path[4:]
    return path


# ------------------------------------------------------------------ 磁盘信息

@dataclass(frozen=True)
class DriveInfo:
    letter: str  # 形如 "D:"
    total_bytes: int
    free_bytes: int

    @property
    def root(self) -> str:
        return self.letter + "\\"

    def label(self) -> str:
        return f"{self.letter}\\  可用 {_human(self.free_bytes)} / {_human(self.total_bytes)}"


def _human(size: int) -> str:
    units = ("B", "KB", "MB", "GB", "TB")
    value = float(size)
    for unit in units:
        if value < 1024 or unit == units[-1]:
            return f"{value:.1f} {unit}" if unit != "B" else f"{int(value)} B"
        value /= 1024
    return f"{value:.1f} TB"


def list_fixed_drives() -> List[DriveInfo]:
    """枚举本机固定磁盘（排除 U 盘/光驱/网络盘）。"""
    drives: List[DriveInfo] = []
    try:
        kernel32 = ctypes.windll.kernel32
    except AttributeError:
        return drives

    for letter in string.ascii_uppercase:
        root = f"{letter}:\\"
        if kernel32.GetDriveTypeW(root) != 3:  # DRIVE_FIXED
            continue
        free = ctypes.c_ulonglong(0)
        total = ctypes.c_ulonglong(0)
        avail = ctypes.c_ulonglong(0)
        ok = kernel32.GetDiskFreeSpaceExW(
            root, ctypes.byref(avail), ctypes.byref(total), ctypes.byref(free)
        )
        if not ok:
            continue
        drives.append(
            DriveInfo(
                letter=f"{letter}:",
                total_bytes=total.value,
                free_bytes=avail.value,
            )
        )
    return drives


def system_drive() -> str:
    return (os.environ.get("SystemDrive") or "C:").upper()


# ------------------------------------------------------------------ 路径工具

def expand(path: str) -> str:
    return os.path.expandvars(path) if path else ""


def normalize(path: str) -> str:
    """规范化路径用于比较：绝对化 + 去尾部分隔符 + 统一大小写（仅比较用）。"""
    if not path:
        return ""
    cleaned = os.path.abspath(path)
    drive, tail = os.path.splitdrive(cleaned)
    if tail == os.sep:
        return (drive + os.sep).lower()
    return cleaned.rstrip("\\/").lower()


def same_path(a: str, b: str) -> bool:
    return normalize(a) == normalize(b)


def is_within(child: str, parent: str) -> bool:
    """``child`` 是否位于 ``parent`` 之内（含自身）。"""
    c, p = normalize(child), normalize(parent)
    if not c or not p:
        return False
    if c == p:
        return True
    return c.startswith(p.rstrip("\\") + "\\")


def resolves_to_same(a: str, b: str) -> bool:
    """两个路径是否指向**同一物理位置**（穿透 junction / 符号链接 / subst 盘符别名）。

    审计 A2：``same_path`` 只做字符串比较，会被重解析点绕过——把目标写成指向源
    目录的 junction 时字符串不同、物理位置相同，随后「复制（全部命中复用）→
    校验通过 → 写注册表 → 删源」会把用户真实目录删掉、注册表指向悬空路径。
    优先 ``os.path.samefile``（要求两边都存在），退回 ``realpath`` 比较，
    最后退回 ``same_path``。
    """
    if not a or not b:
        return False
    try:
        return os.path.samefile(_lp(a), _lp(b))
    except (OSError, ValueError):
        pass
    try:
        return normalize(os.path.realpath(a)) == normalize(os.path.realpath(b))
    except (OSError, ValueError):
        return same_path(a, b)


def long_path(path: str) -> str:
    """把路径中**已存在**的部分还原为长名，解除 8.3 短名别名（审计 A3）。

    ``C:\\PROGRA~1`` 与 ``C:\\Program Files`` 是同一目录，但字符串比较不同，
    会让「不许搬到系统目录」的闸门被短名绕过。逐级向上找到最深的已存在祖先，
    用 ``GetLongPathNameW`` 还原后拼回不存在的尾部；任何一步失败即原样返回
    （宁可漏还原，也不能返回一个更错误的路径）。
    """
    if not path:
        return path
    try:
        kernel32 = ctypes.windll.kernel32
    except AttributeError:
        return path

    probe = os.path.abspath(path)
    missing: List[str] = []
    while not os.path.exists(_lp(probe)):
        parent = os.path.dirname(probe)
        if not parent or parent == probe:
            return path
        missing.append(os.path.basename(probe))
        probe = parent

    buffer = ctypes.create_unicode_buffer(32768)
    try:
        length = kernel32.GetLongPathNameW(ctypes.c_wchar_p(probe), buffer, len(buffer))
    except (OSError, AttributeError):
        return path
    if not length or length >= len(buffer):
        return path
    restored = buffer.value
    for name in reversed(missing):
        restored = os.path.join(restored, name)
    return restored


def system32(name: str) -> str:
    """系统目录下可执行文件的**绝对路径**（审计 A9）。

    本程序用 ``--uac-admin`` 打包、以管理员运行，而 ``CreateProcess`` 在
    ``lpApplicationName`` 为空时会把**当前目录**排进搜索路径——若 exe 放在用户
    可写目录、且同目录被投放了同名 exe，就会以管理员身份被执行。故一律给出绝对
    路径。``System32`` 下没有该文件时退回 ``%SystemRoot%``（``explorer.exe`` 就
    装在 ``%SystemRoot%`` 而非 ``System32``）；两处都没有则仍返回 ``System32``
    下的候选路径（保持"绝对路径"这一契约，调用失败由上层报错）。
    """
    root = os.environ.get("SystemRoot") or r"C:\Windows"
    candidate = os.path.join(root, "System32", name)
    if os.path.exists(candidate):
        return candidate
    fallback = os.path.join(root, name)
    return fallback if os.path.exists(fallback) else candidate


def volume_key(path: str) -> str:
    """用于判断两个路径是否同一卷（同盘）。"""
    try:
        probe = path
        while probe and not os.path.exists(_lp(probe)):
            parent = os.path.dirname(probe)
            if parent == probe:
                break
            probe = parent
        return os.path.splitdrive(os.path.abspath(probe))[0].lower()
    except OSError:
        return os.path.splitdrive(os.path.abspath(path))[0].lower()


def free_space(path: str) -> Optional[int]:
    """返回目标所在卷的可用字节数；**取不到时返回 ``None`` 表示「未知」**。

    审计 P1-3：旧实现取不到就返回 ``0``，而调用方用 ``if needed and available``
    做条件与运算，于是「未知」被当成「无需检查」——空间不足或探测失败时反而
    不阻断，一路复制到磁盘写满。这里用 ``None`` 表达不确定性，由调用方阻断。
    """
    probe = path
    while probe and not os.path.exists(_lp(probe)):
        parent = os.path.dirname(probe)
        if parent == probe:
            break
        probe = parent
    try:
        return shutil.disk_usage(_lp(probe)).free
    except OSError:
        return None


def tail_name(path: str) -> str:
    """取路径最后一段（不收尾的 ``\\`` / ``/`` 会被先去掉）。"""
    cleaned = (path or "").rstrip("\\/")
    if not cleaned:
        return ""
    return os.path.basename(cleaned)


INVALID_NAME_CHARS = set('<>"|?*')


def _first_illegal_char(candidate: str) -> str:
    """返回路径中首个非法字符。

    ``:`` 只在盘符位置（``D:``）合法，出现在其余位置即非法；
    其余 ``< > " | ? *`` 一律非法。
    """
    body = candidate[2:] if len(candidate) >= 2 and candidate[1] == ":" else candidate
    for ch in body:
        if ch in INVALID_NAME_CHARS or ch == ":":
            return ch
    return ""


# 审计 A8：按 Microsoft「Naming Files, Paths, and Namespaces」，``COM¹``/``COM²``/
# ``COM³``（上标数字）与 ``COM1``/``COM2``/``COM3`` 等价，同属设备保留名。只列
# ASCII 1-9 会把这批变体放行，交给后续创建步骤去撞墙；这里按保守取向一并拒绝
# （多拒一个"看似合法其实用不了"的名字，代价远小于放行）。
_SUPERSCRIPT_DIGITS = "\u00b9\u00b2\u00b3\u2074\u2075\u2076\u2077\u2078\u2079"

# Windows 设备保留名（大小写不敏感，含带扩展名形式，如 ``CON.txt``）
_RESERVED_NAMES = frozenset(
    ["CON", "PRN", "AUX", "NUL"]
    + [f"COM{i}" for i in range(1, 10)]
    + [f"LPT{i}" for i in range(1, 10)]
    + [f"COM{d}" for d in _SUPERSCRIPT_DIGITS]
    + [f"LPT{d}" for d in _SUPERSCRIPT_DIGITS]
)


def _invalid_segment(path: str) -> str:
    """返回路径中**首个**使用 Windows 保留名或以点/空格结尾的片段（审计 P2-14）。

    这些名字（``CON``/``NUL``/``COM1``…，含 ``CON.txt`` 形式）与「以点或空格
    结尾」的片段在校验时看似合法，真正创建时会被系统拒绝，导致目标目录建不出来
    却已通过前置校验。正常的 ``.`` / ``..`` 语义（相对路径段）必须放行，不得误伤。
    """
    drive, tail = os.path.splitdrive(path)
    for segment in re.split(r"[\\/]+", tail):
        if not segment or segment in (".", ".."):
            continue            # 保留 ``.`` / ``..`` 的正常语义
        stem = segment.split(".")[0].upper()
        if stem in _RESERVED_NAMES:
            return segment
        if segment[-1] in (" ", "."):
            return segment
    return ""


def reparse_ancestor(path: str) -> str:
    r"""返回 ``path`` 自身、或其**任一级已存在的上级目录**中的第一个重解析点。

    没有命中时返回空串。``is_reparse_point`` 对不存在的路径返回 ``False``，故
    「目标那一级还不存在」（工具自己新建的正常场景）不会被误伤。

    审计 F1 把这条检查写进了 :func:`validate_destination`；**最终审计 P1-1** 发现
    它只在**规划期**被调用一次，而规划与执行之间存在可达分钟级的时间窗
    （``build_plan(scan=True)`` 要为每个文件夹全量枚举源目录，48.7 万文件规模下
    是分钟级）。攻击者只需在这段时间里用 ``mklink /J``（**不需要管理员**）把目标
    的某一级上级换成联接点，就能让以管理员身份运行的复制/重命名把数据写进链接
    指向的位置、随后连同源目录一起删掉——``copy_merge`` 原先只检查目标**自身**
    与其**子目录**，``try_atomic_move`` 只检查「目标是否存在」，都不看祖先链。

    故把这段逻辑抽成独立函数，**执行期再复查一次**（见 ``copy_merge``、
    ``try_atomic_move`` 与 ``transfer._execute_item``），与规划期的闸门同源。
    """
    probe = path
    while probe:
        if is_reparse_point(probe):
            return probe
        parent = os.path.dirname(probe)
        if not parent or parent == probe:
            break
        probe = parent
    return ""


def validate_destination(path: str, sources: Sequence[str]) -> Tuple[bool, str]:
    """校验目标路径是否可用于承接转移。

    拒绝：空路径、非法字符、Windows 保留名/尾随点空格、相对路径、被禁止的系统
    根目录，以及「目标位于源之内」或「源位于目标之内」这类会造成递归搬迁的组合。
    """
    if not path or not path.strip():
        return False, "目标路径不能为空。"

    candidate = path.strip().strip('"')

    bad = _first_illegal_char(candidate)
    if bad:
        return False, f"目标路径含非法字符（{bad}）。"

    expanded = expand(candidate)
    if not os.path.isabs(expanded):
        return False, "目标路径需为绝对路径，例如 D:\\UserData。"

    bad_segment = _invalid_segment(expanded)
    if bad_segment:
        return False, (
            f"路径片段「{bad_segment}」不可用："
            "Windows 保留名，或以点/空格结尾。"
        )

    drive, _ = os.path.splitdrive(expanded)
    if not drive or len(drive) < 2:
        return False, "目标路径缺少盘符。"

    if not os.path.exists(drive + "\\"):
        return False, f"目标磁盘 {drive} 不存在或未就绪。"

    # 审计 A3：目标与禁止根都做一次长名还原后再比。``C:\PROGRA~1`` 与
    # ``C:\Program Files`` 是同一目录但字符串不同，纯字符串比较会让「不许搬进
    # 系统目录」被 8.3 短名绕过。
    norm_candidates = {normalize(expanded), normalize(long_path(expanded))}
    for forbidden in FORBIDDEN_TARGET_ROOTS:
        forbidden_candidates = {normalize(forbidden), normalize(long_path(forbidden))}
        for need in norm_candidates:
            for bad in forbidden_candidates:
                if not need or not bad:
                    continue
                if need == bad or need.startswith(bad.rstrip("\\") + "\\"):
                    return False, f"不能把用户文件夹转移到系统目录 {forbidden}。"

    for source in sources:
        if not source:
            continue
        if same_path(expanded, source):
            continue  # 同路径由调用方按「无需转移」处理
        if resolves_to_same(expanded, source):
            # 审计 A2：字符串不同但物理位置相同（junction / 符号链接 / subst
            # 盘符别名）。放行会走「复制（全部命中复用）→ 校验通过 → 删源」，
            # 把用户真实目录删掉、注册表指向悬空路径。
            return False, (
                "目标与待转移目录实际指向同一位置（链接或盘符别名）："
                f"{expanded}（{source}）"
            )
        if is_within(expanded, source):
            return False, f"目标位于待转移目录内部：{expanded}（在 {source} 之内）"
        if is_within(source, expanded):
            return False, f"待转移目录位于目标内部：{source}（在 {expanded} 之内）"

    # 审计 F1：目标**自身**与其**每一级已存在的上级目录**都不得是重解析点。
    #
    # 上面三层闸门只比较「目标根 vs 源」，从不检查目标这一族路径自己是不是
    # junction / 符号链接。而 ``os.makedirs(exist_ok=True)`` 对已存在的 junction
    # 静默成功，随后的建目录与写文件**全部跟随** junction 落到链接指向的位置；
    # 校验侧 ``os.path.isdir`` / ``lexists`` 同样跟随，于是恒为真 —— 攻击者用
    # ``mklink /J``（**不需要管理员**）把目标指向任意目录，本工具就会以管理员
    # 权限写数据、写注册表并删源，界面还报"转移成功"。
    # 最终审计 P1-1：判定逻辑已抽成 :func:`reparse_ancestor`，**执行期还会再查
    # 一遍**——本函数只是规划期的那一次（规划与执行之间有时间窗）。
    blocked = reparse_ancestor(expanded)
    if blocked:
        return False, (
            f"目标路径或其上级目录是链接/联接点（{blocked}）："
            "数据会被写到链接指向的位置，请改用真实目录"
        )

    return True, ""


# ------------------------------------------------------------------ 内容清点

FILE_ATTRIBUTE_REPARSE_POINT = 0x0400
FILE_ATTRIBUTE_SPARSE_FILE = 0x0200     # 稀疏文件（审计 P2-5）

# FSCTL_SET_SPARSE：把目标文件标记为稀疏，使跳过的零区真正成为磁盘洞（P2-5）
_FSCTL_SET_SPARSE = 0x900C4


def _mark_sparse(handle) -> bool:
    """把已打开的写句柄标记为稀疏文件（Windows ``FSCTL_SET_SPARSE``，P2-5）。

    审计 P2-5：稀疏文件（虚拟机磁盘、PST 等）若按普通文件复制，全零块会被实体
    写入，目标空间暴涨。标记成功后，跳过的零块才真正落成 hole。标记失败即返回
    ``False``，由调用方回退为「完整写入」——**宁可变大，绝不让数据出错**。
    """
    try:
        import msvcrt
    except ImportError:
        return False
    try:
        os_handle = msvcrt.get_osfhandle(handle.fileno())
    except (OSError, ValueError, AttributeError):
        return False
    try:
        kernel32 = ctypes.windll.kernel32
    except AttributeError:
        return False
    from ctypes import wintypes

    returned = wintypes.DWORD(0)
    try:
        ok = kernel32.DeviceIoControl(
            wintypes.HANDLE(os_handle),
            _FSCTL_SET_SPARSE,
            None, 0, None, 0,
            ctypes.byref(returned),
            None,
        )
    except (OSError, AttributeError):
        return False
    return bool(ok)


def is_reparse_point(path: str) -> bool:
    """是否为重解析点（符号链接 / 目录联接 / OneDrive 占位等）。

    这类条目**不能按普通目录递归**：跟随会越界到别的卷、甚至造成循环。
    Windows 上 ``os.path.islink`` 对目录联接返回 False，故直接看文件属性。
    """
    try:
        info = os.lstat(_lp(path))
    except OSError:
        return False
    if os.path.islink(_lp(path)):
        return True
    return bool(getattr(info, "st_file_attributes", 0) & FILE_ATTRIBUTE_REPARSE_POINT)


@dataclass
class TreeStat:
    files: int = 0
    dirs: int = 0
    bytes: int = 0
    links: int = 0
    unreadable: int = 0

    def describe(self) -> str:
        return f"{self.files} 个文件 / {_human(self.bytes)}"


def _walk_error_sink(problems: List[str]):
    """``os.walk`` 的 ``onerror``：把「静默漏列整棵子树」变成显式记录。"""

    def handler(exc: OSError) -> None:
        problems.append(f"无法读取目录 {getattr(exc, 'filename', '?')}：{exc}")

    return handler


def scan_tree(root: str, limit_files: int = 0) -> TreeStat:
    """统计目录规模。跳过符号链接/联接点，避免越界与死循环。

    审计 P1-3：无法读取的目录/文件不再静默忽略，而是计入 ``unreadable``
    （``os.walk`` 传 ``onerror``），由调用方决定是否阻断——低估规模会导致
    「空间充足」的结论失真。
    """
    stat = TreeStat()
    if not os.path.isdir(_lp(root)):
        return stat

    problems: List[str] = []
    for current, dirnames, filenames in os.walk(
        _lp(root), followlinks=False, onerror=_walk_error_sink(problems)
    ):
        # current 可能带回扩展前缀（长路径）；逻辑与 relpath 一律用去前缀的路径
        logical = _unlp(current)
        keep: List[str] = []
        for name in dirnames:
            if is_reparse_point(os.path.join(logical, name)):
                stat.links += 1
                continue          # 不递归、不计入 dirs：它是链接而不是内容
            keep.append(name)
        dirnames[:] = keep
        stat.dirs += len(keep)

        for name in filenames:
            full = os.path.join(logical, name)
            if os.path.islink(_lp(full)):
                stat.links += 1
                continue
            try:
                stat.bytes += os.path.getsize(_lp(full))
            except OSError:
                stat.unreadable += 1
            stat.files += 1
            if limit_files and stat.files >= limit_files:
                stat.unreadable += len(problems)
                return stat

    stat.unreadable += len(problems)
    return stat


# ------------------------------------------------------------------ 安全复制

DIGEST_CHUNK = 1 << 20          # 1 MiB：分块读写，绝不整文件读入内存
_PROGRESS_INTERVAL = 0.1        # 进度回调最小间隔（秒），见审计 P2-11


class CopyCancelled(Exception):
    """复制过程中用户请求取消（在分块循环内响应，不必等整个文件复制完）。"""


@dataclass(slots=True)
class FileFingerprint:
    """**源**文件的指纹。

    审计 P0-1：旧实现只在复制完成后记录目标自己的大小，于是校验退化成
    「目标 vs 目标」的自比对——无论复制是否真的成功，校验恒为通过。
    这里记录源的 (大小, 修改时间, 内容摘要)，校验时对目标复算摘要，
    并回查源是否在复制期间被改动。

    审计 P2-6：本对象**每个文件一个**，48.7 万文件场景下 ``__slots__`` 能显著
    压低单条记录开销（省掉每实例的 ``__dict__``），而不牺牲「两次枚举做差集」
    的安全保证。

    审计 P2(c)：``dev/ino/nlink/attrs`` 是 ``enumerate_source`` 那次 ``os.stat``
    顺手带出来的属性，供 ``copy_merge`` 判定硬链接与稀疏文件，省掉每文件一次
    额外的 ``os.stat``（实测 4 次全量遍历 ≈ 9.1 次 stat/文件）。全部缺省为 0，
    既保证 ``FileFingerprint(size, mtime_ns, digest)`` 这类三段构造继续可用，
    也让消费方按「未知 = 不做硬链接/不做稀疏」处理（fail-safe 取向：最多退化为
    多占空间或多花一次复制，绝不会算错内容）。
    """

    size: int
    mtime_ns: int
    digest: str = ""
    dev: int = 0
    ino: int = 0
    nlink: int = 0
    attrs: int = 0


@dataclass
class SourceTree:
    """源的完整清点结果（键为相对路径）。"""

    dirs: List[str] = field(default_factory=list)
    files: Dict[str, FileFingerprint] = field(default_factory=dict)
    links: List[str] = field(default_factory=list)
    skipped: List[str] = field(default_factory=list)
    errors: List[str] = field(default_factory=list)


@dataclass(slots=True)
class CopyItem:
    """一个文件从源到目标的落点；同名冲突时 ``target_rel`` 与 ``rel`` 不同。"""

    rel: str
    target_rel: str
    fingerprint: FileFingerprint
    reused: bool = False       # 目标已存在且内容一致，未重新写入


@dataclass
class CopyResult:
    items: List[CopyItem] = field(default_factory=list)
    dirs: List[str] = field(default_factory=list)
    links: List[str] = field(default_factory=list)
    conflicts: List[str] = field(default_factory=list)    # 同名冲突，已另存为 (1)
    skipped: List[str] = field(default_factory=list)      # 按 skip_names 规则跳过
    skip_names: Tuple[str, ...] = ()
    errors: List[str] = field(default_factory=list)
    cancelled: bool = False
    moved_by_rename: bool = False
    sparse_files: int = 0          # 以稀疏方式复制的文件数（审计 P2-5）
    hardlinked_files: int = 0      # 以硬链接重建而非复制的文件数（审计 P2-5）

    @property
    def copied_files(self) -> int:
        return len(self.items)

    @property
    def copied_bytes(self) -> int:
        return sum(item.fingerprint.size for item in self.items)

    @property
    def ok(self) -> bool:
        return not self.errors and not self.cancelled


def enumerate_source(root: str, skip_names: Sequence[str] = ()) -> SourceTree:
    """完整清点源目录：目录（含空目录）、文件指纹、重解析点、读取失败项。

    这是「不丢东西」的地基：``os.walk`` 不传 ``onerror`` 时会静默跳过整棵
    无法读取的子树，而漏掉的东西在校验里是看不见的（审计 P0-2）。

    最终审计 P3-5：入口先把调用方可能带进来的 ``\\\\?\\`` 长路径前缀去掉（:func:`_unlp`），
    使 ``root`` 与遍历中 ``_unlp(current)`` 得到的逻辑路径同源——否则
    ``os.path.relpath(full, root)`` 会在「一侧带前缀、一侧不带」时抛
    ``ValueError: path is on mount 'D:', start on mount '\\\\?\\D:'``。加前缀由函数内的
    ``_lp()`` 在真正调 Win32 前统一补齐。
    """
    root = _unlp(root)
    tree = SourceTree()
    if not os.path.isdir(_lp(root)):
        tree.errors.append(f"源目录不存在：{root}")
        return tree

    skip_lookup = {name.lower() for name in skip_names}
    problems: List[str] = []

    for current, dirnames, filenames in os.walk(
        _lp(root), topdown=True, followlinks=False, onerror=_walk_error_sink(problems)
    ):
        # current 可能带回扩展前缀；rel_path 必须干净，故逻辑用去前缀的路径
        logical = _unlp(current)
        keep: List[str] = []
        for name in dirnames:
            full = os.path.join(logical, name)
            rel = os.path.relpath(full, root)
            if name.lower() in skip_lookup:
                tree.skipped.append(rel)
                continue
            if is_reparse_point(full):
                tree.links.append(rel)     # 链接不等于内容：单独记录、单独重建
                continue
            tree.dirs.append(rel)
            keep.append(name)
        dirnames[:] = keep

        for name in filenames:
            full = os.path.join(logical, name)
            rel = os.path.relpath(full, root)
            if name.lower() in skip_lookup:
                tree.skipped.append(rel)
                continue
            if is_reparse_point(full):
                tree.links.append(rel)
                continue
            try:
                info = os.stat(_lp(full))
            except OSError as exc:
                problems.append(f"无法读取文件信息 {rel}：{exc}")
                continue
            tree.files[rel] = FileFingerprint(
                info.st_size,
                info.st_mtime_ns,
                dev=info.st_dev,
                ino=info.st_ino,
                nlink=info.st_nlink,
                attrs=getattr(info, "st_file_attributes", 0),
            )

    tree.errors.extend(problems)
    return tree


def _digest_file(path: str, is_cancelled: Optional[CancelCallback] = None) -> str:
    """分块计算文件内容摘要（blake2b-128，快且足够抗碰撞）。"""
    digest = hashlib.blake2b(digest_size=16)
    with open(_lp(path), "rb") as handle:
        while True:
            if is_cancelled and is_cancelled():
                raise CopyCancelled
            block = handle.read(DIGEST_CHUNK)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def _partial_path(dst_path: str) -> str:
    """为目标文件挑一个**尚不存在**的临时文件名（与目标同一目录，便于原子改名）。

    ``O_CREAT|O_EXCL`` 的排他创建保证既不覆盖用户的同名文件、也不与其它<｜hy_place▁holder▁no▁813｜>
    争夺同一个临时名；``\\?\\`` 前缀由 :func:`_lp` 在外部加好。
    """
    for index in range(100):
        candidate = _lp(dst_path + (".part" if index == 0 else f".part{index}"))
        try:
            os.close(
                os.open(
                    candidate,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_BINARY", 0),
                )
            )
        except FileExistsError:
            continue
        return candidate
    raise FileExistsError(f"临时文件过多，无法为 {dst_path} 找到可用的写入名")


def _copy_with_digest(
    src_path: str,
    dst_path: str,
    is_cancelled: Optional[CancelCallback] = None,
    sparse: bool = False,
) -> Tuple[str, int]:
    """边复制边算**源**内容摘要：源只读一遍，返回 ``(摘要, 写出字节数)``。

    审计 R2：**先写临时文件、成功后再 ``os.replace`` 原子改名**。旧实现直接
    ``open(dst, "wb")``，取消或崩溃会在目标盘留下半截文件；用户重试时那份半成品
    与目标既有文件同名冲突，被改成 ``name (1).ext``，垃圾再也不会被清理。改为原子
    改名后：目标要么**不存在**、要么是**完整内容**，取消/异常时临时文件当场删除，
    重试是续传而不是产生 ``name (1).ext``。

    审计 P2-5：``sparse=True``（源是稀疏文件）时以 ``DIGEST_CHUNK`` 为块判断
    是否全零，全零块**跳过写入**（``seek`` 前进），最后 ``truncate`` 到完整长度，
    配合 ``FSCTL_SET_SPARSE`` 让这些零区真正成为磁盘洞。摘要仍对**源的全部字节**
    （含零）计算，因此校验读回目标（洞读作零）时摘要一致。若打稀疏标记失败则
    自动回退为完整写入——绝不让「省空间」以牺牲数据正确性为代价。
    """
    digest = hashlib.blake2b(digest_size=16)
    written = 0
    tmp_path = _partial_path(dst_path)
    try:
        with open(_lp(src_path), "rb") as reader, open(tmp_path, "wb") as writer:
            sparse = sparse and _mark_sparse(writer)   # 标记失败 → 回退为完整写入
            while True:
                if is_cancelled and is_cancelled():
                    raise CopyCancelled
                block = reader.read(DIGEST_CHUNK)
                if not block:
                    break
                written += len(block)
                if sparse and not block.strip(b"\x00"):
                    # 全零块：只前进不写入，保持洞（strip 在 C 层完成，开销可忽略）
                    writer.seek(len(block), os.SEEK_CUR)
                else:
                    writer.write(block)
                digest.update(block)
            if sparse:
                writer.truncate(written)   # 尾部跳过的零块也要计入文件长度
        # 走到这里内容已完整落盘（句柄也已关闭），改名是原子的：
        # 目标文件只会以"完整"的形态出现在用户面前
        os.replace(tmp_path, _lp(dst_path))
    except BaseException:
        # 含 CopyCancelled / OSError / 崩溃：绝不把半成品留在目标盘
        try:
            os.unlink(tmp_path)
        except OSError:
            pass
        raise
    return digest.hexdigest(), written


def _matches_signature(path: str, fingerprint: FileFingerprint) -> Tuple[bool, bool]:
    """返回目标的 ``(大小相同, 时间戳相同)``；取不到时为 ``(False, False)``。"""
    try:
        info = os.stat(_lp(path))
    except OSError:
        return False, False
    return info.st_size == fingerprint.size, info.st_mtime_ns == fingerprint.mtime_ns


def _unique_rel(dst: str, rel: str) -> str:
    """为同名冲突挑一个不存在的并列名字：``name.ext`` → ``name (1).ext``。"""
    folder, name = os.path.split(rel)
    stem, ext = os.path.splitext(name)
    for index in range(1, 10000):
        candidate = os.path.join(folder, f"{stem} ({index}){ext}")
        if not os.path.exists(_lp(os.path.join(dst, candidate))):
            return candidate
    raise FileExistsError(f"同名文件过多，无法为 {rel} 找到可用的并列名字")


def _recreate_link(src_path: str, dst_path: str) -> Tuple[bool, str]:
    """按原样重建符号链接 / 目录联接（**不**递归复制它指向的内容）。

    先试符号链接；目录联接退回 ``mklink /J``（无需管理员权限）。重建失败
    即返回失败，由调用方拒绝删除源目录——绝不「静默丢弃链接后删源」。
    """
    if is_reparse_point(dst_path):
        return True, ""            # 上次运行已经建过

    try:
        link_target = os.readlink(_lp(src_path))
    except OSError as exc:
        return False, f"无法读取链接 {src_path}：{exc}"
    if link_target.startswith("\\\\?\\"):
        link_target = link_target[4:]   # 联接点读出来带扩展前缀，mklink 不认

    is_dir = os.path.isdir(_lp(src_path))
    try:
        os.symlink(link_target, _lp(dst_path), target_is_directory=is_dir)
        return True, ""
    except (OSError, NotImplementedError) as problem:
        pass

    if is_dir:
        try:
            completed = subprocess.run(
                # 审计 A9：用绝对路径调用 cmd（本进程以管理员运行），不给
                # 「当前目录里的同名 cmd.exe」任何可乘之机；mklink 是 cmd 内建。
                [system32("cmd.exe"), "/c", "mklink", "/J", _lp(dst_path), link_target],
                capture_output=True, text=True, timeout=30,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            if completed.returncode == 0:
                return True, ""
            problem = RuntimeError(
                (completed.stderr or completed.stdout or "").strip()
                or f"mklink 返回 {completed.returncode}"
            )
        except (OSError, subprocess.SubprocessError) as exc:
            problem = exc

    return False, f"无法重建链接 {dst_path}（指向 {link_target}）：{problem}"


def copy_merge(
    src: str,
    dst: str,
    on_progress: Optional[ProgressCallback] = None,
    is_cancelled: Optional[CancelCallback] = None,
    skip_names: Sequence[str] = (),
) -> CopyResult:
    """把 ``src`` 的内容合并复制到 ``dst``，返回可供 :func:`verify_copy` 复核的清单。

    审计 P0-1 / P0-2 / P1-2 的修复要点：

    * 复制前**完整枚举源**（含空目录与重解析点），枚举有错即整体失败；
    * 目标**连同空目录一起重建**；链接按原样重建，重建失败即整体失败；
    * 每个文件**边复制边算源内容摘要**，摘要属于源，不再只记目标大小；
    * 同名冲突**绝不静默覆盖**：目标已有一致内容则复用（不重写），不一致
      则另存为 ``name (1).ext``——两份都保留；
    * 进度按时间节流（≥100ms 或最后一个），避免逐文件入队造成队列洪水；
    * **稀疏文件**按洞复制、**硬链接**在目标侧重建成链接（审计 P2-5），两者都
      能通过 :func:`verify_copy` 的「源 ↔ 目标」复核；
    * ``skip_names``（审计 P3-1）是受支持的「按名跳过」能力（如桌面 ``desktop.ini``），
      当前由测试与预留的调用方使用，故保留而非删除。

    不做任何删除；删除由调用方在 :func:`verify_copy` 通过后决定。
    """
    result = CopyResult(skip_names=tuple(skip_names))
    # 审计 A2 自保护：即使调用方漏了 validate_destination，也绝不允许"自我复制"。
    # 源与目标若指向同一物理位置（链接 / 盘符别名 / 同一路径），每个文件都会
    # 命中"已存在且内容一致"而被判复用，校验一路通过，调用方随后删源 —— 用户
    # 的真实目录就此消失。这里在**任何写操作之前**拒绝。
    if resolves_to_same(src, dst):
        result.errors.append("源与目标指向同一位置，已拒绝执行（未做任何改动）")
        return result
    if not os.path.isdir(_lp(src)):
        result.errors.append(f"源目录不存在：{src}")
        return result

    # 审计 F1 + 最终审计 P1-1：目标**自身**或其**任一级已存在的上级目录**是重解析点
    # → 在任何写操作之前拒绝。``os.makedirs(exist_ok=True)`` 对已存在的 junction 静默
    # 成功，随后创建的子目录与写入的文件都会**跟随**它落到链接指向的位置。目标那一级
    # 还不存在是正常的（工具自己新建），``is_reparse_point`` 对不存在的路径返回 False。
    #
    # 原先这里只查 ``dst`` 自身，于是「目标还不存在、但它的父亲是 junction」这一路
    # （攻击者最省事的一条）会一路写下去；规划期的 ``validate_destination`` 虽然查了
    # 祖先链，但规划与执行之间存在时间窗（见 :func:`reparse_ancestor`）。
    blocked = reparse_ancestor(dst)
    if blocked:
        result.errors.append(
            f"目标目录或其上级是链接/联接点（{blocked}）：数据会被写到链接指向的"
            "位置，已拒绝执行（未做任何改动）"
        )
        return result

    tree = enumerate_source(src, skip_names)
    result.skipped = list(tree.skipped)
    if tree.errors:
        # 枚举不完整 → 连复制都不做：漏掉的项在校验里是看不见的
        result.errors.extend(tree.errors[:5])
        if len(tree.errors) > 5:
            # 审计 B-3：截断必须说清总数，否则用户分不清「全貌」与「冰山一角」
            result.errors.append(f"（共 {len(tree.errors)} 项，以上仅列前 5 条）")
        return result

    try:
        os.makedirs(_lp(dst), exist_ok=True)
    except OSError as exc:
        result.errors.append(f"无法创建目标根目录 {dst}：{exc}")
        return result

    # ① 目录先建齐（含空目录）——旧实现只建「文件的父目录」，空目录永远丢失
    for rel in tree.dirs:
        target_dir = os.path.join(dst, rel)
        # 审计 F1：目标子目录**已存在**且是重解析点 → 整体失败。此处若靠
        # ``exist_ok=True`` 静默放行，随后落在它下面的每个文件都会写进链接
        # 指向的目录（攻击面与 R3 同源，且不需要管理员权限即可布好）。
        if is_reparse_point(target_dir):
            result.errors.append(
                f"目标路径已存在且是链接/联接点，拒绝写入：{target_dir}"
            )
            return result
        try:
            os.makedirs(_lp(target_dir), exist_ok=True)
        except OSError as exc:
            result.errors.append(f"无法创建目录 {target_dir}：{exc}")
    if result.errors:
        return result
    result.dirs = list(tree.dirs)

    # ② 重解析点按原样重建
    for rel in tree.links:
        ok, message = _recreate_link(os.path.join(src, rel), os.path.join(dst, rel))
        if ok:
            result.links.append(rel)
        else:
            result.errors.append(message)
    if result.errors:
        return result

    # ③ 文件：硬链接判定 → 冲突判定 → 复制 → 摘要
    total = len(tree.files)
    done = 0
    last_report = 0.0
    # 审计 P2-5：以 (st_dev, st_ino) 为键记住已落地的硬链接，同一 inode 的第二份
    # 起改用 os.link 重建，而不是各复制一份（源里一份数据 = 目标里一份数据）。
    link_seen: Dict[Tuple[int, int], Tuple[str, str]] = {}

    for rel, fingerprint in tree.files.items():
        if is_cancelled and is_cancelled():
            result.cancelled = True
            return result

        source_path = os.path.join(src, rel)
        target_rel = rel
        target = os.path.join(dst, rel)
        reused = False
        copied_digest = fingerprint.digest   # 复用项的摘要可能为空

        # 审计 P2(c)：dev/ino/nlink/attrs 已在 ``enumerate_source`` 那次 stat 里
        # 取到并随指纹带出，此处直接复用，每文件省掉一次 ``os.stat``。文件若在
        # 枚举之后被删掉了，下面 ``_copy_with_digest`` 打开时会以 OSError 失败并
        # 记入 errors —— fail-closed 的语义与旧的「先 stat 再复制」完全一致。
        # 审计 A7：部分 SMB / exFAT 卷取不到文件索引，``ino`` 恒为 0。若只用
        # ``(dev, ino)`` 做键，两个无关文件会共用键而被错误地建成硬链接（内容被
        # 替换）；故 ``ino`` 为 0 时一律不参与硬链接复用。
        inode_key = (
            (fingerprint.dev, fingerprint.ino)
            if (fingerprint.nlink > 1 and fingerprint.ino)
            else None
        )
        sparse = bool(fingerprint.attrs & FILE_ATTRIBUTE_SPARSE_FILE)

        # ③a 同一 inode 已落地过 → 在目标侧重建成硬链接
        placed = link_seen.get(inode_key) if inode_key is not None else None
        if placed is not None:
            link_rel, link_digest = placed
            try:
                if not os.path.exists(_lp(target)):
                    folder = os.path.dirname(target)
                    if folder:
                        os.makedirs(_lp(folder), exist_ok=True)
                    os.link(_lp(os.path.join(dst, link_rel)), _lp(target))
                    result.items.append(
                        CopyItem(
                            rel,
                            target_rel,
                            FileFingerprint(fingerprint.size, fingerprint.mtime_ns, link_digest),
                            True,
                        )
                    )
                    result.hardlinked_files += 1
                    done += 1
                    now = time.monotonic()
                    if on_progress and (
                        now - last_report >= _PROGRESS_INTERVAL or done == total
                    ):
                        last_report = now
                        on_progress(done, max(total, 1), rel)
                    continue
            except OSError:
                # 跨卷/权限等导致建链接失败 → 回退为下方常规复制，绝不丢内容
                pass

        # 审计 F1：目标文件位置**已存在**且是重解析点（符号链接 / 联接点）→
        # 整体失败。写入会跟随链接落到别处，``os.replace`` 又会替换掉链接本身，
        # 而校验侧 ``isfile`` 同样跟随 —— 任何一条路径都会在没有任何提示的
        # 情况下把管理员权限的写操作导向攻击者指定的位置。
        if is_reparse_point(target):
            result.errors.append(
                f"目标路径已存在且是链接/联接点，拒绝写入：{target_rel}"
            )
            return result

        try:
            # 审计 P2(b)：``isdir`` 与 ``isfile`` 是**两次独立**的 stat；合成一次
            # ``os.stat`` + ``S_ISDIR/S_ISREG`` 判定。语义逐条保持原样：
            # 目录 → 并列改名；普通文件 → 比大小再比内容；其它/不存在 → 不冲突。
            try:
                target_info = os.stat(_lp(target))
            except (FileNotFoundError, NotADirectoryError):
                target_info = None            # 目标不存在：直接写入
            if target_info is not None and stat.S_ISDIR(target_info.st_mode):
                # 目标位置被同名目录占着：不能覆盖，改存并列名字
                target_rel = _unique_rel(dst, rel)
                target = os.path.join(dst, target_rel)
                result.conflicts.append(rel)
            elif target_info is not None and stat.S_ISREG(target_info.st_mode):
                if target_info.st_size == fingerprint.size:
                    # 审计 A1：既有文件即使「大小 + mtime 都对上」也**必须**比对
                    # 内容才能复用。本工具随后会删除源目录，仅凭大小/时间戳就
                    # 判定"已复制"，会把「时间戳巧合相同、内容不同」的既有文件
                    # 当成副本（目标目录是合并语义，常已有用户资料，且 robocopy
                    # /COPY:DT、xcopy /K 等都会保留原始 mtime），删源后源内容
                    # 永久丢失。相同则复用并记下摘要，不同则另存并列名字保留两份。
                    target_digest = _digest_file(target)
                    if target_digest == _digest_file(source_path, is_cancelled):
                        reused = True
                        # 记下摘要：否则复用项会因 fingerprint.digest 为空而在
                        # 校验时退化成"只比大小+时间戳"。
                        fingerprint = FileFingerprint(
                            fingerprint.size, fingerprint.mtime_ns, target_digest
                        )
                        copied_digest = target_digest
                    else:
                        target_rel = _unique_rel(dst, rel)
                        target = os.path.join(dst, target_rel)
                        result.conflicts.append(rel)
                else:
                    target_rel = _unique_rel(dst, rel)
                    target = os.path.join(dst, target_rel)
                    result.conflicts.append(rel)
        except CopyCancelled:
            result.cancelled = True
            return result
        except OSError as exc:
            result.errors.append(f"同名冲突处理失败 {rel}：{exc}")
            continue

        if not reused:
            try:
                # 审计 P2(a)：旧实现在这里对每个文件都做一次 ``makedirs``，而所有
                # 目录已在本函数开头按 ``tree.dirs`` 一次性建齐（每个文件的父目录
                # 必然是源里被枚举到的普通目录 → 必然在 ``tree.dirs`` 里），
                # 故这次 per-file 调用纯属重复系统调用，删除。
                digest, written = _copy_with_digest(
                    source_path, target, is_cancelled, sparse=sparse
                )
                if written != fingerprint.size:
                    result.errors.append(
                        f"复制字节数不符 {rel}：源 {fingerprint.size}，写出 {written}"
                    )
                    continue
                if sparse:
                    result.sparse_files += 1
                copied_digest = digest
                fingerprint = FileFingerprint(
                    fingerprint.size, fingerprint.mtime_ns, digest
                )
                shutil.copystat(_lp(source_path), _lp(target))
            except CopyCancelled:
                result.cancelled = True
                return result
            except OSError as exc:
                result.errors.append(f"复制失败 {rel}：{exc}")
                continue

        # 登记本 inode 的首个落地点，供后续硬链接复用其摘要
        if inode_key is not None and inode_key not in link_seen:
            link_seen[inode_key] = (target_rel, copied_digest)

        result.items.append(CopyItem(rel, target_rel, fingerprint, reused))
        done += 1
        now = time.monotonic()
        if on_progress and (now - last_report >= _PROGRESS_INTERVAL or done == total):
            last_report = now
            on_progress(done, max(total, 1), rel)

    return result


def verify_copy(
    src: str,
    dst: str,
    result: CopyResult,
    on_progress: Optional[ProgressCallback] = None,
    is_cancelled: Optional[CancelCallback] = None,
) -> Tuple[bool, str]:
    """删源前的最后一道闸门：逐项做「源 ↔ 目标」对照 + 集合差集校验。

    两个新形参都是**可选且纯增量**的（审计 P1）：

    * ``on_progress`` / ``is_cancelled``：与 ``copy_merge`` 完全相同的 100ms
      节流与取消响应。旧实现两者皆无，19,953 文件校验实测 52.5s 期间进度条停在
      100%、状态栏冻结、"停止"按钮点了毫无反应（取消标记根本传不进来）。
      取消时按既有约定抛 :class:`CopyCancelled`（调用方据此返回 ST_CANCELLED，
      **绝不删源**）。

    任何一项不过就返回 ``False``，调用方必须保留源目录：

    1. 目标文件存在；
    2. 目标大小等于**源**记录的大小；
    3. 目标内容摘要等于**源**的摘要（复制时对源算出；复用项也会带上摘要，
       见 ``copy_merge`` 的审计 A1 修复，不再退回"大小+时间戳"）；
    4. 源文件自枚举以来未被改动（防复制期间被并发修改后静默丢弃新版本）；
    5. 重新枚举源，``源集合 − 清单集合`` 必须为空（空目录、清单外文件、
       权限漏列、链接都在此被拦下）；
    6. 清单里的目录与链接在目标侧齐备（链接用 ``lexists`` 判条目本身，
       故源里的悬空链接不会让校验恒失败，见审计 A6）；
    7. 没有任何「按 skip_names 跳过、既未复制也不在清单里」的项 —— 这类项
       会随源目录一起被删，故直接判失败保住源（见审计 A5）。
    """
    missing: List[str] = []
    size_bad: List[str] = []
    content_bad: List[str] = []
    source_changed: List[str] = []

    total = len(result.items)
    done = 0
    last_report = 0.0

    for item in result.items:
        # 取消与 ``copy_merge`` 同口径：立刻停下，由调用方按 ST_CANCELLED 处理
        if is_cancelled and is_cancelled():
            raise CopyCancelled
        target = os.path.join(dst, item.target_rel)
        fingerprint = item.fingerprint
        if not os.path.isfile(_lp(target)):
            missing.append(item.target_rel)
        else:
            same_size, same_time = _matches_signature(target, fingerprint)
            if not same_size:
                size_bad.append(f"{item.target_rel}(期望 {fingerprint.size})")
            else:
                try:
                    if fingerprint.digest:
                        if _digest_file(target, is_cancelled) != fingerprint.digest:
                            content_bad.append(item.target_rel)
                    elif not same_time:
                        content_bad.append(item.target_rel)
                except OSError as exc:
                    missing.append(f"{item.target_rel}({exc})")

            try:
                source_info = os.stat(_lp(os.path.join(src, item.rel)))
                if (
                    source_info.st_size != fingerprint.size
                    or source_info.st_mtime_ns != fingerprint.mtime_ns
                ):
                    source_changed.append(item.rel)
            except OSError:
                source_changed.append(item.rel)

        done += 1
        now = time.monotonic()
        if on_progress and (
            now - last_report >= _PROGRESS_INTERVAL or done == total
        ):
            last_report = now
            on_progress(done, max(total, 1), item.target_rel)

    tree = enumerate_source(src, result.skip_names)
    manifest_rels = {item.rel for item in result.items}
    extra_source = sorted(set(tree.files) - manifest_rels)
    missing_source = sorted(manifest_rels - set(tree.files))
    # 源侧与目标侧都要看：源侧差集说明「有东西没进清单」，
    # 目标侧差集说明「清单里的东西没落地」
    absent_dirs = sorted(
        rel for rel in set(tree.dirs) if not os.path.isdir(_lp(os.path.join(dst, rel)))
    )
    # 审计 A6：判"链接是否已就位"必须用 lexists（判条目本身），不能用 exists
    # ——exists 会跟随链接，源里的**悬空**符号链接在目标侧重建成同样悬空的链接后
    # 仍被判为"缺失"，于是该文件夹永远报"校验未通过"、永远搬不完。
    absent_links = sorted(
        rel for rel in set(tree.links) if not os.path.lexists(_lp(os.path.join(dst, rel)))
    )

    problems: List[str] = []
    if tree.errors:
        problems.append(f"源目录无法完整枚举：{tree.errors[0]}")
    if missing:
        problems.append(f"目标缺失 {len(missing)} 项，例如 {missing[:3]}")
    if size_bad:
        problems.append(f"大小不符 {len(size_bad)} 项，例如 {size_bad[:3]}")
    if content_bad:
        problems.append(f"内容与源不一致 {len(content_bad)} 项，例如 {content_bad[:3]}")
    if source_changed:
        problems.append(
            f"源在复制期间被改动 {len(source_changed)} 项，例如 {source_changed[:3]}"
        )
    if extra_source:
        problems.append(f"源中有 {len(extra_source)} 项未进入清单，例如 {extra_source[:3]}")
    if missing_source:
        problems.append(f"清单中有 {len(missing_source)} 项已不在源中，例如 {missing_source[:3]}")
    if absent_dirs:
        problems.append(f"目标缺少 {len(absent_dirs)} 个目录（如空目录），例如 {absent_dirs[:3]}")
    if absent_links:
        problems.append(f"目标缺少 {len(absent_links)} 个链接，例如 {absent_links[:3]}")
    # 审计 A5：按 skip_names 跳过的项**既没复制也不在清单里**，而调用方是通过
    # 「删掉整个源目录」来收尾的——被跳过的文件会跟着源一起消失且无人察觉。
    # 故只要存在跳过项就让校验失败（fail-closed），把源目录保住。
    skipped = sorted(set(result.skipped))
    if skipped:
        problems.append(
            f"有 {len(skipped)} 项按跳过规则未复制（例如 {skipped[:3]}）——"
            "源目录不会被删除"
        )

    if problems:
        text = "；".join(problems[:4])
        if len(problems) > 4:
            # 审计 B-3：截断必须说清总数，否则用户分不清「这就是全部」还是「这只是前四条」
            text += f"（共 {len(problems)} 项，以上仅列前 4 条）"
        return False, text

    detail = f"已校验 {len(result.items)} 个文件 / {len(result.dirs)} 个目录"
    if result.skipped:
        detail += f"，另有 {len(result.skipped)} 项按规则跳过"
    if result.conflicts:
        detail += f"，{len(result.conflicts)} 个同名文件已另存为 (1)"
    # 审计 P2-5：把「按稀疏方式复制 / 以硬链接重建」显式报出来，避免用户
    # 误以为目标体积异常是数据问题。
    if result.sparse_files:
        detail += f"，{result.sparse_files} 个稀疏文件（零区保留为洞）"
    if result.hardlinked_files:
        detail += f"，{result.hardlinked_files} 个硬链接（未重复占用空间）"
    return True, detail


def safe_remove_directory(path: str) -> Tuple[bool, str]:
    """仅删除目录，且要求它确实是目录。"""
    if not os.path.exists(_lp(path)):
        return True, "源目录已不存在"
    if not os.path.isdir(_lp(path)):
        return False, f"拒绝删除非目录对象：{path}"
    try:
        shutil.rmtree(_lp(path), ignore_errors=False)
        return True, "已删除源目录"
    except OSError as exc:
        return False, f"删除源目录失败：{exc}"


def try_atomic_move(src: str, dst: str) -> Tuple[bool, str]:
    """同盘且目标不存在时用原子重命名完成迁移。

    最终审计 P1-1：与 :func:`copy_merge` 同源的**执行期**闸门——目标自身或其任一级
    已存在的上级目录若成了链接/联接点，``os.makedirs`` 会跟随它建目录、
    ``os.rename`` 会把整棵源目录搬进链接指向的位置，随后删源收尾。
    ``os.path.exists(_lp(dst))`` 只看得见「目标自身是不是存在」，看不见祖先链，
    故此处显式复查一次（:func:`reparse_ancestor`）。
    """
    blocked = reparse_ancestor(dst)
    if blocked:
        return False, (
            f"目标或其上级是链接/联接点（{blocked}），改用复制合并并拒绝写入"
        )
    if os.path.exists(_lp(dst)):
        return False, "目标已存在，改用复制合并"
    if volume_key(src) != volume_key(dst):
        parent = os.path.dirname(os.path.abspath(dst))
        if volume_key(parent) != volume_key(src):
            return False, "源与目标不同卷，改用复制合并"
    try:
        os.makedirs(_lp(os.path.dirname(os.path.abspath(dst))), exist_ok=True)
        os.rename(_lp(src), _lp(dst))
        return True, "已通过原子重命名完成转移"
    except OSError as exc:
        return False, f"重命名失败（{exc}），改用复制合并"


def ensure_directory(path: str) -> Tuple[bool, str]:
    try:
        os.makedirs(_lp(path), exist_ok=True)
        return True, ""
    except OSError as exc:
        return False, f"无法创建目录 {path}：{exc}"
