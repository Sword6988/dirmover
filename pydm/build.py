"""打包脚本：用 PyInstaller 生成单文件 exe。

用法：
    <python> build.py            打包到 ../dist/DirMover.exe
    <python> build.py --keep     保留中间产物（build_work/）便于排查

审计 P2-18 / P2-19 / D2 / D3：本脚本在打包前会
    * **强断言**构建解释器次版本精确等于 ``tools.ci_check.EXPECTED_PYTHON``
      （单一真源；exe 内嵌的就是 ``sys.executable`` 的 CPython，见 D3）；
    * 断言 PyInstaller 已安装，且版本**精确等于** ``requirements-build.txt``
      里的 ``pyinstaller==X.Y.Z`` pin（单一真源，见 D2）；
    * 由**唯一真源** ``docmover.constants.APP_VERSION`` 渲染版本资源文件
      （``build_version.txt`` 仅作模板，不含手写版本号）；
    * 以 ``--noupx`` 打包（UPX 压缩是杀软误报与解包异常的常见来源）；
    * 清理 dist 目录里已知的旧交付物（仅限固定文件名，绝不通配删除）；
    * **打包成功后自动自检**（审计 §5.3-10）：调用 ``tools/verify_release.py``，断言
      产物是 x64/GUI/提权、内嵌期望次版本的 ``python3XX.dll``、图标逐帧内嵌、版本资源与
      ``APP_VERSION`` 一致、且 exe 内 PYZ 关键模块与**当前源码逐字节同源**；任一不符即
      构建失败（退出码非 0），把发布关键属性从「人工核验」升级为「构建断言」。
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import List, Tuple

ROOT = Path(__file__).resolve().parent
WORK = ROOT / "build_work"
DIST = ROOT.parent / "dist"
NAME = "DirMover"

# 保证无论从哪个工作目录调用都能导入 docmover 包
sys.path.insert(0, str(ROOT))

from docmover.constants import APP_VERSION  # noqa: E402
# 审计 D3：**构建**解释器的期望版本取自 tools/ci_check.py::EXPECTED_PYTHON 这一
# 唯一真源（本地门禁、CI 与打包同源），不在本脚本里另写一份「下限」。
from tools.ci_check import EXPECTED_PYTHON  # noqa: E402

# 构建解释器要求（审计 D3）：必须**精确等于** EXPECTED_PYTHON（当前 3.14）。
# 原因：exe 内嵌的就是 sys.executable 的 CPython——用别的次版本打包会产出与发布
# 声明（README「Win10+ / 内嵌 CPython 3.14」）脱节的产物（历史上曾用 3.13 打出内嵌
# ``python313.dll`` 的 exe，与文档声称的 3.14 自相矛盾）。故把原来的 (3, 10)「下限」
# 改为「强断言精确相等」。
#
# 另记**运行时**要求（仅供参考，**不**参与构建断言）：本程序用到
# ``dataclass(slots=True)`` 等特性，源码运行需要 Python >= 3.10；打包则须使用上面的
# EXPECTED_PYTHON（两者不是一回事）。

# dist 目录中已知的历史交付物（更名前的一代产物），只按**固定文件名**清理，
# 不做通配删除——绝不误伤 DirMover.exe 与 dist/logs/。
LEGACY_DIST_FILES: Tuple[str, ...] = ("DocMover.exe",)


def _check_python() -> bool:
    """强断言构建解释器次版本**精确等于** EXPECTED_PYTHON（审计 D3）。

    与旧实现（``sys.version_info[:2] < (3, 10)`` 的「下限」判定）不同：这里要求
    完全一致，任一方向的不符（更低或更高）都拒绝打包，并给出可操作的提示。
    """
    current = sys.version_info[:2]
    expected = f"{EXPECTED_PYTHON[0]}.{EXPECTED_PYTHON[1]}"
    if current != EXPECTED_PYTHON:
        print(
            f"[FAIL] 构建解释器版本不符：期望 {expected}，"
            f"当前为 {sys.version.split()[0]}（{sys.executable}）"
        )
        print(
            "       exe 内嵌的是构建解释器的 CPython，版本不符会打出与发布声明"
            f"（内嵌 CPython {expected}）脱节的产物。"
        )
        print(
            f"       请改用 Python {expected} 运行本脚本——它与 "
            "tools/ci_check.py::EXPECTED_PYTHON 为同一真源。"
        )
        return False
    return True


def _expected_pyinstaller_version() -> str:
    """从 requirements-build.txt 解析 ``pyinstaller==X.Y.Z`` 的 pin（审计 D2）。

    返回去掉 ``==`` 的版本串（如 ``6.22.2``）；未找到 pin 时返回空串。
    只认 ``pyinstaller``（大小写不敏感），忽略空行与注释行——这样「固定版本」的
    真源就只有 requirements-build.txt 一处，不需要在脚本里再手写一遍。
    """
    requirement = ROOT / "requirements-build.txt"
    try:
        text = requirement.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.lower().startswith("pyinstaller=="):
            return line.split("==", 1)[1].strip()
    return ""


def _check_pyinstaller() -> bool:
    """断言 PyInstaller 版本与 requirements-build.txt 的 pin 精确一致（审计 D2）。"""
    try:
        import PyInstaller  # noqa: F401
    except Exception as exc:  # ImportError 及其它导入期异常
        print(f"[FAIL] 未安装 PyInstaller（或无法导入）：{exc}")
        print('       请先执行：python -m pip install -r requirements-build.txt')
        return False
    version = getattr(PyInstaller, "__version__", "")
    if not version:
        print("[FAIL] 无法读取 PyInstaller 版本（PyInstaller.__version__ 缺失）")
        return False
    expected = _expected_pyinstaller_version()
    if not expected:
        print("[FAIL] requirements-build.txt 中未找到 pyinstaller==X.Y.Z 的版本 pin")
        return False
    if version != expected:
        print(
            f"[FAIL] PyInstaller 版本不符：requirements-build.txt 固定为 {expected}，"
            f"当前为 {version}"
        )
        print("       请执行：python -m pip install -r requirements-build.txt")
        return False
    print(f"[OK] PyInstaller {version} / Python {sys.version.split()[0]}")
    return True


def _version_parts(version: str) -> List[int]:
    """把 ``3.0.0`` 规整为 4 段数字 ``[3, 0, 0, 0]``（PyInstaller 版本资源要求）。"""
    parts: List[int] = []
    for chunk in str(version).split("."):
        digits = "".join(ch for ch in chunk if ch.isdigit())
        parts.append(int(digits) if digits else 0)
    while len(parts) < 4:
        parts.append(0)
    return parts[:4]


def _render_version_file(template: Path, out_path: Path, version: str) -> Path:
    """由模板渲染 PyInstaller 版本资源文件（唯一真源 = APP_VERSION）。

    模板中不得出现手写版本数字，只用 ``__APP_VER_TUPLE__`` / ``__APP_VER_4__``
    两个占位符；渲染失败（残留占位符）即中止，避免打出错误版本号。
    """
    parts = _version_parts(version)
    text = template.read_text(encoding="utf-8")
    text = text.replace("__APP_VER_TUPLE__", ", ".join(str(p) for p in parts))
    text = text.replace("__APP_VER_4__", ".".join(str(p) for p in parts))
    if "__APP_VER" in text:
        raise ValueError(
            f"版本资源模板仍有未替换的占位符：{template}"
        )
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(text, encoding="utf-8")
    return out_path


def _clean_legacy_artifacts() -> None:
    """删除 dist 中已知的旧交付物（精确文件名，不递归、不通配）。"""
    removed: List[str] = []
    for name in LEGACY_DIST_FILES:
        target = DIST / name
        try:
            if target.is_file():
                target.unlink()
                removed.append(name)
        except OSError as exc:
            print(f"[warn] 清理旧产物失败 {target}：{exc}")
    if removed:
        print("[OK] 已清理 dist 中的旧交付物：" + "、".join(removed))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--keep", action="store_true", help="保留 build_work 中间产物")
    args = parser.parse_args()

    if not _check_python():
        return 1
    if not _check_pyinstaller():
        return 1

    template = ROOT / "build_version.txt"
    if not template.exists():
        print("[FAIL] 缺少 build_version.txt（版本资源模板）")
        return 1
    if not (ROOT / "assets" / "app.ico").exists():
        print("[FAIL] 缺少 assets/app.ico")
        return 1

    # 由唯一真源 APP_VERSION 渲染版本资源，避免与 constants.py 脱节（P2-19）
    try:
        version_file = _render_version_file(
            template, WORK / "version_info.txt", APP_VERSION
        )
    except (OSError, ValueError) as exc:
        print(f"[FAIL] 渲染版本资源文件失败：{exc}")
        return 1
    print(f"[OK] 版本资源：APP_VERSION={APP_VERSION} -> {version_file}")

    DIST.mkdir(parents=True, exist_ok=True)
    # 清理 dist 里已知的历史交付物（P2-20），避免残留误交付
    _clean_legacy_artifacts()

    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm", "--clean",
        "--onefile",
        "--windowed",
        "--noupx",                     # 禁用 UPX：减少杀软误报与解包异常（P2-18）
        "--uac-admin",
        "--icon", str(ROOT / "assets" / "app.ico"),
        "--name", NAME,
        "--add-data", f"{ROOT / 'assets'}{os.pathsep}assets",
        "--version-file", str(version_file),
        "--distpath", str(DIST),
        "--workpath", str(WORK / "work"),
        "--specpath", str(WORK / "spec"),
        str(ROOT / "run.py"),
    ]

    print("[打包命令]")
    print("  " + " ".join(cmd))
    print()

    started = time.time()
    result = subprocess.run(cmd, cwd=str(ROOT))
    elapsed = time.time() - started

    if result.returncode != 0:
        print(f"\n[FAIL] PyInstaller 退出码 {result.returncode}")
        return result.returncode

    artifact = DIST / f"{NAME}.exe"
    if not artifact.exists():
        print("\n[FAIL] 产物未生成")
        return 1

    size = artifact.stat().st_size
    print(f"\n[OK] 打包完成，用时 {elapsed:.1f} 秒")
    print(f"     {artifact}  ({size:,} 字节 / {size / 1024 / 1024:.1f} MB)")

    # 审计 §5.3-10：打包后自检——把发布关键属性纳入构建断言（失败即构建失败）。
    # 延迟导入：自检逻辑与打包逻辑解耦，且导入期不引入额外开销。
    from tools.verify_release import verify_release

    print()
    if verify_release(artifact) != 0:
        print("\n[FAIL] 产物自检未通过（见上），构建失败")
        return 1

    if not args.keep:
        shutil.rmtree(WORK, ignore_errors=True)
        print("     中间产物已清理（--keep 可保留）")

    return 0


if __name__ == "__main__":
    sys.exit(main())
