"""系统集成交互：资源管理器重启、环境变更广播、Desktop.ini、管理员权限检测与提权重启。

所有对外部程序的调用都被包装成「可安全跳过」的形式，测试与试运行不会触发。
"""

from __future__ import annotations

import ctypes
import csv
import logging
import os
import subprocess
import sys
import time
from pathlib import Path
from typing import List, Optional, Tuple

from .constants import (
    RESTART_EXPLORER_TIMEOUT,
    RESTART_RELAUNCH_WAIT,
    RESTART_TERM_WAIT,
    WINLOGON_SUBKEY,
)
from .fsops import system32
from .registry import RegistryStore

_log = logging.getLogger(__name__)

HWND_BROADCAST = 0xFFFF
WM_SETTINGCHANGE = 0x001A
WM_WININICHANGE = 0x001A
SMTO_ABORTIFHUNG = 0x0002


# --------------------------------------------------------------- 管理员权限

def is_admin() -> bool:
    """当前进程是否具备管理员权限。"""
    if sys.platform != "win32":
        return False
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def relaunch_as_admin(extra_args: List[str] | None = None) -> Tuple[bool, str]:
    """以管理员身份重新启动自身。返回 ``(是否已触发, 说明)``。"""
    if sys.platform != "win32":
        return False, "非 Windows 平台"
    if is_admin():
        return False, "当前已是管理员权限"

    try:
        if getattr(sys, "frozen", False):
            executable = sys.executable
            params = subprocess.list2cmdline(list(extra_args or []))
        else:
            executable = sys.executable
            script = str(Path(__file__).resolve().parent.parent / "run.py")
            # 用 list2cmdline 转义（审计 P2-15）：手写 join 遇到含空格的参数会散架
            params = subprocess.list2cmdline([script] + list(extra_args or []))

        result = ctypes.windll.shell32.ShellExecuteW(
            None, "runas", executable, params, None, 1
        )
    except Exception as exc:
        return False, f"提权调用失败：{exc}"

    if int(result) <= 32:
        return False, f"提权被拒绝（代码 {result}）"
    return True, "已请求管理员权限，请在 UAC 窗口确认"


# ------------------------------------------------------------- 资源管理器

def is_autorestart_shell_enabled(store: RegistryStore | None = None) -> bool:
    """系统是否启用了 shell 自动重启（决定能否安全结束 explorer）。

    .. important::

       **本函数读 HKLM，不读 HKCU（审计 P2-2 修复）。**

    Microsoft 文档给出的位置是
    ``HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\\AutoRestartShell``
    （``REG_DWORD``，取值 ``0|1``，**默认 1**）。历史上这里读的是 **HKCU** 下的同名值，
    而 HKCU 的 Winlogon 子键里根本没有这个值（实测只有 ``RestartApps`` /
    ``ExcludeProfileDirs`` / ``BuildNumber`` / ``FirstLogon`` / ``ParseAutoexec``），
    于是判定恒为 ``False``、``restart_explorer`` 恒走"已跳过重启"分支、
    ``taskkill`` 那一行在生产里**永远执行不到**——界面问"是否重启"，用户点"是"，
    拿到的却是"已跳过"。这是**读错 hive** 的口径缺陷，不是"刻意保守"。

    三态语义（均为如实反映系统）：

    * 值存在且 ``=1`` → ``True``；
    * 值存在且 ``=0`` → ``False``（管理员显式关掉了自动重启，尊重该设置）；
    * 值**不存在** → ``True``（Windows 默认即为 1，文档口径；缺省≠关闭）；
    * 读取失败（权限 / 句柄 / 损坏）→ ``False``（**未知时不冒险**，fail-closed）。

    为什么"值不存在按启用"是安全的：即便此处判为真，:func:`restart_explorer`
    结束 explorer 之后**仍会**复查进程是否回来，没回来就用 ``ShellExecuteExW``
    （不继承管理员令牌）主动拉起。桌面恢复的真正保险是那道兜底；本函数只是
    "系统自己会不会把它拉回来"这层**前提**判定。

    该开放只读 HKLM：本工具**从不**写 HKLM，``RegistryBackend`` 的写入面依旧
    全部限定在 HKCU。
    """
    backend = (store or RegistryStore()).backend
    try:
        found = backend.read_value_hklm(WINLOGON_SUBKEY, "AutoRestartShell")
    except Exception as exc:  # noqa: BLE001 - 读不到即按未启用处理（见 docstring）
        _log.warning(
            "读取 HKLM\\%s\\AutoRestartShell 失败，按未启用处理：%s",
            WINLOGON_SUBKEY,
            exc,
        )
        return False
    if found is None:
        # 值不存在：Windows 默认行为为 1（MS 文档：Range 0|1, Default 1）
        return True
    value = found[0]
    try:
        return int(value) == 1
    except (TypeError, ValueError):
        return False


def _user_filter() -> Optional[str]:
    """``taskkill`` 的 ``/fi`` 过滤串：只结束**当前用户**会话里的进程。

    审计 F7：``taskkill /f /im explorer.exe`` 不带过滤时会杀掉**所有会话**的
    explorer——包括其它已登录用户 / RDP 会话。交付产物由 PyInstaller
    ``--uac-admin`` 提权，杀伤半径是全机的，必须收窄到本用户。
    取不到用户名（极少数环境）时返回 ``None`` → 调用方不加该过滤。
    """
    user = os.environ.get("USERNAME", "").strip()
    if not user:
        return None
    domain = os.environ.get("USERDOMAIN", "").strip()
    return f"username eq {domain}\\{user}" if domain else f"username eq {user}"


def _relaunch_explorer() -> bool:
    """重新拉起 explorer：优先 ShellExecute（不继承管理员令牌），失败降级 Popen。

    审计 F7：交付产物必然已提权为管理员。``subprocess.Popen`` 起的 explorer 会
    **继承提升后的令牌**（完整性级别 High），此后用户从该 explorer 启动的程序
    也全部带管理员权限——这是权限扩散。``ShellExecute`` 走 shell，按当前登录
    用户的**未提权**令牌拉起，是唯一正确的做法。
    """
    if _relaunch_explorer_via_shell():
        return True
    # 降级路径（ShellExecute 不可用时保留原有行为）
    try:
        subprocess.Popen(
            [system32("explorer.exe")],
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        return True
    except OSError as exc:
        _log.warning("无法拉起 explorer.exe：%s", exc)
        return False


class _ShellExecuteInfo(ctypes.Structure):
    """``SHELLEXECUTEINFOW``（仅用于 :func:`_relaunch_explorer_via_shell`）。"""

    _fields_ = [
        ("cbSize", ctypes.c_ulong),
        ("fMask", ctypes.c_ulong),
        ("hwnd", ctypes.c_void_p),
        ("lpVerb", ctypes.c_wchar_p),
        ("lpFile", ctypes.c_wchar_p),
        ("lpParameters", ctypes.c_wchar_p),
        ("lpDirectory", ctypes.c_wchar_p),
        ("nShow", ctypes.c_int),
        ("hInstApp", ctypes.c_void_p),
        ("lpIDList", ctypes.c_void_p),
        ("lpClass", ctypes.c_wchar_p),
        ("hKeyClass", ctypes.c_void_p),
        ("dwHotKey", ctypes.c_ulong),
        ("hIcon", ctypes.c_void_p),
        ("hProcess", ctypes.c_void_p),
    ]


def _relaunch_explorer_via_shell() -> bool:
    """用 ``ShellExecuteExW`` 拉起 explorer，失败返回 ``False`` 由调用方降级。"""
    if sys.platform != "win32":
        return False
    try:
        info = _ShellExecuteInfo()
        info.cbSize = ctypes.sizeof(_ShellExecuteInfo)
        info.lpFile = system32("explorer.exe")
        info.nShow = 1   # SW_SHOWNORMAL
        ok = ctypes.windll.shell32.ShellExecuteExW(ctypes.byref(info))
        return bool(ok)
    except Exception as exc:   # noqa: BLE001 - 拿不到 shell32 就走降级路径
        _log.warning("ShellExecuteExW 拉起 explorer 失败：%s", exc)
        return False


def pids_by_image(imagename: str, user_filter: Optional[str] = None) -> List[int]:
    """按镜像名列出进程 PID（``tasklist`` CSV 解析）。

    对外公开是给 ``run.py`` 的单实例校验复用（审计 F6）：那里要判断
    "互斥体持有者是不是一个活着的 DirMover"，与这里同一套解析口径。

    ``user_filter``（审计 P2-2 **伴随修复**）：传入
    ``"username eq DOMAIN\\\\user"`` 时只列**该用户会话**的进程，与
    :func:`restart_explorer` 里 ``taskkill`` 的 ``/fi`` 口径对齐。此前"强杀按用户
    过滤、列举不过滤"，在「别人也登录着（RDP / 快速切换用户）」的机器上，
    "我们自己的 explorer 回来了没有"会被**别人的** explorer 干扰 → 该重新拉起时
    不拉、还误报"已重启"。``None`` = 不筛选（``run.py`` 的单实例校验需要看全机同名
    进程，行为不变）。
    """
    try:
        # 审计 A9：绝对路径，避免「当前目录里的同名 tasklist.exe」被提权执行
        args = [system32("tasklist.exe"), "/fi", f"imagename eq {imagename}"]
        if user_filter:
            args += ["/fi", user_filter]
        args += ["/fo", "csv", "/nh"]
        output = subprocess.run(
            args,
            capture_output=True,
            text=True,
            timeout=15,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return []

    pids: List[int] = []
    # 用 csv 模块解析（审计 P2-16）：手写按 '","' 切分遇到格式变化就会失效
    needle = imagename.lower()
    for row in csv.reader(output.splitlines()):
        if len(row) >= 2 and row[0].strip().lower().endswith(needle):
            try:
                pids.append(int(row[1]))
            except ValueError:
                continue
    return pids


def _explorer_pids(user_filter: Optional[str] = None) -> List[int]:
    """当前用户会话里的 explorer PID（``user_filter`` 见 :func:`pids_by_image`）。"""
    return pids_by_image("explorer.exe", user_filter)


def restart_explorer(
    store: RegistryStore | None = None,
    force: bool = False,
    dry_run: bool = False,
) -> Tuple[bool, str]:
    """安全重启资源管理器。

    只有当系统启用了 ``AutoRestartShell``（或显式 ``force``）时才结束 explorer，
    并在结束后确保它重新拉起，避免出现桌面不恢复的黑屏问题。
    """
    if dry_run:
        return True, "[试运行] 将重启资源管理器"

    if not force and not is_autorestart_shell_enabled(store):
        return False, (
            "系统未启用 shell 自动重启（AutoRestartShell≠1），"
            "已跳过重启以免桌面无法恢复。可注销后重新登录使其生效。"
        )

    # 审计 F7 + P2-2 伴随修复：**强杀与列举用同一个用户过滤**。只处理**本用户**
    # 会话里的 explorer（参数数组形式，不拼字符串）；若只过滤 taskkill 而列举不过滤，
    # 在别人也登录着的机器上，"我们的 explorer 回来了没有"会被别人的 explorer 干扰。
    user_filter = _user_filter()
    before = _explorer_pids(user_filter)
    if not before:
        return False, "未检测到正在运行的资源管理器"

    args = [system32("taskkill.exe"), "/f"]
    if user_filter:
        args += ["/fi", user_filter]
    args += ["/im", "explorer.exe"]

    try:
        completed = subprocess.run(
            args,
            capture_output=True,
            timeout=RESTART_EXPLORER_TIMEOUT,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError) as exc:
        return False, f"结束资源管理器失败：{exc}"

    # 审计 P2-9：原先忽略返回码，无论成败都继续往下走。这里改为显式判定：
    if completed.returncode != 0:
        detail = (completed.stderr or b"").decode(errors="replace").strip()
        _log.warning("taskkill explorer.exe 返回 %s：%s", completed.returncode, detail)
        if _explorer_pids(user_filter):
            return False, f"未能结束资源管理器（taskkill 返回 {completed.returncode}）"
        # 进程本来就不在（返回 128）→ 继续走兜底拉起

    time.sleep(RESTART_TERM_WAIT)

    if not _explorer_pids(user_filter):
        if not _relaunch_explorer():
            return False, "资源管理器未能自动恢复，请手动运行 explorer.exe"
        time.sleep(RESTART_RELAUNCH_WAIT)

    return True, "资源管理器已重启"


def broadcast_environment_change() -> Tuple[bool, str]:
    """通知系统环境变量已变更，使新的 TEMP/TMP 立即生效。

    预留接口 —— 口径同 ``constants.py`` 的 ``FolderSpec.is_environment``：当前
    ``FOLDERS`` 中**没有** ``section="environment"`` 的项，TEMP/TMP 不经本工具
    转移，故本函数**生产无调用点**，目前仅由测试覆盖。保留它是合法能力（将来新增
    TEMP/TMP 项即启用），不是可删的死代码。

    一旦新增 TEMP/TMP 项，**必须**在该项注册表写入成功之后调用本函数：环境变量只在
    进程启动时读取一次，不广播的话已运行的进程仍持有旧 TEMP/TMP，改动要等注销/重启
    才生效。故审计 A10 对返回值的判定（``SendMessageTimeoutW`` 返回 0 = 超时或无顶层
    窗口响应）是**为该接线预留**的——接线之后它才真正决定上报给用户的文案，而不是对
    一段死代码做无效加固。
    """
    if sys.platform != "win32":
        return False, "非 Windows 平台"
    try:
        result = ctypes.c_long(0)
        delivered = ctypes.windll.user32.SendMessageTimeoutW(
            HWND_BROADCAST,
            WM_SETTINGCHANGE,
            0,
            ctypes.c_wchar_p("Environment"),
            SMTO_ABORTIFHUNG,
            3000,
            ctypes.byref(result),
        )
    except Exception as exc:
        return False, f"广播失败：{exc}"
    # 审计 A10：返回值 0 表示超时或没有任何顶层窗口响应。旧实现完全无视返回值，
    # 无论成败都报"已广播环境变更通知"，给用户一个成功假象。
    if not delivered:
        return False, "广播环境变更未确认送达（可能没有窗口响应或已超时）"
    return True, "已广播环境变更通知"


# ------------------------------------------------------------- Desktop.ini

def write_desktop_ini(folder: str, alias: str, icon_spec: str = "") -> Tuple[bool, str]:
    """为目录写入 Desktop.ini，使其在资源管理器中显示自定义名称。

    只有在目录已存在时才写入；内容以 UTF-16 保存（资源管理器的要求），
    并把 **ini 置为 Hidden+System**、**文件夹置为 Read-only**——这是资源管理器
    认可 ``LocalizedResourceName`` 的经典要求（审计 A11：旧实现给文件夹设的是
    SYSTEM 而非 READONLY，自定义名称很可能不生效）。

    注意（接入生产前必须先解决）：本函数目前**只有测试调用、生产无调用点**；
    它会给传入目录添加 Read-only 属性且**没有还原路径**，"恢复系统默认值"也不会
    去掉它。接入前需要先设计属性还原。

    预留接口 —— 口径同 ``broadcast_environment_change``：保留它是合法能力，不是
    可删的死代码。

    这是**设计取舍**，不是"本就不写 Desktop.ini"：早期版本确实会写 Desktop.ini——
    在**目标根目录**（如 ``D:\\UserData`` 那一级）写入 ``LocalizedResourceName``
    （取自界面上的「个性化目录别名」，默认「用户目录」）与 ``IconResource``
    （``%SystemRoot%\\system32\\imageres.dll,169``），并把 ini 置为 Hidden+System，
    效果是让资源管理器把该目录显示成中文别名。**已决策不接线**（属产品取舍，非缺陷），原因有三：

    * 它会给**目标根目录**设 Read-only、给 ini 设 Hidden+System，全部**没有还原路径**
      ——"恢复系统默认值"只回滚注册表与把文件搬回去（``transfer.restore_defaults``
      → ``build_restore_plan`` → ``execute``），全程不碰文件属性、也不删 Desktop.ini。
      一旦接线，磁盘上就会留下**无人记录、无人清理、也没有地方告知用户**的持久副作用；
      接入前必须先设计属性还原（读原属性 → 记录 → 恢复默认/回滚时还原 → 或界面明示）。
    * 写入层级需要产品决策：早期版本固定写目标根那一级，并且**不判断本次是否真的转移过**
      （"未勾选或路径相同"时同样会写）；本工具可选的写入层有三级（目标根 / 用户目录 /
      各文件夹），三者的显示效果与属性波及范围不同，须先定层级再动手。
    * 早期版本该行为与目录生命周期耦合：除写 ini 外还给目标根设 System 属性，并在
      "根下只剩 Desktop.ini、别无他物"时把**整个根目录**一并删掉——只照搬"写三行 ini"
      不等于完整对齐这套副作用。

    不要用本函数解释"文件夹显示英文物理名"：``Documents`` ↔「文档」这一层的本地化由
    Windows 自带的 ``Documents\\desktop.ini``（``LocalizedResourceName=@%SystemRoot%
    \\system32\\shell32.dll,-21770``）承担，它随目录整树复制被带走；``copy_merge``
    不按属性过滤文件（只处理重解析点与稀疏文件），因此早期版本与本工具在这一层行为相同，
    与本函数无关。

    接入前提：先设计属性还原（含"恢复默认"时的清理），并同步 README。
    """
    if not alias:
        return False, "别名为空，跳过"
    if not os.path.isdir(folder):
        return False, f"目录不存在：{folder}"

    ini_path = Path(folder) / "Desktop.ini"
    icon = icon_spec or r"%SystemRoot%\system32\imageres.dll,169"
    body = (
        "[.ShellClassInfo]\r\n"
        f"LocalizedResourceName={alias}\r\n"
        f"IconResource={icon}\r\n"
    )

    try:
        existing = ""
        if ini_path.exists():
            try:
                existing = ini_path.read_text(encoding="utf-16")
            except (UnicodeError, OSError):
                existing = ""
        if alias in existing and icon in existing:
            return True, "Desktop.ini 已是目标内容"

        ini_path.write_text(body, encoding="utf-16")
        _set_attributes(ini_path, hidden=True, system=True)
        _set_attributes(Path(folder), readonly=True)
        return True, "已写入 Desktop.ini"
    except OSError as exc:
        return False, f"写入 Desktop.ini 失败：{exc}"


def _set_attributes(
    path: Path, hidden: bool = False, system: bool = False, readonly: bool = False
) -> None:
    if sys.platform != "win32":
        return
    try:
        attrs = ctypes.windll.kernel32.GetFileAttributesW(str(path))
        if attrs == -1:
            _log.warning("读取文件属性失败，跳过属性设置：%s", path)
            return
        if hidden:
            attrs |= 0x02  # FILE_ATTRIBUTE_HIDDEN
        if system:
            attrs |= 0x04  # FILE_ATTRIBUTE_SYSTEM
        if readonly:
            attrs |= 0x01  # FILE_ATTRIBUTE_READONLY
        ctypes.windll.kernel32.SetFileAttributesW(str(path), attrs)
    except Exception as exc:
        # 审计 P2-10：原先静默 pass，用户会以为属性已生效
        _log.warning("设置文件属性失败 %s：%s", path, exc)


def open_in_explorer(path: str) -> None:
    """在资源管理器中定位到指定路径（列表双击、打开日志目录等入口）。"""
    if not os.path.exists(path):
        return
    try:
        subprocess.Popen([system32("explorer.exe"), os.path.normpath(path)])
    except OSError as exc:
        _log.warning("无法打开资源管理器 %s：%s", path, exc)


def detect_windows_version() -> Tuple[str, float]:
    """返回 ``(说明文本, 主版本号)``。优先使用 RtlGetVersion（不受兼容性清单影响）。"""
    major = sys.getwindowsversion().major
    try:
        class _OsVersionInfo(ctypes.Structure):
            _fields_ = [
                ("dwOSVersionInfoSize", ctypes.c_ulong),
                ("dwMajorVersion", ctypes.c_ulong),
                ("dwMinorVersion", ctypes.c_ulong),
                ("dwBuildNumber", ctypes.c_ulong),
                ("dwPlatformId", ctypes.c_ulong),
                ("szCSDVersion", ctypes.c_wchar * 128),
            ]

        info = _OsVersionInfo()
        info.dwOSVersionInfoSize = ctypes.sizeof(_OsVersionInfo)
        if ctypes.windll.ntdll.RtlGetVersion(ctypes.byref(info)) == 0:
            major = info.dwMajorVersion
            release = _release_name(info.dwMajorVersion, info.dwBuildNumber)
            return (
                f"Windows {release}（内部版本 {info.dwBuildNumber}）",
                float(info.dwMajorVersion),
            )
    except Exception:
        pass
    return f"Windows 内核 {major}", float(major)


def _release_name(major: int, build: int) -> str:
    if major >= 11 or (major == 10 and build >= 22000):
        return "11"
    if major == 10:
        return "10"
    if major == 6:
        return "8.1/7"
    return str(major)
