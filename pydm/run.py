"""用户数据转移工具 —— 程序入口。

三种运行方式：
    <python> run.py              启动图形界面
    <python> run.py --scan       只读列出当前各文件夹的位置与状态
    <python> run.py --dry-run    完整走一遍流程但不做任何改动
"""

from __future__ import annotations

import argparse
import collections
import ctypes
import logging
import os
import sys
import tempfile
from pathlib import Path
from typing import List, Optional

# 保证以脚本或包两种方式运行都能找到 docmover
sys.path.insert(0, str(Path(__file__).resolve().parent))

from docmover import fsops, logging_setup, shell  # noqa: E402
from docmover.config import (  # noqa: E402
    AppConfig,
    app_dir,
    default_target_root,
)
from docmover.constants import (  # noqa: E402
    APP_NAME,
    APP_TITLE,
    APP_VERSION,
    BACKUP_DIRNAME,
    LOG_DIRNAME,
    FOLDERS,
    USER_SHELL_FOLDERS_SUBKEY,
)
from docmover.registry import RegistryStore  # noqa: E402
from docmover.transfer import IntentJournal, TransferEngine  # noqa: E402

MIN_SUPPORTED_MAJOR = 6
MIN_SUPPORTED_MINOR = 1


def _display_width(text: str) -> int:
    """按终端显示宽度计算字符串长度（中文占 2 列）。"""
    from unicodedata import east_asian_width

    return sum(2 if east_asian_width(ch) in ("W", "F") else 1 for ch in text)


def _pad(text: str, width: int) -> str:
    return text + " " * max(0, width - _display_width(text))


# ------------------------------------------------------------ 控制台编码

def _force_utf8_console() -> None:
    """Windows 中文控制台默认 GBK，输出中文会乱码甚至抛异常。"""
    for stream_name in ("stdout", "stderr"):
        stream = getattr(sys, stream_name, None)
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            try:
                reconfigure(encoding="utf-8", errors="replace")
            except (ValueError, OSError):
                pass


# -------------------------------------------------- 统一控制台输出（审计 P2-2）

# 以 ``--windowed`` 打包后 ``sys.stdout``/``sys.stderr`` 为 ``None``，``print``
# 会**静默丢弃**（CPython 3.11+ 不抛异常）。于是 ``DirMover.exe --scan`` 毫无
# 输出、退出码 0，用户以为程序坏了。这里在无流可用时把输出落到日志文件，并在
# **收尾时**弹一次可见提示（消息框），保证任何情况下都有可诊断的落点。
#
# 提示为什么延后到收尾（审计 C4）：提示里要带上"末尾若干行"结果，而在第一条
# 输出时就弹的话，缓冲区里只有那一行，摘出来没有诊断价值。缓冲区只在"确实没有
# stdout"时累积（有控制台时直接 print，不留副本）。

_CONSOLE_TAIL_LINES = 15
_console_tail: "collections.deque[str]" = collections.deque(maxlen=_CONSOLE_TAIL_LINES)
_CONSOLE_NOTIFY_DONE = False
# 本次运行的输出是否全部成功落盘。失败时提示必须改口径（见 _notify_no_console）。
_CONSOLE_LOG_OK = True


def _console_log_path() -> Path:
    """无控制台时的输出落盘位置。"""
    return app_dir() / LOG_DIRNAME / "console.log"


def _log_console_output(text: str) -> None:
    """把一行输出追加到日志文件；落盘失败也不抛（避免二次伤害）。

    失败只记在 ``_CONSOLE_LOG_OK`` 上，供收尾提示决定"指路"还是"贴内容"：
    exe 放在 ``C:\\Program Files`` 下时 ``app_dir()/logs`` 建不出来，此时再告诉
    用户"完整结果已写入日志文件"等于把他指向一个并不存在的文件。
    """
    global _CONSOLE_LOG_OK
    path = _console_log_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8", errors="replace") as handle:
            handle.write(text + "\n")
    except OSError:
        _CONSOLE_LOG_OK = False   # 一旦写不进去就保持"不可用"，宁可多贴内容


def _notify_no_console() -> None:
    """收尾时的一次性可见提示（仅弹一次，避免刷屏）。

    - 落盘成功：指引用户去看 ``console.log``，并附末尾若干行便于当场判读；
    - 落盘失败（无写权限）：**不写**"已写入日志文件"，直接把末尾若干行贴进
      消息框——排障指引不能指向一个不存在的文件。
    """
    global _CONSOLE_NOTIFY_DONE
    if _CONSOLE_NOTIFY_DONE:
        return
    _CONSOLE_NOTIFY_DONE = True
    tail = "\n".join(_console_tail)
    if _CONSOLE_LOG_OK:
        body = (
            "本程序以无控制台方式打包，命令行输出无法直接显示。\n\n"
            f"完整结果已写入日志文件：\n{_console_log_path()}\n\n"
            f"—— 末尾 {len(_console_tail)} 行 ——\n{tail}"
        )
    else:
        body = (
            "本程序以无控制台方式打包，命令行输出无法直接显示；\n"
            f"日志文件也无法写入（可能没有写权限）：\n{_console_log_path()}\n\n"
            f"以下为本次输出（末尾 {len(_console_tail)} 行）：\n{tail}"
        )
    try:
        ctypes.windll.user32.MessageBoxW(
            None,
            body,
            APP_TITLE,
            0x40,   # MB_ICONINFORMATION
        )
    except Exception:
        # 弹窗失败无所谓——结果已经在缓冲/日志里，仍可诊断
        pass


def _out(text: str = "") -> None:
    """统一的控制台输出：可写就 ``print``，否则落盘 + 缓冲（审计 P2-2 / C4）。

    提示不在本函数里弹（见文件头说明），由 :func:`_flush_console_notice` 在
    ``main`` 收尾时统一触发。
    """
    stream = getattr(sys, "stdout", None)
    if stream is not None:
        try:
            print(text, file=stream)
            return
        except (OSError, ValueError):
            pass
    _console_tail.append(text)
    _log_console_output(text)


def _flush_console_notice() -> None:
    """``main`` 收尾时的一次性提示：只在"确实无控制台且真的有过输出"时触发。

    没有输出（或输出走的是真实 stdout）时静默返回——``--help`` / ``--version``
    由 argparse 直接写 stdout，不经过 ``_out``，不该因此弹框。
    """
    if not _console_tail:
        return
    _notify_no_console()


# ---------------------------------------------------------- DPI 与单实例

def _enable_dpi_awareness() -> None:
    """让界面在高分屏上不发虚。"""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)  # PROCESS_SYSTEM_DPI_AWARE
        return
    except Exception:
        pass
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass


_SINGLE_INSTANCE_HANDLE = None
_KERNEL32 = None
_ERROR_ALREADY_EXISTS = 183
_MUTEX_NAME = "Local\\DirMover_Single"

# 审计 F6：互斥体的**持有者 PID**。
#
# 互斥体本身是用 NULL 安全描述符建在 ``Local\`` 下的：``Local\`` 按登录会话
# 隔离，但**同会话内跨完整性级别可见**，而 NULL DACL 让对象取创建者默认 DACL，
# 同一用户可以打开。于是**一个普通（非提权）进程**只要先创建同名互斥体，就能让
# 本程序永远停在"程序已在运行。请勿重复打开。"——可持续的拒绝服务（可恢复）。
#
# 因此"已存在"不能再单独作为阻断依据：还要确认持有者**真的是一个活着的
# DirMover**。PID 记在文件里（内核对象不便携带进程信息），用 ``tasklist`` 校验。
_MUTEX_OWNER_FILENAME = "single_instance.pid"


def _mutex_owner_path() -> Path:
    """互斥体持有者 PID 的落盘位置（审计 F6）。"""
    base = os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()
    return Path(base) / APP_NAME / _MUTEX_OWNER_FILENAME


def _write_mutex_owner(pid: int) -> None:
    """取得互斥体后记下本进程 PID。写不进去只影响"被抢占"判定的精度。"""
    try:
        path = _mutex_owner_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(str(pid), encoding="utf-8")
    except OSError:
        pass


def _read_mutex_owner() -> Optional[int]:
    """读取持有者 PID；读不到（文件缺失 / 内容损坏）返回 ``None``。"""
    try:
        return int(_mutex_owner_path().read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return None


def _clear_mutex_owner() -> None:
    try:
        _mutex_owner_path().unlink(missing_ok=True)
    except OSError:
        pass


def _app_image_name() -> str:
    """本程序在进程列表里的镜像名（打包产物=DirMover.exe，源码运行=python.exe）。"""
    return Path(sys.executable).name or f"{APP_NAME}.exe"


def _docmover_pids() -> "set[int]":
    """进程列表里与本程序同镜像名的 PID 集合。

    拿不到进程列表（``tasklist`` 失败 / 非 Windows）时返回**空集合**——上层据此
    按"校验无法完成 → 允许启动"处理，避免制造新的"已在运行"误报。
    """
    try:
        return set(shell.pids_by_image(_app_image_name()))
    except Exception:   # noqa: BLE001 - 校验手段失效不是错误，退化为"允许启动"
        return set()


def _mutex_preempted() -> bool:
    """已有同名互斥体，但持有者**不是**一个活着的 DirMover → 判定被抢占。

    * 持有者 PID 仍存活且镜像名与本程序一致 → 真的有另一个实例 → 不抢占（阻断）；
    * PID 已死 / 不对应本程序 / 读不到 PID → **允许启动**（不因别人的互斥体被挡）；
    * ``tasklist`` 拿不到进程列表 → 同样**允许启动**（宁可放过一次重复启动，
      也不给用户一个无法解释的"程序已在运行"）。
    """
    owner = _read_mutex_owner()
    if owner is None or owner == os.getpid():
        return True
    return owner not in _docmover_pids()


def _kernel32():
    """惰性取得声明了 argtypes/restype 的 kernel32（审计 C10）。

    为什么要严格声明：``ctypes`` 默认 ``restype`` 是 ``c_int``，64 位下 HANDLE
    会被截断成 32 位（未定义行为，只是恰好没炸）；而 ``GetLastError`` 也只有配
    ``use_last_error=True`` 才是 CPython 的契约行为（对应 ``ctypes.get_last_error()``），
    直接再调一次 ``kernel32.GetLastError()`` 并不被保证读到上一个调用的错误码。
    非 Windows 上 ``ctypes.WinDLL`` 会抛 OSError，由调用方兜底。
    """
    global _KERNEL32
    if _KERNEL32 is None:
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateMutexW.restype = ctypes.c_void_p
        k32.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_wchar_p]
        k32.CloseHandle.restype = ctypes.c_int
        k32.CloseHandle.argtypes = [ctypes.c_void_p]
        _KERNEL32 = k32
    return _KERNEL32


def _acquire_single_instance() -> bool:
    """用命名互斥体防止重复打开（比用进程列表判断更稳，没有竞态）。

    ``Local\\`` 命名空间按**登录会话**隔离：同一会话内——包括以管理员身份启动的
    实例（UAC 提权不换会话，互斥体跨完整性级别可见）——只允许一份；而多用户 /
    多会话（快速切换用户、RDP 各一条会话）各能跑一份。这是本产品的预期语义，
    若将来要求整机唯一，换成 ``Global\\``。

    审计 F6：``ERROR_ALREADY_EXISTS`` 不再直接阻断——先确认持有者是活着的
    DirMover（见 :func:`_mutex_preempted`）。正常的「重复双击 → 提示已运行」
    行为不受影响：那个场景里持有者就是本程序的另一个实例。
    """
    global _SINGLE_INSTANCE_HANDLE
    try:
        handle = _kernel32().CreateMutexW(None, 0, _MUTEX_NAME)
    except Exception:
        # 非 Windows / 无法调用 kernel32：不做单实例限制，不阻断启动
        return True
    if not handle:
        return True
    if ctypes.get_last_error() == _ERROR_ALREADY_EXISTS:
        if _mutex_preempted():
            # 被抢占：照样启动，并把持有者信息刷新成自己
            _SINGLE_INSTANCE_HANDLE = handle
            _write_mutex_owner(os.getpid())
            return True
        return False
    _write_mutex_owner(os.getpid())
    _SINGLE_INSTANCE_HANDLE = handle  # 持有引用，进程退出才释放
    return True


def _release_single_instance() -> None:
    """主动释放单实例互斥体（提权重启前调用，审计 C5）。

    不释放的话，提权后的子进程可能先跑到 :func:`_acquire_single_instance`，看到
    一个"已有实例"的互斥体 → 弹"程序已在运行。请勿重复打开。"后退出；而父进程
    随后也收工退出，用户看到主程序整块消失、什么都没有起来。释放后由子进程重新持有。

    审计 F6：持有者 PID 文件必须**一并清掉**——否则子进程即便拿到互斥体，也会
    读到「父进程 PID 仍存活且镜像名相同」而判定"已在运行"，把 C5 修好的提权
    重启又堵回去。
    """
    global _SINGLE_INSTANCE_HANDLE
    if not _SINGLE_INSTANCE_HANDLE:
        return
    _clear_mutex_owner()
    handle, _SINGLE_INSTANCE_HANDLE = _SINGLE_INSTANCE_HANDLE, None
    try:
        _kernel32().CloseHandle(handle)
    except Exception:
        pass   # 释放失败只会退化成"可能多弹一次已在运行"，不值得抛


def _app_icon_path() -> Path:
    """应用图标路径（与 MainWindow._icon_path 同源：frozen 用 _MEIPASS，开发用仓库根）。"""
    if getattr(sys, "frozen", False):
        base = Path(getattr(sys, "_MEIPASS", Path(sys.executable).parent))
    else:
        base = Path(__file__).resolve().parent
    return base / "assets" / "app.ico"


def _notify_already_running() -> int:
    """第二个实例的提示。

    不用原生 ``messagebox``：它需要一个已映射（realize 过）的 owner 窗口才能
    正常绘制，而这里的根窗口是刚建出来、还没进过事件循环的——原生模态框会
    整块空白（用户报告的"弹窗显示空白"）。改用与主界面同一套自绘弹窗：
    它会自己 ``update_idletasks`` 并跑嵌套事件循环，渲染稳定。
    根窗口先隐藏，避免闪一个空的 Tk 窗口；此时自绘弹窗自动按屏幕居中。

    主题必须在这里手动安装（``Theme(root)``）：应用内弹窗的主题是
    ``MainWindow`` 构造时装到 root 上的，本函数没有主界面，不装就会
    回退到 Tk 默认字体/按钮样式，与应用内其他弹窗风格不一致。
    """
    import tkinter as tk

    from docmover.ui import dialogs
    from docmover.ui.theme import Theme

    root = tk.Tk()
    root.withdraw()
    try:
        Theme(root)
        try:
            root.iconbitmap(default=str(_app_icon_path()))
        except (tk.TclError, OSError):
            pass
        # 图标语义（用户反馈，第十二批）：show_warning 的棕「!」用于「操作被拦下」，
        # 与 main_window 里「无法开始」「恢复受阻」同类——拒绝第二个实例正属此类。
        # 之前误用 show_info（蓝色「i」= 纯提示），与其它弹窗的警示色不一致。
        dialogs.show_warning(root, APP_TITLE, "程序已在运行。请勿重复打开。")
    except Exception:
        # 自绘弹窗本身失败时退回原生提示："已运行"这个信息必须送达
        try:
            from tkinter import messagebox

            messagebox.showwarning(APP_TITLE, "程序已在运行。")
        except Exception:
            pass
    finally:
        root.destroy()
    return 0


# ------------------------------------------------------------------ 环境

def _check_environment() -> str:
    """返回阻断性错误说明；空字符串表示可以继续。"""
    if sys.platform != "win32":
        return "本工具仅支持 Windows 系统。"

    version = sys.getwindowsversion()
    if (version.major, version.minor) < (MIN_SUPPORTED_MAJOR, MIN_SUPPORTED_MINOR):
        return "仅支持 Windows 7 及以上系统。"

    return ""


# ------------------------------------------------------------------ CLI

def _cmd_scan() -> int:
    """只读列出全部项目的位置与状态。不修改任何东西。

    审计 P3-1：原先的 ``verbose`` 入参从未被使用，已删除（无任何调用方传入）。
    """
    store = RegistryStore()
    engine = TransferEngine(store=store, dry_run=True)

    caption, _major = shell.detect_windows_version()
    _out(f"{APP_TITLE} v{APP_VERSION} —— 环境扫描（只读）")
    _out(f"系统：{caption}")
    _out(f"权限：{'管理员' if shell.is_admin() else '普通用户'}")
    _out()

    header = f"{_pad('文件夹', 14)}{_pad('当前位置', 48)}状态"
    _out(header)
    _out("-" * 108)

    read_errors: List[str] = []
    for spec in FOLDERS:
        current = engine.resolve_current(spec)
        error = store.consume_read_error()
        if error:
            read_errors.append(f"{spec.display}：{error}")
        exists = "存在" if os.path.isdir(current) else "不存在"
        risk = "" if spec.risk == "normal" else f"（{spec.risk_label}）"
        _out(f"{_pad(spec.display, 14)}{_pad(current, 48)}{exists}{risk}")

    _out()
    _out("固定磁盘：")
    for drive in fsops.list_fixed_drives():
        _out(f"  {drive.label()}")

    stored = store.enum_values(USER_SHELL_FOLDERS_SUBKEY)
    enum_error = store.consume_read_error()
    _out(f"\nHKCU User Shell Folders 共 {len(stored)} 个值。")
    if enum_error:
        # 审计 问题#3：枚举失败不再让 --scan 中途中断，但必须显式可见，
        # 而不是给一个「看起来正常」的空结果。
        _out(f"[警告] 枚举 User Shell Folders 失败，以下计数可能不完整：{enum_error}")

    if read_errors:
        # 审计 P2-3：注册表读取异常会静默回退，这里显式提示，避免用户被误导
        _out(f"\n[警告] 有 {len(read_errors)} 项注册表位置读取异常，已回退到系统默认：")
        for item in read_errors:
            _out(f"  {item}")

    pending = IntentJournal.pending(app_dir() / BACKUP_DIRNAME)
    # 审计 B1：``pending()`` 把「日志损坏/不可读」这类**非空**失败写进了
    # ``_JOURNAL_READ_ERROR``，但它此前在生产里**无人消费**——日志只写了一半
    # （进程被硬杀）时 ``pending()`` 返回 ``[]``，用户与日志都收不到「上次可能
    # 有半完成项」这唯一的收尾信号。这里显式取走并提示。
    journal_error = IntentJournal.consume_read_error()
    if journal_error:
        _out(f"\n[警告] 上次事务日志不可用：{journal_error}")
        _out("        无法据此判断是否有未完成项，请在界面上留意各文件夹当前位置。")
    if pending:
        _out(f"\n注意：上次转移有 {len(pending)} 项未正常收尾：")
        for entry in pending:
            _out(f"  {entry['display']}（{entry['stage']}）→ {entry['target']}")
        _out("启动图形界面时会提示如何收尾。")
    return 0


def _cmd_dry_run(keys=None) -> int:
    """完整规划但不执行任何改动。

    审计 P3-1：``keys`` 为**预留**入参——CLI 路径不可达（``main`` 恒以默认全选
    调用），保留供测试或未来的子命令直接注入要试运行的项目集合。
    """
    config = AppConfig()
    store = RegistryStore()
    engine = TransferEngine(store=store, dry_run=True)

    keys = keys or config.selected or [spec.key for spec in FOLDERS]
    destination = config.destination
    if not destination:
        for drive in fsops.list_fixed_drives():
            if drive.letter.upper() != fsops.system_drive():
                destination = default_target_root(drive.letter)
                break

    _out(f"{APP_TITLE} —— 试运行（不会做任何改动）")
    _out(f"目标根目录：{destination or '（未设置）'}")
    _out()

    plan = engine.build_plan(keys, destination, config.create_user_dir, scan=True)
    for item in plan:
        _out(f"  {item.display}")
        _out(f"    当前位置：{item.current}")
        _out(f"    转移目标：{item.target}")
        if item.same:
            verdict = "无需转移（位置相同）"
        else:
            reason = item.blocking_reason()
            verdict = f"待转移 {item.stat.describe()}" if not reason else f"受阻：{reason}"
        _out(f"    判定：{verdict}")
        _out()

    report = engine.execute(plan)
    _out("试运行结论：" + report.summary())
    for outcome in report.outcomes:
        _out(f"  {outcome.label} {outcome.display}：{outcome.message}")
    return 0


# ------------------------------------------------------------------ GUI

def _cmd_gui(dry_run: bool = False) -> int:
    import traceback

    import tkinter as tk

    from docmover.ui.main_window import MainWindow

    config = AppConfig()
    logger = logging_setup.setup_logging(app_dir() / LOG_DIRNAME)

    # 审计 P3-2：环境检查只执行一次并复用结果（原写法在 if 与 error 里各调一次）
    env_error = _check_environment()
    if env_error:
        logger.error(env_error)
        return 2

    # DPI 感知必须在创建任何窗口之前声明，否则整个窗口位图被系统拉伸，
    # 文字与 1px 描边全部发虚（Theme 也依赖它计算缩放系数）。
    _enable_dpi_awareness()

    # 单实例判定放在建主窗口之前（审计 P2-17）：否则第二个实例会先建窗口、
    # 读注册表、写日志，然后才被判定为重复而退出。
    if not dry_run and not _acquire_single_instance():
        logger.info("检测到已有实例在运行，本次启动退出。")
        return _notify_already_running()

    root = tk.Tk()
    try:
        try:
            # 释放单实例的动作注入给界面（审计 C5）：互斥体句柄在本模块里，
            # 界面若直接 ``import run`` 会把脚本当**第二个模块**再导入一次
            # （``__main__`` 与 ``run`` 是两个模块对象），释放动作等于空转。
            MainWindow(
                root, config, logger=logger, dry_run=dry_run,
                on_before_relaunch=_release_single_instance,
            )
        except Exception:
            # 窗口化 exe 没有控制台，崩溃默认无声无息。把 traceback 落到
            # 日志目录，并弹窗告知（而不是留下一个空白 tk 窗口）。
            detail = traceback.format_exc()
            logger.error("界面初始化失败：\n%s", detail)
            crash_file = None
            try:
                crash_file = app_dir() / LOG_DIRNAME / "startup_error.txt"
                crash_file.parent.mkdir(parents=True, exist_ok=True)
                crash_file.write_text(detail, encoding="utf-8")
            except OSError:
                pass
            from docmover.ui import dialogs

            hint = (f"界面初始化失败，详情见日志文件：\n{crash_file}"
                    if crash_file is not None else
                    f"界面初始化失败：\n{detail[-800:]}")
            # 同样走自绘弹窗（原生 MessageBox 在 owner 未就绪时会空白）；
            # 自绘也失败时退回原生，保证崩溃信息一定能被看到。
            try:
                dialogs.show_error(root, APP_TITLE, hint)
            except Exception:
                from tkinter import messagebox

                messagebox.showerror(APP_TITLE, hint)
            root.destroy()
            return 3

        root.mainloop()
    finally:
        # 审计 C1：**正常退出**（以及界面初始化失败退出）时释放单实例并清掉 PID
        # 文件。此前只有提权重启路径（``on_before_relaunch=_release_single_instance``）
        # 会清理，正常关窗会把 ``single_instance.pid`` 留在
        # ``%LOCALAPPDATA%\DirMover\`` 里。该函数对「未持有句柄」是无操作——提权重启
        # 时父进程已先释放（句柄置 None），故走到这里不会误删子进程刚写入的 PID，
        # 也不会误伤「本轮未取得互斥体」的路径（互斥体在更早处已判定退出）。
        _release_single_instance()
    return 0


# ------------------------------------------------------------------ 主入口

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog=APP_NAME,
        description=f"{APP_TITLE} v{APP_VERSION}",
        add_help=True,
    )
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--scan", action="store_true",
                       help="只读列出各文件夹当前位置与状态，不做任何修改")
    group.add_argument("--dry-run", action="store_true",
                       help="完整规划但不执行任何改动（不启动界面）")
    group.add_argument("--gui", action="store_true",
                       help="强制启动图形界面（默认行为）")
    parser.add_argument("--version", action="version",
                        version=f"{APP_NAME} {APP_VERSION}")
    return parser


def main(argv=None) -> int:
    """顶层入口：任何未预期异常都落盘日志并返回非零退出码（审计 P2-2）。

    退出码约定：0 正常 / 2 环境不满足 / 3 界面初始化失败 / **4 未预期错误**。
    ``--help`` / ``--version`` / 参数错误由 argparse 抛 ``SystemExit``，原样放行。
    """
    global _CONSOLE_LOG_OK
    try:
        _force_utf8_console()
        # 输出缓冲与落盘状态按"每次运行"重置：同一进程内可能被反复调用
        # （测试、嵌入式宿主），残留的旧缓冲会让下一次运行弹出上一次的内容。
        _console_tail.clear()
        _CONSOLE_LOG_OK = True
        args = build_parser().parse_args(argv)
        if args.scan:
            return _cmd_scan()
        if args.dry_run:
            return _cmd_dry_run()
        return _cmd_gui()
    except SystemExit:
        raise
    except BaseException as exc:   # noqa: BLE001 - 顶层兜底，任何异常都要有落点
        import traceback

        detail = traceback.format_exc()
        _log_console_output(detail)
        _out(f"[FAIL] 未预期错误：{type(exc).__name__}: {exc}")
        if _CONSOLE_LOG_OK:
            _out(f"[FAIL] 详细信息已写入：{_console_log_path()}")
        else:
            _out(f"[FAIL] 日志文件无法写入（可能没有写权限）：{_console_log_path()}")
        return 4
    finally:
        # 收尾时才弹无控制台提示：此刻才有完整的输出可摘（审计 C4）
        _flush_console_notice()


if __name__ == "__main__":
    sys.exit(main())
