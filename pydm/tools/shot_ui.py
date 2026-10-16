"""开发工具：抓取主界面 + 主题化弹窗的截图，供 UI 视觉验收前后对比。

用法：
    <python> tools/shot_ui.py <前缀>
生成 <前缀>_main.png / <前缀>_dialog_info.png / <前缀>_dialog_warning.png
输出到 tools/shots/ 下。不属于交付产物。
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
OUT = HERE / "shots"


def enable_dpi() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def grab_window(path: Path) -> bool:
    from PIL import ImageGrab

    # 抓整个虚拟屏，按窗口矩形裁剪（Toplevel 弹窗没有独立 HWND 树可靠获取）
    image = ImageGrab.grab(all_screens=True)
    out = OUT / path.name
    out.parent.mkdir(parents=True, exist_ok=True)
    image.save(out)
    print(f"[OK] {out}  {image.size[0]}x{image.size[1]}")
    return True


def grab_bbox(box, path: Path) -> bool:
    from PIL import ImageGrab

    image = ImageGrab.grab(bbox=box, all_screens=True)
    out = OUT / path.name
    out.parent.mkdir(parents=True, exist_ok=True)
    image.save(out)
    print(f"[OK] {out}  {image.size[0]}x{image.size[1]}")
    return True


def window_bbox(hwnd: int):
    rect = wt.RECT()
    ctypes.windll.user32.GetWindowRect(wt.HWND(hwnd), ctypes.byref(rect))
    return (rect.left, rect.top, rect.right, rect.bottom)


def main() -> int:
    prefix = sys.argv[1] if len(sys.argv) > 1 else "shot"
    enable_dpi()

    import tkinter as tk

    from docmover.config import AppConfig
    from docmover.ui import dialogs
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
    time.sleep(0.7)
    root.update()

    hwnd = root.winfo_id()
    parent = ctypes.windll.user32.GetParent(wt.HWND(hwnd))
    target = parent or hwnd
    box = window_bbox(target)
    grab_bbox(box, Path(f"{prefix}_main.png"))

    # ---- 弹窗：先构建、注入后自动截图再关闭
    def capture_dialog(kind: str, title: str, message: str, buttons, name: str):
        dlg = dialogs.ModalDialog(root, title, message, kind=kind, buttons=buttons,
                                  cancel_value=False)

        def shoot_and_close():
            root.update_idletasks()
            d = window_bbox(dlg.tl.winfo_id())
            grab_bbox(d, Path(f"{prefix}_{name}.png"))
            dlg._finish(None)

        root.after(500, shoot_and_close)
        dlg.show()

    yes_no = (("是", "primary", True), ("否", "normal", False))
    capture_dialog("info", "无需转移",
                   "所选项目当前位置与目标位置相同，无需转移。\n\n"
                   "如需调整，请修改「目标根目录」。",
                   (("确定", "primary", None),), "dialog_info")
    capture_dialog("warning", "确认开始转移",
                   "即将转移 5 项，合计 58.2 GB / 128,430 个文件\n"
                   "目标根目录：D:\\UserData\n\n"
                   "本次将转移的项目已在列表中用浅蓝底色标出，"
                   "可逐行核对「当前位置 → 转移目标」。\n\n"
                   "先复制并校验，通过后才删除源目录；任一步失败都会自动回滚。",
                   yes_no, "dialog_warning")
    capture_dialog("error", "执行出错",
                   "发生未预期错误：\n\n"
                   "PermissionError: [WinError 5] 拒绝访问。\n\n"
                   "详细过程见日志目录：\n"
                   "D:\\UserData\\...\\logs", (("确定", "primary", None),), "dialog_error")
    capture_dialog("success", "操作完成",
                   "完成 7 项\n\n"
                   "完成｜我的文档：复制校验通过，源目录已删除\n"
                   "完成｜桌面：复制校验通过，源目录已删除",
                   (("确定", "primary", None),), "dialog_success")

    root.destroy()
    return 0


if __name__ == "__main__":
    sys.exit(main())
