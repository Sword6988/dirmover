"""发布产物只读核验：把「一次性探针」沉淀为**可重复执行**的验收项。

审计 §5.3-9 / §5.3-10：过去这几项属性（内嵌解释器、图标内嵌、版本资源、产物↔源码
同源）都是审计当时临时写脚本核验的，重打包后要再验一次就得重写。本脚本把它们固化
成一个常驻项——``build.py`` 打包成功后会**自动调用**它，把「发布关键属性」变成构建
断言；平日也可单独执行，一键复验任一产物。

本脚本**不运行** exe（避免 UAC 提权与真实改注册表），只做静态核验。核验项（全部只读）：

  1. PE 头：x64（``Machine=0x8664``）/ GUI 子系统（``Subsystem=2``）/ 内嵌提权清单
     （``requireAdministrator``）——该工具必须提权运行，缺了就是交付缺陷。
  2. 内嵌解释器：含 ``python3XX.dll``（XX 取自 ``tools.ci_check.EXPECTED_PYTHON``，
     与构建断言同源），且**不含**更早次版本的 DLL——防止「文档声称 3.14、实物却是 3.13」。
  3. 图标：``assets/app.ico`` 的**每一帧**像素载荷都逐帧内嵌在 exe 中（8 帧全中）。
  4. 版本资源：``FileVersion`` / ``ProductVersion`` / ``FileDescription`` / ``ProductName`` /
     ``CompanyName`` / ``InternalName`` / ``OriginalFilename`` 与 ``docmover.constants``
     （**唯一真源**）逐字段一致。
  5. 产物↔源码同源：exe 内 PYZ 里若干关键模块的代码对象与**当前源码逐字节一致**
     （比「看时间戳」强得多的证据——排除「改了源码但 exe 是旧的」）。

单一真源：版本号取自 ``APP_VERSION``、期望解释器取自 ``tools.ci_check.EXPECTED_PYTHON``，
本脚本不另写一份。若缺少 PyInstaller（非构建环境），第 5 项降级为 ``[warn]`` 跳过，
其余项照常核验；第 4 项的版本 API 走 ``ctypes``（标准库），无第三方依赖。

用法：
    <python> tools/verify_release.py               # 核验 ../dist/DirMover.exe
    <python> tools/verify_release.py --exe <路径>  # 指定产物
退出码：0 = 全部通过；非 0 = 有失败项。
输出一律用 ``[OK]`` / ``[FAIL]`` / ``[warn]``（与 ci_check 同风格，避免 GBK 控制台
无法编码 ✓/✗ 或 emoji）。
"""

from __future__ import annotations

import argparse
import ctypes
import struct
import sys
import tempfile
from pathlib import Path
from typing import Dict, List, Tuple

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent                       # pydm/
DEFAULT_EXE = PROJECT.parent / "dist" / "DirMover.exe"

# 与 build.py 同样：保证无论从哪个工作目录调用都能导入 docmover / tools。
# （``tools`` 是 PEP 420 命名空间包，故 ``from tools.ci_check import ...`` 要求
# ``PROJECT`` 在 sys.path 上——这也是 build.py 的既有用法。）
sys.path.insert(0, str(PROJECT))

from docmover.constants import (  # noqa: E402
    APP_AUTHOR,
    APP_NAME,
    APP_TITLE,
    APP_VERSION,
)
from tools.ci_check import EXPECTED_PYTHON  # noqa: E402

ICON_PATH = PROJECT / "assets" / "app.ico"

# 应证明「产物里的字节码 == 当前源码」的关键模块：覆盖数据安全核心链路 + 界面。
# 与源码同源核验只比对 co_code / co_names / co_consts，不 import，故不依赖 tkinter。
SOURCE_MODULES: Tuple[str, ...] = (
    "docmover.constants",
    "docmover.config",
    "docmover.registry",
    "docmover.shell",
    "docmover.fsops",
    "docmover.transfer",
    "docmover.ui.theme",
    "docmover.ui.main_window",
)

_PE_MACHINE_AMD64 = 0x8664
_PE_SUBSYSTEM_WINDOWS_GUI = 2

# 版本资源里应逐字段一致的 StringFileInfo 项 → 期望值（真源 = constants）。
_VERSION_FIELDS: Tuple[str, ...] = (
    "FileVersion",
    "ProductVersion",
    "FileDescription",
    "ProductName",
    "CompanyName",
    "InternalName",
    "OriginalFilename",
)


class _Report:
    """极简结果收集器：``[OK]`` / ``[FAIL]`` / ``[warn]``，任一 FAIL 即整体失败。"""

    def __init__(self) -> None:
        self.failed = False

    def ok(self, label: str, detail: str = "") -> None:
        print(f"[OK] {label}" + (f"：{detail}" if detail else ""))

    def bad(self, label: str, detail: str = "") -> None:
        self.failed = True
        print(f"[FAIL] {label}" + (f"：{detail}" if detail else ""))

    def warn(self, label: str, detail: str = "") -> None:
        print(f"[warn] {label}" + (f"：{detail}" if detail else ""))


def _four_part_version(version: str) -> str:
    """``3.0.0`` → ``3.0.0.0``（与 build.py 渲染版本资源的规则一致）。

    局部实现而非 import build.py：build.py 会 import 本模块，避免循环引用；
    规则单一真源仍在 ``APP_VERSION``。
    """
    parts: List[int] = []
    for chunk in str(version).split("."):
        digits = "".join(ch for ch in chunk if ch.isdigit())
        parts.append(int(digits) if digits else 0)
    while len(parts) < 4:
        parts.append(0)
    return ".".join(str(p) for p in parts[:4])


def _read_pe_header(data: bytes) -> Tuple[int, int]:
    """返回 (Machine, Subsystem)；非 PE 文件抛 ``ValueError``。"""
    if len(data) < 0x40:
        raise ValueError("文件过小，不是有效的 PE")
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise ValueError("缺少 PE\\0\\0 签名")
    machine = struct.unpack_from("<H", data, e_lfanew + 4)[0]
    # COFF Header 20 字节 + OptionalHeader 里 Subsystem 的偏移 68
    subsystem = struct.unpack_from("<H", data, e_lfanew + 4 + 20 + 68)[0]
    return machine, subsystem


def _ico_frames(path: Path) -> List[Tuple[int, int, bytes]]:
    """解析 .ico，返回 [(宽, 高, 像素载荷字节)]（``w/h==0`` 表示 256）。"""
    data = path.read_bytes()
    count = struct.unpack_from("<H", data, 4)[0]
    frames: List[Tuple[int, int, bytes]] = []
    for i in range(count):
        off = 6 + i * 16
        w, h, _colors, _res, _planes, _bpp, size, offset = struct.unpack_from(
            "<BBBBHHII", data, off
        )
        frames.append((w or 256, h or 256, data[offset:offset + size]))
    return frames


def _file_version_strings(exe: Path) -> Dict[str, str]:
    """用 Windows 版本 API（``version.dll``，经 ctypes）读取版本资源 StringFileInfo。

    零第三方依赖；仅在 Windows 上可用（本工具本就面向 Windows）。资源缺失或查询
    失败即抛 ``OSError``，由调用方转成 ``[FAIL]``。
    """
    from ctypes import wintypes

    version = ctypes.WinDLL("version")
    version.GetFileVersionInfoSizeW.argtypes = [
        wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD),
    ]
    version.GetFileVersionInfoSizeW.restype = wintypes.DWORD
    version.GetFileVersionInfoW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
    ]
    version.GetFileVersionInfoW.restype = wintypes.BOOL
    version.VerQueryValueW.argtypes = [
        ctypes.c_void_p, wintypes.LPCWSTR,
        ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.UINT),
    ]
    version.VerQueryValueW.restype = wintypes.BOOL

    path = str(exe)
    handle = wintypes.DWORD(0)
    size = version.GetFileVersionInfoSizeW(path, ctypes.byref(handle))
    if not size:
        raise OSError("GetFileVersionInfoSizeW 失败（exe 可能不含版本资源）")
    buffer = ctypes.create_string_buffer(size)
    if not version.GetFileVersionInfoW(path, 0, size, buffer):
        raise OSError("GetFileVersionInfoW 失败")

    translations = ctypes.c_void_p()
    tlen = wintypes.UINT()
    if not version.VerQueryValueW(
        buffer, "\\VarFileInfo\\Translation",
        ctypes.byref(translations), ctypes.byref(tlen),
    ):
        raise OSError("版本资源缺少 VarFileInfo\\\\Translation")
    langid, codepage = struct.unpack_from("<HH", ctypes.string_at(translations.value, 4))

    found: Dict[str, str] = {}
    for key in _VERSION_FIELDS:
        sub = f"\\StringFileInfo\\{langid:04x}{codepage:04x}\\{key}"
        ptr = ctypes.c_void_p()
        length = wintypes.UINT()
        if version.VerQueryValueW(buffer, sub, ctypes.byref(ptr), ctypes.byref(length)):
            # VerQueryValueW 对字符串返回的 ``length`` 是**含结尾 NUL 的字符数**，
            # 故按 NUL 截断读取（``wstring_at`` 读到 NUL 即止），再兜底去尾。
            found[key] = ctypes.wstring_at(ptr.value).rstrip("\x00")
    return found


def check_pe(data: bytes, report: _Report) -> None:
    """核验 PE 头：x64 / GUI 子系统 / 提权清单。"""
    try:
        machine, subsystem = _read_pe_header(data)
    except ValueError as exc:
        report.bad("PE 头", str(exc))
        return
    ok = True
    if machine != _PE_MACHINE_AMD64:
        report.bad("PE 架构", f"Machine=0x{machine:04X}，期望 x64（0x8664）")
        ok = False
    if subsystem != _PE_SUBSYSTEM_WINDOWS_GUI:
        report.bad("PE 子系统", f"Subsystem={subsystem}，期望 GUI（2）")
        ok = False
    if b"requireAdministrator" not in data:
        report.bad("提权清单", "exe 内未见 requireAdministrator（该工具必须提权运行）")
        ok = False
    if ok:
        report.ok("PE 头", "x64 / GUI 子系统 / requireAdministrator")


def check_interpreter(data: bytes, report: _Report) -> None:
    """核验内嵌解释器 DLL：含期望次版本、不含更早次版本。"""
    major, minor = EXPECTED_PYTHON
    expected = f"python{major}{minor}.dll".encode()
    if expected not in data:
        report.bad("内嵌解释器", f"未见 {expected.decode()}（期望内嵌 CPython {major}.{minor}）")
        return
    strays = [
        f"python{major}{m}.dll"
        for m in range(minor - 1, 5, -1)
        if f"python{major}{m}.dll".encode() in data
    ]
    if strays:
        report.bad("内嵌解释器", f"含 {expected.decode()}，但也含旧次版本：{'、'.join(strays)}")
        return
    report.ok("内嵌解释器", f"含 {expected.decode()}，无更早次版本 DLL")


def check_icon(data: bytes, report: _Report) -> None:
    """核验 assets/app.ico 的每一帧像素载荷都逐帧内嵌在 exe 中。"""
    if not ICON_PATH.exists():
        report.bad("图标内嵌", f"缺少图标源 {ICON_PATH}")
        return
    frames = _ico_frames(ICON_PATH)
    if not frames:
        report.bad("图标内嵌", "assets/app.ico 无任何帧")
        return
    missing: List[str] = []
    for w, h, payload in frames:
        if payload and payload not in data:
            missing.append(f"{w}x{h}")
    if missing:
        report.bad("图标内嵌", f"{len(missing)}/{len(frames)} 帧缺失：{'、'.join(missing)}")
        return
    report.ok("图标内嵌", f"assets/app.ico {len(frames)}/{len(frames)} 帧全部内嵌")


def check_version_resource(exe: Path, report: _Report) -> None:
    """核验版本资源与 constants 逐字段一致（真源 = APP_VERSION / APP_*）。"""
    ver4 = _four_part_version(APP_VERSION)
    expected = {
        "FileVersion": ver4,
        "ProductVersion": ver4,
        "FileDescription": APP_TITLE,
        "ProductName": APP_TITLE,
        "CompanyName": APP_AUTHOR,
        "InternalName": APP_NAME,
        "OriginalFilename": f"{APP_NAME}.exe",
    }
    try:
        actual = _file_version_strings(exe)
    except OSError as exc:
        report.bad("版本资源", str(exc))
        return
    mismatched = [
        f"{k}：实物 {actual.get(k)!r} ≠ 期望 {v!r}"
        for k, v in expected.items()
        if actual.get(k) != v
    ]
    if mismatched:
        report.bad("版本资源", "；".join(mismatched))
        return
    report.ok("版本资源", f"7 个字段与 constants 一致（版本 {ver4}）")


def _walk_codes(codes: List[object], code: object) -> None:
    """递归收集代码对象（含嵌套函数）。"""
    for const in code.co_consts:  # type: ignore[attr-defined]
        if hasattr(const, "co_code"):
            codes.append(const)
            _walk_codes(codes, const)


def _compile_source(name: str) -> object:
    rel = Path(*name.split(".")).with_suffix(".py")
    path = PROJECT / rel
    return compile(path.read_bytes(), str(path), "exec")


def _compare_module(name: str, frozen: object) -> Tuple[bool, str]:
    ref = _compile_source(name)
    frozen_codes: List[object] = []
    source_codes: List[object] = []
    _walk_codes(frozen_codes, frozen)
    _walk_codes(source_codes, ref)
    frozen_map = {c.co_name: c for c in frozen_codes}  # type: ignore[attr-defined]
    source_map = {c.co_name: c for c in source_codes}  # type: ignore[attr-defined]
    if frozen_map.keys() != source_map.keys():
        return False, f"函数集合不一致：{sorted(frozen_map)} vs {sorted(source_map)}"
    bad = [
        fname
        for fname in sorted(source_map)
        if (
            frozen_map[fname].co_code != source_map[fname].co_code
            or frozen_map[fname].co_names != source_map[fname].co_names
            or frozen_map[fname].co_consts != source_map[fname].co_consts
        )
    ]
    if bad:
        return False, f"字节码不一致的函数：{bad}"
    return True, f"顶层 + {len(frozen_codes)} 个嵌套函数逐字节一致"


def check_source_identity(exe: Path, report: _Report) -> None:
    """核验 exe 内 PYZ 的关键模块与当前源码逐字节同源。

    缺少 PyInstaller（非构建环境）时降级为 ``[warn]`` 跳过——该环境无法读取 PYZ，
    并非产物有问题；构建流程（build.py）里 PyInstaller 恒在，此项必然执行。
    """
    try:
        from PyInstaller.archive.readers import CArchiveReader, ZlibArchiveReader
    except Exception as exc:  # noqa: BLE001 —— 缺依赖 / 导入期异常都降级
        report.warn("产物↔源码同源", f"未安装 PyInstaller（{exc}），本项跳过")
        return

    try:
        archive = CArchiveReader(str(exe))
        pyz_names = [n for n in archive.toc if n.endswith(".pyz")]
        if not pyz_names:
            report.bad("产物↔源码同源", "exe 内未找到 .pyz 归档")
            return
        with tempfile.NamedTemporaryFile(suffix=".pyz", delete=False) as tmp:
            tmp.write(archive.extract(pyz_names[0]))
            tmp_path = Path(tmp.name)
    except Exception as exc:  # noqa: BLE001 —— 非 PyInstaller 产物会抛 ArchiveReadError
        report.bad("产物↔源码同源", f"无法读取 exe 内归档（{exc}）")
        return
    try:
        reader = ZlibArchiveReader(str(tmp_path))
        missing = [n for n in SOURCE_MODULES if n not in reader.toc]
        if missing:
            report.bad("产物↔源码同源", f"{len(missing)} 个模块不在 PYZ 中：{'、'.join(missing)}")
            return
        bad: List[str] = []
        for name in SOURCE_MODULES:
            same, detail = _compare_module(name, reader.extract(name))
            if not same:
                bad.append(f"{name}（{detail}）")
        if bad:
            report.bad("产物↔源码同源", "；".join(bad))
            return
    finally:
        tmp_path.unlink(missing_ok=True)

    report.ok("产物↔源码同源", f"{len(SOURCE_MODULES)} 个模块逐字节一致（PYZ == 当前源码）")


def verify_release(exe: Path) -> int:
    """核验指定产物；返回 0 = 全过，非 0 = 有失败项。供 build.py 直接调用。"""
    print("== verify_release：发布产物只读核验 ==")
    print(f"产物：{exe}")
    if not exe.exists():
        print(f"[FAIL] 找不到产物：{exe}")
        return 1

    data = exe.read_bytes()
    size = len(data)
    print(f"字节数：{size:,}（{size / 1024 / 1024:.2f} MB）")
    print(f"真源：APP_VERSION={APP_VERSION}，EXPECTED_PYTHON={EXPECTED_PYTHON[0]}.{EXPECTED_PYTHON[1]}")

    report = _Report()
    check_pe(data, report)
    check_interpreter(data, report)
    check_icon(data, report)
    check_version_resource(exe, report)
    check_source_identity(exe, report)

    print("== 结论：" + ("[FAIL] 有项目未通过" if report.failed else "[OK] 全部通过") + " ==")
    return 1 if report.failed else 0


def main() -> int:
    parser = argparse.ArgumentParser(description="发布产物只读核验")
    parser.add_argument(
        "--exe", type=Path, default=DEFAULT_EXE,
        help=f"待核验的 exe（默认：{DEFAULT_EXE}）",
    )
    args = parser.parse_args()
    return verify_release(args.exe)


if __name__ == "__main__":
    sys.exit(main())
