"""静态定义：应用元信息、用户文件夹规格、注册表位置。

本模块不导入任何 UI 或注册表实现，可被测试独立导入。
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Tuple

APP_NAME = "DirMover"
APP_TITLE = "用户数据转移工具"
APP_VERSION = "3.0.0"
APP_AUTHOR = "ajian"

LOG_DIRNAME = "logs"
BACKUP_DIRNAME = "backups"

# --------------------------------------------------------------- 注册表位置

EXPLORER_SUBKEY = r"Software\Microsoft\Windows\CurrentVersion\Explorer"
SHELL_FOLDERS_SUBKEY = EXPLORER_SUBKEY + r"\Shell Folders"
USER_SHELL_FOLDERS_SUBKEY = EXPLORER_SUBKEY + r"\User Shell Folders"
ENVIRONMENT_SUBKEY = r"Environment"
WINLOGON_SUBKEY = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"

# ---------------------------------------------------------- 已知文件夹 GUID
#
# Windows 的资源管理器「位置」属性页在写入 User Shell Folders 时，
# 除了传统值名（Personal / My Pictures / ...）外还会写入一组 GUID 值名。
# 两套必须成对同步，否则会出现「资源管理器显示 A、系统实际认 B」的错乱。

GUID_DOCUMENTS = "{F42EE2D3-909F-4907-8871-4C22FC0BF756}"
GUID_PICTURES = "{0DDD015D-B06C-45D5-8C4C-F59713854639}"
GUID_MUSIC = "{A0C69A99-21C8-4671-8703-7934162FCF1D}"
GUID_VIDEOS = "{35286A68-3C57-41A1-BBB1-0EAE73D76C95}"
GUID_DOWNLOADS = "{374DE290-123F-4565-9164-39C4925E467B}"
GUID_DOWNLOADS_ALT = "{7D83EE9B-2244-4E70-B1F5-5393042AF1E4}"
GUID_DESKTOP = "{754AC886-DF64-4CBA-86B5-F7FBF4FBCEF5}"
GUID_FAVORITES = "{1777F761-68AD-4D8A-87BD-30B759FA33DD}"

# ------------------------------------------------- 空间余量与重启等待（A-7/B-6）
#
# 这一组数字会被**两处以上**复述：核心层的空间闸门（``transfer.py``）、
# 系统集成层（``shell.py`` 的 taskkill 超时与两段等待）、以及**界面文案**
# （``main_window.py`` 的退出提示与注释）。原先它们散落成字面量，改一处就会让
# 界面继续复述旧数字——用户看到的"最长约 20 秒"与实际行为静默脱节。
# 收在这里，任何一处需要数字都必须 import 本组常量。
#
# 其中 ``SPACE_MARGIN_*`` 的核心消费者是 ``transfer.py`` 的空间闸门（本模块
# 不引用它，只负责定义）。

SPACE_MARGIN_RATIO = 1.05
SPACE_MARGIN_BYTES = 64 * 1024 * 1024

# 重启资源管理器的耗时参数（``shell.restart_explorer``）：
#   RESTART_EXPLORER_TIMEOUT  taskkill 的 subprocess 超时（秒）
#   RESTART_TERM_WAIT         结束 explorer 后的静置等待（秒）
#   RESTART_RELAUNCH_WAIT     重新拉起 explorer 后的存活确认等待（秒）
RESTART_EXPLORER_TIMEOUT = 20
RESTART_TERM_WAIT = 1.5
RESTART_RELAUNCH_WAIT = 1.0

# 风险等级
RISK_NORMAL = "normal"
RISK_CAUTION = "caution"
RISK_RISKY = "risky"

RISK_LABELS = {
    RISK_NORMAL: "常规",
    RISK_CAUTION: "需注意",
    RISK_RISKY: "高风险",
}


@dataclass(frozen=True)
class FolderSpec:
    """一个可转移的用户文件夹。

    key         稳定标识，用于配置文件与代码引用
    display     界面显示名
    section     ``explorer`` 表示 Explorer 用户文件夹；``environment`` 表示环境变量
    value_name  权威注册表值名
    default     系统出厂默认路径（可能含 ``%USERPROFILE%`` 等环境变量）
    aliases     需要一并同步的其它候选值名（GUID 等）。
                写入时只更新「注册表中已存在」的别名，不会凭空创建。
    risk        风险等级，用于界面提示
    note        界面提示文案

    注：本项目不支持 Win7，故不保留 ``legacy_default``（早期版本用于在 Win7+
    删除 ``My Pictures`` 之类旧别名键）。死字段已移除。
    """

    key: str
    display: str
    section: str
    value_name: str
    default: str
    aliases: Tuple[str, ...] = ()
    risk: str = RISK_NORMAL
    note: str = ""

    @property
    def is_environment(self) -> bool:
        """环境变量项（TEMP/TMP）走 HKCU\\Environment，且不写 Shell Folders。

        审计 P3-1：当前 ``FOLDERS`` 中**没有** ``section="environment"`` 的项，
        故该分支暂时恒为 False。这里**保留**属性和相关分支作为合法能力——
        将来新增 TEMP/TMP 项即自动启用，不属可删的死代码。
        """
        return self.section == "environment"

    @property
    def write_subkey(self) -> str:
        """该项目的权威注册表子键（相对 HKCU）。"""
        return ENVIRONMENT_SUBKEY if self.is_environment else USER_SHELL_FOLDERS_SUBKEY

    @property
    def risk_label(self) -> str:
        return RISK_LABELS.get(self.risk, self.risk)


FOLDERS: Tuple[FolderSpec, ...] = (
    FolderSpec(
        key="documents",
        display="我的文档",
        section="explorer",
        value_name="Personal",
        default=r"%USERPROFILE%\Documents",
        aliases=(GUID_DOCUMENTS,),
    ),
    FolderSpec(
        key="favorites",
        display="收藏夹",
        section="explorer",
        value_name="Favorites",
        default=r"%USERPROFILE%\Favorites",
        aliases=(GUID_FAVORITES,),
    ),
    FolderSpec(
        key="pictures",
        display="我的图片",
        section="explorer",
        value_name="My Pictures",
        default=r"%USERPROFILE%\Pictures",
        aliases=(GUID_PICTURES,),
    ),
    FolderSpec(
        key="music",
        display="我的音乐",
        section="explorer",
        value_name="My Music",
        default=r"%USERPROFILE%\Music",
        aliases=(GUID_MUSIC,),
    ),
    FolderSpec(
        key="videos",
        display="我的视频",
        section="explorer",
        value_name="My Video",
        default=r"%USERPROFILE%\Videos",
        aliases=(GUID_VIDEOS,),
    ),
    FolderSpec(
        key="downloads",
        display="下载目录",
        section="explorer",
        value_name=GUID_DOWNLOADS,
        default=r"%USERPROFILE%\Downloads",
        aliases=(GUID_DOWNLOADS_ALT,),
    ),
    FolderSpec(
        key="desktop",
        display="桌面",
        section="explorer",
        value_name="Desktop",
        default=r"%USERPROFILE%\Desktop",
        aliases=(GUID_DESKTOP,),
    ),
)

FOLDERS_BY_KEY = {spec.key: spec for spec in FOLDERS}
FOLDER_KEYS = tuple(spec.key for spec in FOLDERS)

# 默认目标根目录名
DEFAULT_TARGET_LEAF = "UserData"

# 需要排除的非法目标路径（避免把用户文档搬进系统目录）。
#
# 审计 A4：原先写死 ``C:``，在系统盘不是 C: 的机器（改过盘符 / 精简装机）上整道
# 守卫会失效。改为按 ``SystemDrive`` 动态构造；常量名与元素构成保持不变，
# 以免影响现有 import 与测试。
#
# 审计 A1：这份名单**只在这里定义一次**（相对系统盘根的名字），目标禁区与源禁区
# （``transfer._unsafe_source_tree``）都从下面的函数派生。旧实现让目标侧动态化、
# 源侧却仍写死 ``C:\Windows`` 等，于是系统盘非 C: 的机器上源侧整道闸门 fail-open。
# 现在两处同源同口径，且每次判定都按**当前**系统盘现算（便于测试 monkeypatch）。
_SYSTEM_DRIVE = os.environ.get("SystemDrive", "C:").rstrip("\\")

# 系统盘下需要「拉黑整棵子树」的敏感目录（相对系统盘根的名字）。
SYSTEM_SENSITIVE_TREES: Tuple[str, ...] = (
    "Windows",
    "Program Files",
    "Program Files (x86)",
    "ProgramData",
    "PerfLogs",
    "Recovery",
)
# 仅做**精确匹配**的敏感目录：用户把自定义资料目录放在系统盘 ``\Users\`` 之下是
# 合法状态（例如 ``C:\Users\me\MyDocs``），故只拦「源本身就是 ``\Users``」这一种，
# 不对 ``\Users`` 做 ``is_within``（审计 R3 / A1）。
SYSTEM_SENSITIVE_EXACT_ONLY: Tuple[str, ...] = ("Users",)


def _on_system_drive(name: str) -> str:
    """把相对系统盘根的名字拼成绝对路径（每次按**当前** ``_SYSTEM_DRIVE`` 现算）。

    用函数而非模块级常量：测试可用 ``mock.patch.object(constants, "_SYSTEM_DRIVE",
    "D:")`` 模拟「系统盘非 C:」，调用方（含 ``transfer`` 的源禁区判定）随即生效
    （审计 A1）。
    """
    return _SYSTEM_DRIVE + "\\" + name


def forbidden_target_roots() -> Tuple[str, ...]:
    """目标禁区：系统盘下全部敏感目录（树 + 精确匹配），语义为 ``is_within``（A4）。"""
    return tuple(
        _on_system_drive(name)
        for name in SYSTEM_SENSITIVE_TREES + SYSTEM_SENSITIVE_EXACT_ONLY
    )


def forbidden_source_trees() -> Tuple[str, ...]:
    """源禁区（子树匹配部分）：系统盘下需整棵拉黑的敏感目录（审计 A1）。"""
    return tuple(_on_system_drive(name) for name in SYSTEM_SENSITIVE_TREES)


def forbidden_source_exact() -> Tuple[str, ...]:
    """源禁区（精确匹配部分）：树 + 仅精确匹配的目录（如 ``\\Users``）（审计 A1）。"""
    return tuple(
        _on_system_drive(name)
        for name in SYSTEM_SENSITIVE_TREES + SYSTEM_SENSITIVE_EXACT_ONLY
    )


# 兼容既有 import：目标禁区仍以常量形式提供（模块导入时按当时的系统盘算好）。
# 构造口径与上面完全一致——``constants`` 是唯一真源。
FORBIDDEN_TARGET_ROOTS = forbidden_target_roots()
