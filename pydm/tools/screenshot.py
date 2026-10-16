"""开发工具：构建主界面窗口并截图，用于人工验收界面效果。

同进程构建窗口，因此不受窗口焦点影响。不属于交付产物。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent
sys.path.insert(0, str(PROJECT))


def enable_dpi() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def grab(hwnd: int, out_path: Path) -> bool:
    from PIL import ImageGrab

    rect = wt.RECT()
    ctypes.windll.user32.GetWindowRect(wt.HWND(hwnd), ctypes.byref(rect))
    box = (rect.left, rect.top, rect.right, rect.bottom)
    if box[2] - box[0] <= 0 or box[3] - box[1] <= 0:
        print(f"[FAIL] 窗口尺寸异常 {box}")
        return False
    image = ImageGrab.grab(bbox=box, all_screens=True)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    image.save(out_path)
    print(f"[OK] {out_path.name}  {image.size[0]}x{image.size[1]}")
    return True


def main() -> int:
    enable_dpi()

    import tkinter as tk

    from docmover.config import AppConfig
    from docmover.ui.main_window import MainWindow

    root = tk.Tk()
    config = AppConfig()
    config.normalize()
    window = MainWindow(root, config, dry_run=True)

    root.update()
    root.deiconify()
    root.lift()
    root.attributes("-topmost", True)
    root.update()
    time.sleep(0.8)
    root.update()

    hwnd = root.winfo_id()
    # 顶层窗口：winfo_id 给出的是内层窗口，取父级才是真正带标题栏的窗口
    parent = ctypes.windll.user32.GetParent(wt.HWND(hwnd))
    target = parent or hwnd
    print(f"[OK] 窗口句柄 {target}（内层 {hwnd}）")

    # 主界面（单屏，无页签、无日志抽屉）
    root.update()
    time.sleep(0.45)
    root.update()
    grab(target, HERE / "screenshot_main.png")

    root.destroy()
    return 0


if __name__ == "__main__":
    sys.exit(main())
