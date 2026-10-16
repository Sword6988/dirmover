"""应用设置（仅内存态，不做持久化）。

每次启动都使用内置默认值：默认全选、目标根目录 ``D:\\UserData``、
默认创建当前用户目录。程序不再读写任何配置文件。
"""

from __future__ import annotations

import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import List

from .constants import DEFAULT_TARGET_LEAF, FOLDER_KEYS


def app_dir() -> Path:
    """程序所在目录：打包后是 exe 所在目录，开发时是项目根目录。"""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


@dataclass
class AppConfig:
    """运行时设置（仅内存，不落盘）。"""

    # 目标位置：默认 D:\UserData；该盘不存在时由界面回落到首个可用盘
    destination: str = f"D:\\{DEFAULT_TARGET_LEAF}"
    create_user_dir: bool = True

    # 勾选项：默认全选
    selected: List[str] = field(default_factory=lambda: list(FOLDER_KEYS))

    def normalize(self) -> "AppConfig":
        """把外部传入的脏数据收敛到合法范围，就地修改并返回自身。"""
        if not isinstance(self.destination, str):
            # 非法值置空，交给界面回落到首个可用磁盘
            self.destination = ""

        if isinstance(self.selected, str):
            self.selected = [self.selected]
        if not isinstance(self.selected, list):
            self.selected = []
        # 只保留已知 key，去重且保持 FOLDERS 顺序
        wanted = set(str(k) for k in self.selected)
        self.selected = [k for k in FOLDER_KEYS if k in wanted]

        self.create_user_dir = bool(self.create_user_dir)
        return self


def default_target_root(drive: str) -> str:
    """按既定语义构造目标根目录：固定为 ``<盘>:\\UserData``。

    用户名不参与目标根目录——是否在中间加一层
    ``<用户名>`` 由「创建当前用户目录」勾选逐项控制。
    """
    return f"{drive.rstrip(':').upper()}:\\{DEFAULT_TARGET_LEAF}"
