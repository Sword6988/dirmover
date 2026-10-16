"""开发工具：对打包后的 exe 做启动冒烟测试（含反「假通过」加固，审计 F4）。

流程：预检无残留实例 → 启动 exe → 等待「归属本次启动进程（或其 onefile 子进程）」的
主窗口（class 精确 = ``TkTopLevel``、标题精确匹配、且内含足够 ``TkChild`` 控件）→ 截图 →
发送 ``WM_CLOSE`` → 确认进程退出（exit=0）。

审计 F4（QA 独立复验发现）：原实现**会「假通过」**，因为它
    * 不绑定 PID——只按标题在整个桌面搜窗口，机器上若有**残留的 DirMover 实例**，
      0.0 秒就"找到"了别人的窗口；
    * 踩到 onefile 的**两进程模型**——``proc.wait()`` 等的是本脚本启动的 bootloader，
      若本次启动因重复启动已立即退出 → ``exit=0``，「假通过」；
    * 完全不检查任何控件（ListView / 状态栏）。
本项目有过「测试假通过」的翻车史，故本版据此加固：
    * **PID 绑定**：窗口 PID 必须 ∈ {本次启动的 bootloader PID} ∪ {其全部子进程}；
    * **class + 标题精确匹配**：``TkTopLevel`` + ``用户数据转移工具``；
    * **控件级存在性检查**：窗口内 ``TkChild`` 数量 ≥ 阈值（Tk 把控件画在 ``TkChild``
      子窗口里、文案无法用 Win32 读取，故按"存在足够控件"判定界面确实构建出来了）；
    * **残留实例预检**：先于本次启动就存在的 DirMover 一律**报错退出**，绝不张冠李戴；
    * 失败时**退出码非 0** 并打印足够诊断（PID / 找到的窗口 / class / 标题 / 控件数）。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time
from pathlib import Path
from typing import List, Optional, Set, Tuple

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent
EXE = PROJECT.parent / "dist" / "DirMover.exe"

WM_CLOSE = 0x0010
INFINITE_HINT = 30.0

# 期望的主窗口属性（单一真源：docmover.constants.APP_TITLE = 用户数据转移工具；
# Tk 顶层窗口的 Win32 class 固定为 TkTopLevel）。
EXPECTED_TITLE = "用户数据转移工具"
EXPECTED_CLASS = "TkTopLevel"
# 控件级存在性阈值：主窗口是 7 行网格（目标根 / 项目列表 / 操作条 / 状态栏…），
# 正常构建出的 TkChild 远多于 5；取一个保守阈值，既能抓"空窗口/构建失败"又不误报。
MIN_TK_CHILDREN = 5

IMAGE_NAME = "DirMover.exe"

# ---------------------------------------------------------------- Win32 声明
_user32 = ctypes.windll.user32
_kernel32 = ctypes.windll.kernel32

TH32CS_SNAPPROCESS = 0x00000002
MAX_PATH = 260
_INVALID_HANDLE = ctypes.c_void_p(-1).value


class _PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wt.DWORD),
        ("cntUsage", wt.DWORD),
        ("th32ProcessID", wt.DWORD),
        ("th32DefaultHeapID", ctypes.c_void_p),
        ("th32ModuleID", wt.DWORD),
        ("cntThreads", wt.DWORD),
        ("th32ParentProcessID", wt.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wt.DWORD),
        ("szExeFile", wt.WCHAR * MAX_PATH),
    ]


def _declare() -> None:
    """显式声明用到的 kernel32/user32 原型——64 位下 HANDLE/HWND 是 8 字节指针，
    若沿用 ctypes 默认的 32 位 int 会被**截断**（历史坑，务必声明）。"""
    _kernel32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
    _kernel32.CreateToolhelp32Snapshot.restype = wt.HANDLE
    _kernel32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
    _kernel32.Process32FirstW.restype = wt.BOOL
    _kernel32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
    _kernel32.Process32NextW.restype = wt.BOOL
    _kernel32.CloseHandle.argtypes = [wt.HANDLE]
    _kernel32.CloseHandle.restype = wt.BOOL

    _user32.IsWindowVisible.argtypes = [wt.HWND]
    _user32.IsWindowVisible.restype = wt.BOOL
    _user32.GetWindowTextLengthW.argtypes = [wt.HWND]
    _user32.GetWindowTextLengthW.restype = ctypes.c_int
    _user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
    _user32.GetWindowTextW.restype = ctypes.c_int
    _user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
    _user32.GetClassNameW.restype = ctypes.c_int
    _user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
    _user32.GetWindowThreadProcessId.restype = wt.DWORD
    _user32.EnumWindows.argtypes = [
        ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM), wt.LPARAM
    ]
    _user32.EnumWindows.restype = wt.BOOL
    _user32.EnumChildWindows.argtypes = [
        wt.HWND, ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM), wt.LPARAM
    ]
    _user32.EnumChildWindows.restype = wt.BOOL
    _user32.SetWindowPos.argtypes = [
        wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        ctypes.c_uint,
    ]
    _user32.SetWindowPos.restype = wt.BOOL
    _user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
    _user32.GetWindowRect.restype = wt.BOOL
    _user32.PostMessageW.argtypes = [wt.HWND, ctypes.c_uint, wt.WPARAM, wt.LPARAM]
    _user32.PostMessageW.restype = wt.BOOL


def enable_dpi() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


# ---------------------------------------------------------------- 进程枚举
def _iter_processes() -> List[Tuple[int, int, str]]:
    """返回 ``[(pid, ppid, exe_name), ...]``（经 Toolhelp32 快照）。"""
    snap = _kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap or snap == _INVALID_HANDLE:
        return []
    procs: List[Tuple[int, int, str]] = []
    try:
        entry = _PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(_PROCESSENTRY32W)
        ok = _kernel32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            procs.append(
                (int(entry.th32ProcessID), int(entry.th32ParentProcessID),
                 str(entry.szExeFile))
            )
            ok = _kernel32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        _kernel32.CloseHandle(snap)
    return procs


def _docmover_pids() -> Set[int]:
    """当前系统上所有镜像名为 ``DirMover.exe`` 的进程 PID。"""
    return {
        pid for pid, _ppid, name in _iter_processes()
        if name.lower() == IMAGE_NAME.lower()
    }


def _descendants(root_pid: int, procs: Optional[List[Tuple[int, int, str]]] = None) -> Set[int]:
    """``root_pid`` 的**全部后代** PID（含多层；onefile 启动的子进程通常是一层）。"""
    procs = procs if procs is not None else _iter_processes()
    children: dict = {}
    for pid, ppid, _name in procs:
        children.setdefault(ppid, []).append(pid)
    out: Set[int] = set()
    stack = [root_pid]
    while stack:
        parent = stack.pop()
        for child in children.get(parent, []):
            if child not in out:
                out.add(child)
                stack.append(child)
    return out


# ---------------------------------------------------------------- 窗口枚举
def _window_info(hwnd: int) -> Tuple[str, str, int]:
    """返回 (标题, class, PID)。"""
    length = _user32.GetWindowTextLengthW(wt.HWND(hwnd))
    buf = ctypes.create_unicode_buffer(length + 1)
    _user32.GetWindowTextW(wt.HWND(hwnd), buf, length + 1)
    cls = ctypes.create_unicode_buffer(256)
    _user32.GetClassNameW(wt.HWND(hwnd), cls, 256)
    pid = wt.DWORD()
    _user32.GetWindowThreadProcessId(wt.HWND(hwnd), ctypes.byref(pid))
    return buf.value, cls.value, int(pid.value)


def find_window(
    title: str, cls: str, allowed_pids: Set[int], timeout: float
) -> Optional[Tuple[int, int, str, str]]:
    """等一个**归属 ``allowed_pids``** 且标题/class **精确匹配**的可见顶层窗口。

    返回 ``(hwnd, pid, title, cls)``；超时返回 ``None``。
    """
    deadline = time.time() + timeout
    while time.time() < deadline:
        found: List[Tuple[int, int, str, str]] = []

        @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        def callback(hwnd, _lparam):
            text, klass, pid = _window_info(hwnd)
            if (text == title and klass == cls and pid in allowed_pids
                    and _user32.IsWindowVisible(wt.HWND(hwnd))):
                found.append((int(hwnd), pid, text, klass))
            return True

        _user32.EnumWindows(callback, 0)
        if found:
            return found[0]
        time.sleep(0.3)
    return None


def count_children_by_class(hwnd: int, cls: str) -> int:
    """窗口内**后代**中 class 精确等于 ``cls`` 的数量（``EnumChildWindows`` 递归）。"""
    count = [0]

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def callback(child, _lparam):
        klass = ctypes.create_unicode_buffer(256)
        _user32.GetClassNameW(wt.HWND(child), klass, 256)
        if klass.value == cls:
            count[0] += 1
        return True

    _user32.EnumChildWindows(wt.HWND(hwnd), callback, 0)
    return count[0]


def _dump_windows() -> None:
    """诊断：列出桌面上所有 ``TkTopLevel`` 窗口（含标题 / class / PID / 可见性）。"""
    rows: List[str] = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def callback(hwnd, _lparam):
        text, klass, pid = _window_info(hwnd)
        if klass == EXPECTED_CLASS or IMAGE_NAME in text or EXPECTED_TITLE in text:
            rows.append(
                f"    hwnd={int(hwnd)} pid={pid} visible={bool(_user32.IsWindowVisible(wt.HWND(hwnd)))} "
                f"class={klass!r} title={text!r}"
            )
        return True

    _user32.EnumWindows(callback, 0)
    if rows:
        print("[诊断] 桌面上的 Tk 顶层窗口：")
        for row in rows:
            print(row)
    else:
        print("[诊断] 桌面上未发现任何 Tk 顶层窗口。")
    print(f"[诊断] 当前 DirMover.exe 进程 PID：{sorted(_docmover_pids())}")


# ---------------------------------------------------------------- 截图 / 前台
def bring_to_front(hwnd: int) -> None:
    """把目标窗口提到最前，否则截到的是叠在它上面的无关窗口（实测缺陷：
    抓屏按窗口矩形裁剪的是「屏幕像素」，不看 z 序——结果截到聊天窗口）。

    用 TOPMOST→NOTOPMOST 两连跳（经典前台化手法）：SetForegroundWindow 在
    调用方非前台时会被系统静默拒绝，而 SetWindowPos 的这两步是可靠的。
    失败仅影响截图清晰度，不冒烟语义，故静默吞掉。
    """
    swp = 0x0001 | 0x0002          # SWP_NOSIZE | SWP_NOMOVE
    try:
        _user32.SetWindowPos(wt.HWND(hwnd), wt.HWND(-1), 0, 0, 0, 0, swp)   # TOPMOST
        _user32.SetWindowPos(wt.HWND(hwnd), wt.HWND(-2), 0, 0, 0, 0, swp)   # NOTOPMOST
    except Exception:
        pass


def grab(hwnd: int, out_path: Path) -> bool:
    from PIL import ImageGrab

    bring_to_front(hwnd)
    time.sleep(0.6)                # 等窗口真正置顶、完成重绘
    rect = wt.RECT()
    _user32.GetWindowRect(wt.HWND(hwnd), ctypes.byref(rect))
    box = (rect.left, rect.top, rect.right, rect.bottom)
    image = ImageGrab.grab(bbox=box, all_screens=True)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    image.save(out_path)
    print(f"[OK] 截图 {out_path.name}  {image.size[0]}x{image.size[1]}")
    return True


# ---------------------------------------------------------------- 主流程
def main() -> int:
    _declare()
    enable_dpi()
    if not EXE.exists():
        print(f"[FAIL] 未找到 {EXE}")
        return 1

    # F4 加固①：预检残留实例——先于本次启动就存在的 DirMover 一律拒绝，否则会
    # "找到别人的窗口"造成假通过。
    pre_existing = _docmover_pids()
    if pre_existing:
        print(
            f"[FAIL] 检测到**先于本次启动就存在**的 DirMover 实例：PID={sorted(pre_existing)}。"
        )
        print("       为避免张冠李戴的假通过，请先关闭这些实例（taskkill /f /im DirMover.exe）再跑冒烟。")
        return 1

    print(f"[..] 启动 {EXE.name} …")
    started = time.time()
    try:
        proc = subprocess.Popen([str(EXE)])
    except OSError as exc:
        # 产物带 requireAdministrator 清单（build.py 的 --uac-admin）：非提权会话里
        # CreateProcess 直接失败（WinError 740 需要提升），Popen 抛 OSError。
        # 这里给明确指引，不要留一个 traceback 让使用者以为是脚本坏了。
        print(
            f"[FAIL] 无法启动 exe（{exc}）。\n"
            "       若为 WinError 740（请求的操作需要提升）：请在本机的**管理员会话**\n"
            "       中运行本冒烟脚本，或改用 ShellExecuteW(\"runas\") 启动；\n"
            "       产物本身带有 requireAdministrator 清单，普通权限无法直接起进程。"
        )
        return 1

    # F4 加固②：窗口归属绑定——onefile 是**两进程**模型，真正创建 Tk 窗口的是
    # bootloader（proc.pid）派生的子进程，故允许集 = {proc.pid} ∪ 其全部后代。
    # 子进程可能在启动后一小会儿才出现，故在 find_window 的轮询里**动态**重算。
    rows: List[Tuple[int, int, str, str]] = []
    allowed: Set[int] = {proc.pid}
    deadline = time.time() + INFINITE_HINT
    found: Optional[Tuple[int, int, str, str]] = None
    while time.time() < deadline:
        allowed = {proc.pid} | _descendants(proc.pid)
        result = find_window(EXPECTED_TITLE, EXPECTED_CLASS, allowed, 0.5)
        if result is not None:
            found = result
            break
        if proc.poll() is not None:
            # 本次进程已退出却没出现我们的窗口——极可能是重复启动（被互斥体劝退）
            # 或崩溃；绝不能拿"别人的窗口"顶上，直接失败。
            print(f"[FAIL] 本次启动的进程已退出（exit={proc.poll()}），但未出现归属它的主窗口。")
            print(f"       期望：class={EXPECTED_CLASS!r}，title={EXPECTED_TITLE!r}，归属PID∈{sorted(allowed)}")
            _dump_windows()
            return 1
    if found is None:
        print("[FAIL] 30 秒内未出现「归属本次启动进程」的主窗口")
        print(f"       期望：class={EXPECTED_CLASS!r}，title={EXPECTED_TITLE!r}，归属PID∈{sorted(allowed)}")
        _dump_windows()
        proc.terminate()
        return 1

    hwnd, pid, title, klass = found
    elapsed = time.time() - started
    print(f"[OK] 主窗口出现（{elapsed:.1f} 秒，PID={pid}，hwnd={hwnd}，class={klass}，title={title}）")
    print(f"[OK] PID 绑定校验通过（窗口 PID={pid} ∈ 本次启动 {proc.pid} 及其子进程）")

    # F4 加固③：控件级存在性检查（Tk 把控件画在 TkChild 子窗口里、文案无法用 Win32 读，
    # 故按"存在足够多 TkChild"判定界面确实构建出来，再截图留证）。
    children = count_children_by_class(hwnd, "TkChild")
    print(f"[..] 主窗口内 TkChild 控件数 = {children}（阈值 {MIN_TK_CHILDREN}）")
    if children < MIN_TK_CHILDREN:
        print(f"[FAIL] 控件不足：仅 {children} 个 TkChild（< {MIN_TK_CHILDREN}），界面可能未正确构建")
        _dump_windows()
        proc.terminate()
        return 1
    print(f"[OK] 控件级存在性检查通过（TkChild={children} ≥ {MIN_TK_CHILDREN}）")

    time.sleep(2.0)
    try:
        grab(hwnd, HERE / "screenshot_exe.png")
    except Exception as exc:
        # 无交互式桌面（无头 / 服务会话）下抓屏会失败；截图只是辅助证据，
        # 不应中断后续 WM_CLOSE 与退出码判定——核心冒烟照常完成。
        print(f"[warn] 截图失败（无交互式桌面？）：{exc}")

    _user32.PostMessageW(wt.HWND(hwnd), WM_CLOSE, 0, 0)
    try:
        code = proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        print("[FAIL] 15 秒后进程仍存活 → 关闭按钮异常")
        proc.kill()
        return 1
    if code != 0:
        print(f"[FAIL] WM_CLOSE 后进程退出码非 0（exit={code}）")
        return 1
    print(f"[OK] WM_CLOSE 后进程已退出（exit={code}）→ 关闭按钮生效")
    return 0


if __name__ == "__main__":
    sys.exit(main())
