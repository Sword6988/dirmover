"""日志初始化：把过程详情写入程序目录下的 ``logs\\`` 文件。

界面不再提供日志查看区域（2026-10 按需求移除日志抽屉），因此这里只保留
文件与 stderr 两个处理器，供出问题时事后排查。
"""

from __future__ import annotations

import logging
import sys
from datetime import datetime
from pathlib import Path
from typing import Optional

from .constants import APP_NAME


_CONFIGURED = False
_FILE_HANDLER: Optional[logging.FileHandler] = None
# 最近一次配置后是否存在可用的**文件**处理器。默认 False（尚未配置 = 没有文件日志）。
# 存在的意义（审计 C4）：exe 放在 ``C:\Program Files`` 下时 ``app_dir()/logs`` 建不出来，
# 而程序随后会告诉用户"完整结果已写入日志文件：<路径>"——排障指引指向空处。
# 调用方据此改口径（把结果直接贴出来），而不是无条件断言落盘成功。
_FILE_LOGGING_OK = False


def file_logging_available() -> bool:
    """最近一次 :func:`setup_logging` 之后，是否存在可用的文件日志处理器。"""
    return _FILE_LOGGING_OK


def setup_logging(
    log_dir: Optional[Path] = None,
    level: int = logging.INFO,
    app_name: str = APP_NAME,
) -> logging.Logger:
    """配置根日志器。

    审计 P2-22：原实现用 ``_CONFIGURED`` 一次性短路，**首次之后的调用完全不生效**
    （既不改目录也不加处理器）。这使测试无法把日志重定向到临时目录，会污染仓库
    ``logs/``。现在文件处理器**每次调用都指向最新的 ``log_dir``**（先移除并关闭
    旧句柄），既保持生产「重复调用无副作用」，又让测试可安全注入临时目录。

    审计 C4：文件处理器没建起来时**不再静默**——把结果记进
    :func:`file_logging_available`，让调用方（``run.py`` 的无控制台提示、
    界面上的"打开日志目录"）能改口径，不再声称"已写入日志文件"。
    """
    global _CONFIGURED, _FILE_HANDLER, _FILE_LOGGING_OK

    logger = logging.getLogger(app_name)
    logger.setLevel(level)
    logger.propagate = False

    formatter = logging.Formatter(
        "%(asctime)s [%(levelname)s] %(message)s", datefmt="%Y-%m-%d %H:%M:%S"
    )

    if log_dir is not None:
        new_handler: Optional[logging.FileHandler] = None
        try:
            log_dir = Path(log_dir)
            log_dir.mkdir(parents=True, exist_ok=True)
            file_name = f"{app_name}-{datetime.now():%Y%m%d}.log"
            new_handler = logging.FileHandler(log_dir / file_name, encoding="utf-8")
            new_handler.setFormatter(formatter)
        except OSError:
            new_handler = None
        if new_handler is not None:
            if _FILE_HANDLER is not None:
                logger.removeHandler(_FILE_HANDLER)
                try:
                    _FILE_HANDLER.close()
                except Exception:
                    pass
            logger.addHandler(new_handler)
            _FILE_HANDLER = new_handler
        # 以"当前是否挂着文件处理器"为准：本次目录切换失败但旧处理器仍在时，
        # 文件日志其实是可用的，不该误报为不可用。
        _FILE_LOGGING_OK = _FILE_HANDLER is not None

    if not _CONFIGURED and sys.stderr is not None:
        stream = logging.StreamHandler(sys.stderr)
        stream.setFormatter(formatter)
        logger.addHandler(stream)

    _CONFIGURED = True
    return logger


def get_logger(app_name: str = APP_NAME) -> logging.Logger:
    return logging.getLogger(app_name)
