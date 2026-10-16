"""本地门禁（审计 P2-23）：语法编译 + 全量单测 + 红线守卫。纯标准库，零第三方依赖。

用法：
    <python> tools/ci_check.py
退出码：0 = 全部通过；非 0 = 有失败项。
输出一律用 ``[OK]`` / ``[FAIL]``（避免 GBK 控制台无法编码 ✓/✗ 或 emoji）。

审计 §8.25：输出流在导入时被强制为 UTF-8（见下方 ``_force_utf8_output``）。本文有中文
标题与中文失败原因，而 CI runner 的控制台编码未必是 UTF-8（GitHub windows runner 实测
为 **cp1252**），不处理会在第一行 ``print`` 就抛 ``UnicodeEncodeError`` 直接退出。
"""

from __future__ import annotations

import ast
import os
import subprocess
import sys
from pathlib import Path
from typing import Iterable, List, Tuple


def _force_utf8_output() -> None:
    """把 stdout/stderr 切到 UTF-8，使门禁在任意控制台编码下都不会因中文而崩。

    仅在流支持 ``reconfigure``（Python 3.7+ 的 TextIOWrapper）时生效；失败则静默跳过，
    退化为原有行为——即**不会**因为这段兼容代码本身引入新的失败模式。
    """
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(encoding="utf-8", errors="backslashreplace")
        except (ValueError, OSError):
            pass


_force_utf8_output()

ROOT = Path(__file__).resolve().parent.parent          # pydm/
UI_DIR = ROOT / "docmover" / "ui"
TESTS_DIR = ROOT / "tests"

# 审计 D2：`.venv-build` 是 requirements-build.txt 推荐的**构建用隔离 venv** 目录名；
# 纳入跳过集，避免「按文档建了 venv → 门禁去扫 site-packages」而把守卫自己弄红。
_SKIP_DIRS = {"__pycache__", "build_work", ".git", ".venv", ".venv-build"}

# 编译缓存守卫（审计 E1）专用跳过集：**不含** ``__pycache__``（那正是要抓的目标，
# 若沿用 ``_SKIP_DIRS`` 就永远扫不到），只跳过与交付无关的构建/环境目录。
# 同含 ``.venv-build``：venv 里必然有 ``__pycache__``，不跳过会让本条恒红。
_PYC_SKIP_DIRS = {"build_work", ".git", ".venv", ".venv-build"}

# 换行守卫扫描的文本文件范围（含无扩展名的 .gitattributes）
_TEXT_SUFFIXES = {".py", ".md", ".txt", ".yml", ".yaml"}
_TEXT_NAMES = {".gitattributes"}

# 审计 P2-18（问题#6）：构建 / 本地测试 / CI 期望的解释器，与 build.py 打进 exe 的
# CPython 同源。此门禁在版本不一致时**告警但不失败**——本地开发机可能只装了别的
# 次版本，先提示差异即可；真正的强约束由 build.py 的版本断言与 CI 的 setup-python 兜底。
EXPECTED_PYTHON = (3, 14)

# 红线：Unicode 几何字形不得出现在**可执行代码/注释**里（渲染勾选框一律走 icons.py）
_GLYPHS = "☑☐▸▾✓✗✅❌"


def _iter_py_files(base: Path) -> Iterable[Path]:
    for path in sorted(base.rglob("*.py")):
        if any(part in _SKIP_DIRS for part in path.parts):
            continue
        yield path


def _iter_text_files(base: Path) -> Iterable[Path]:
    """扫描交付树内的文本文件（复用 .py 扫描的跳过目录集合）。

    只扫传入的 ``base``（对守卫而言即 ``pydm/``）；仓库根部与本目录并级的
    开发期临时目录天然不在范围内。
    """
    for path in sorted(base.rglob("*")):
        if not path.is_file():
            continue
        if any(part in _SKIP_DIRS for part in path.parts):
            continue
        if path.suffix.lower() in _TEXT_SUFFIXES or path.name in _TEXT_NAMES:
            yield path


def _byte_to_char(text: str, byte_offset: int) -> int:
    """把 ``ast`` 的 UTF-8 字节偏移换算成 ``text`` 里的字符偏移。"""
    if byte_offset <= 0:
        return 0
    raw = text.encode("utf-8")
    if byte_offset >= len(raw):
        return len(text)
    return len(raw[:byte_offset].decode("utf-8", errors="ignore"))


def _strip_docstrings(source: str) -> str:
    """去掉所有文档字符串，避免「解释为何不用某物」的说明被误判为违规。

    实现要点（审计遗留项）：**不能直接删除文本**——那样会让 docstring 之后
    所有行号前移，守卫报出的行号与实际文件错位（历史缺陷：新加的 units 守卫
    报成 ``theme.py:522``，真实行号 540）。这里按 ``ast`` 给出的 docstring 节点
    位置（``lineno/col_offset/end_lineno/end_col_offset``，均为 UTF-8 字节偏移），
    把每个非换行字符替换为空格、**保留其中的 ``\\n``**，等价于把 docstring 抹成
    等量空白的占位行，从而 ``splitlines()`` 的行号与实际文件一致。
    """
    try:
        tree = ast.parse(source)
    except SyntaxError:
        return source

    spans: List[Tuple[int, int, int, int]] = []
    for node in ast.walk(tree):
        if not isinstance(
            node, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)
        ):
            continue
        body = getattr(node, "body", None)
        if not body:
            continue
        first = body[0]
        value = getattr(first, "value", None)
        if (isinstance(first, ast.Expr)
                and isinstance(value, ast.Constant)
                and isinstance(value.value, str)):
            spans.append((
                first.lineno,
                first.col_offset,
                getattr(first, "end_lineno", None) or first.lineno,
                getattr(first, "end_col_offset", None) or first.col_offset,
            ))
    if not spans:
        return source

    lines = source.splitlines(keepends=True)
    for start_line, start_col, end_line, end_col in spans:
        for lineno in range(start_line, end_line + 1):
            idx = lineno - 1
            if idx < 0 or idx >= len(lines):
                continue
            raw_line = lines[idx]
            body_text = raw_line.rstrip("\r\n")
            eol = raw_line[len(body_text):]
            width = len(body_text)
            col_from = _byte_to_char(body_text, start_col) if lineno == start_line else 0
            col_to = _byte_to_char(body_text, end_col) if lineno == end_line else width
            col_from = max(0, min(col_from, width))
            col_to = max(col_from, min(col_to, width))
            lines[idx] = (body_text[:col_from]
                          + " " * (col_to - col_from)
                          + body_text[col_to:] + eol)
    return "".join(lines)


def check_compile() -> List[str]:
    """语法编译校验——**不得在交付树内落盘任何 .pyc**（审计 C-1）。

    历史缺陷：此前用 ``py_compile.compile(str(path), doraise=True)``，不传 ``cfile``
    时它会按惯例把 ``.pyc`` 写进源文件旁的 ``__pycache__``——**每跑一次门禁就重新
    生成一批**。仓库虽有 ``.gitignore`` 规则，但本机当前 ``git rev-parse`` 报
    not a git repository，忽略规则不生效，整包拷走会把这些缓存一起带走。

    改用内置 ``compile()`` 只做**语法**编译：它只把源码编译成内存中的代码对象，
    不写任何文件。传**字节**而非文本，让解释器按 PEP 263 的编码声明（或 BOM）
    自行解码，与 ``py_compile`` 的读取口径一致；编译期的一切源码问题（含
    「源里含空字节」）都表现为 ``SyntaxError``，故只捕它。判定口径（每个 .py
    都必须能编译）、报错文案格式与后续 7 项守卫、退出码均保持不变。
    """
    problems: List[str] = []
    for path in _iter_py_files(ROOT):
        try:
            compile(path.read_bytes(), str(path), "exec")
        except SyntaxError as exc:
            problems.append(f"{path.relative_to(ROOT)}：{exc.msg}")
    return problems


def check_tests() -> Tuple[bool, str]:
    # 审计 E1（次序陷阱）：子进程默认**会**写 ``.pyc``——它继承本进程环境，但本
    # 进程未必带 ``PYTHONDONTWRITEBYTECODE``。若不显式关掉，导入 ``docmover`` 就会
    # 在交付树里生成 ``__pycache__``，使紧随其后的「编译缓存」守卫**必然**失败。
    # 故在继承环境的基础上强制置位，保证测试跑完交付树仍是干净的。
    env = dict(os.environ)
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    result = subprocess.run(
        [sys.executable, "-m", "unittest", "discover", "-s", "tests"],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
    )
    tail = ((result.stderr or "") + "\n" + (result.stdout or "")).strip().splitlines()
    # unittest 的汇总（`Ran 155 tests in …s` / `OK`）打到 stderr；取「Ran …」行 +
    # 末行状态，让门禁能显示条数（此前只取 stdout → 汇总恒为空）。判定逻辑不变。
    lines = [ln.strip() for ln in tail if ln.strip()]
    ran = next((ln for ln in reversed(lines) if ln.startswith("Ran ")), "")
    status = lines[-1] if lines else ""
    summary = f"{ran} {status}".strip() if ran else status
    return result.returncode == 0, summary


def check_no_bytecode() -> List[str]:
    """交付树内不得残留编译缓存（``__pycache__`` 目录 或 ``*.pyc``/``*.pyo``）。

    审计 E1：本机不是 git 工作树，``.gitignore`` 的 ``__pycache__/``/``*.pyc`` 规则
    不生效，整包拷走会把这些缓存一起带走（历史上已清过一批，又在
    ``docmover/__pycache__`` 复现 ``fsops/transfer.cpython-314.pyc``）。全程没有
    任何守卫能拦住复发，故新增本条。

    扫描时**不**跳过 ``__pycache__``（见 ``_PYC_SKIP_DIRS``），只跳过构建/环境目录；
    文件与目录两类都报，报错文案给出相对路径便于定位。
    """
    problems: List[str] = []
    for path in sorted(ROOT.rglob("*")):
        if any(part in _PYC_SKIP_DIRS for part in path.parts):
            continue
        if path.is_dir():
            if path.name == "__pycache__":
                problems.append(f"{path.relative_to(ROOT)}（__pycache__ 目录）")
        elif path.suffix.lower() in (".pyc", ".pyo"):
            problems.append(f"{path.relative_to(ROOT)}（编译缓存文件）")
    return problems


def check_ui_hex_colors() -> List[str]:
    """界面代码（除令牌源 theme.py）不得出现字面色值（审计红线）。"""
    import re

    pattern = re.compile(r"#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{3}(?![0-9a-fA-F]))")
    problems: List[str] = []
    for path in _iter_py_files(UI_DIR):
        if path.name == "theme.py":      # 唯一允许定义字面色值的令牌源
            continue
        code = _strip_docstrings(path.read_text(encoding="utf-8", errors="replace"))
        for lineno, line in enumerate(code.splitlines(), 1):
            if pattern.search(line):
                problems.append(f"{path.relative_to(ROOT)}:{lineno} 出现字面色值：{line.strip()}")
    return problems


def check_ui_glyphs() -> List[str]:
    """界面代码不得用 Unicode 几何字形当控件（字号/基线不可控）。"""
    problems: List[str] = []
    for path in _iter_py_files(UI_DIR):
        code = _strip_docstrings(path.read_text(encoding="utf-8", errors="replace"))
        for lineno, line in enumerate(code.splitlines(), 1):
            hit = [g for g in _GLYPHS if g in line]
            if hit:
                problems.append(
                    f"{path.relative_to(ROOT)}:{lineno} 出现几何字形 {hit}：{line.strip()}"
                )
    return problems


def check_no_config_persistence() -> List[str]:
    """config.py 不得做设置持久化（红线：程序为纯内存态）。"""
    problems: List[str] = []
    path = ROOT / "docmover" / "config.py"
    text = path.read_text(encoding="utf-8", errors="replace")
    for forbidden in ("save_config", "json.dump", "open(", "write_text"):
        if forbidden in text:
            problems.append(f"{path.relative_to(ROOT)} 出现被禁的持久化痕迹：{forbidden}")
    return problems


def check_line_endings() -> List[str]:
    """交付树内文本文件必须统一 LF（审计遗留项）。

    残留 CRLF 会在版本控制下污染 diff、并诱发「整文件二次翻译」。此处以
    **字节**判定（``read_bytes``），避免文本模式读取把 CRLF 归一化成 LF 而漏检；
    只扫 ``pydm/`` 交付树（``_iter_text_files`` 复用 .py 扫描的跳过目录集合）。
    """
    problems: List[str] = []
    for path in _iter_text_files(ROOT):
        cr = path.read_bytes().count(b"\r")
        if cr:
            problems.append(f"{path.relative_to(ROOT)}:{cr}")
    return problems


def check_icon_units_no_px() -> List[str]:
    """图标 ``units`` 一律是**设计单位**，不得再过 ``px()``（否则高 DPI 二次缩放）。

    根因守卫（对应本轮 chevron 缺陷）：``IconFactory._make`` 内部已按 ``scale``
    缩放（``size = max(12, round(units * scale))``），外层再套 ``px()`` 即双重放大
    （150% DPI 下 12 → 27px）。只扫界面目录，逐个找 ``units=`` 关键字实参，
    若同一行右侧表达式含 ``px(`` 即报告。
    """
    import re

    pattern = re.compile(r"(?<![A-Za-z_])units\s*=\s*[^,\n)]*px\s*\(")
    problems: List[str] = []
    for path in _iter_py_files(UI_DIR):
        code = _strip_docstrings(path.read_text(encoding="utf-8", errors="replace"))
        for lineno, line in enumerate(code.splitlines(), 1):
            if pattern.search(line):
                problems.append(
                    f"{path.relative_to(ROOT)}:{lineno} 出现二次缩放：{line.strip()}"
                )
    return problems


def main() -> int:
    print("== ci_check：本地门禁 ==")
    failed = False

    current = sys.version_info[:2]
    if current != EXPECTED_PYTHON:
        # 告警但不失败，避免误伤只装了别的次版本的开发机。
        print(
            f"[warn] 当前解释器 {current[0]}.{current[1]} 与期望的 "
            f"{EXPECTED_PYTHON[0]}.{EXPECTED_PYTHON[1]} 不一致：构建/测试解释器"
            "应以期望版本为准（审计 P2-18）。"
        )

    compile_problems = check_compile()
    if compile_problems:
        failed = True
        print(f"[FAIL] 语法编译：{len(compile_problems)} 个文件失败")
        for item in compile_problems[:10]:
            print(f"       - {item}")
    else:
        print("[OK] 语法编译（全部 .py）")

    ok, summary = check_tests()
    if ok:
        print(f"[OK] 单元测试：{summary}")
    else:
        failed = True
        print(f"[FAIL] 单元测试未通过：{summary or '（无输出）'}")

    # 审计 E1：跑在测试**之后**——即便 check_tests 的环境隔离将来被改坏，本条也能
    # 立刻抓到它新写出的 __pycache__/*.pyc（更靠后比更靠前更保险）。
    bytecode_problems = check_no_bytecode()
    if bytecode_problems:
        failed = True
        print(f"[FAIL] 交付树无编译缓存：{len(bytecode_problems)} 处")
        for item in bytecode_problems[:10]:
            print(f"       - {item}")
    else:
        print("[OK] 交付树无编译缓存（__pycache__/*.pyc）")

    for label, problems in (
        ("界面无字面色值", check_ui_hex_colors()),
        ("界面无几何字形", check_ui_glyphs()),
        ("config 无持久化", check_no_config_persistence()),
        ("文本文件统一 LF", check_line_endings()),
        ("图标 units 无二次缩放", check_icon_units_no_px()),
    ):
        if problems:
            failed = True
            print(f"[FAIL] 红线守卫 · {label}：{len(problems)} 处")
            for item in problems[:10]:
                print(f"       - {item}")
        else:
            print(f"[OK] 红线守卫 · {label}")

    print("== 结论：" + ("[FAIL] 有项目未通过" if failed else "[OK] 全部通过") + " ==")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
