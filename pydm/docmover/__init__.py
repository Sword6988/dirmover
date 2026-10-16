"""DirMover —— 用户数据转移工具的 Python 实现。

纯标准库（tkinter + winreg），无第三方运行时依赖。
"""

from .constants import APP_AUTHOR, APP_NAME, APP_TITLE, APP_VERSION

__all__ = ["APP_NAME", "APP_TITLE", "APP_VERSION", "APP_AUTHOR"]
__version__ = APP_VERSION
