#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""静态校验 WPF 版 XAML 里的绑定路径。

为什么需要它：WPF 的 {Binding} 是运行期解析，路径写错既没有编译错误、也不抛异常，
只是那个绑定永远不生效。WinUI 版靠 DebugSettings.BindingFailed 把这类问题变成日志；
WPF 的等价物（PresentationTraceSources）只在 app.config 里声明才真正生效，
而本项目的单文件承诺禁止 exe.config —— 于是改成提交前静态检查。

做法：把每个 XAML 里的 {Binding ...} 路径与 C# 的公开属性表对照，逐段解析类型
（含 Columns.NameWidth 这类多级路径，以及 DataTemplate 内的上下文切换）。

显式跳过：RelativeSource / ElementName / Source= / TemplateBinding / 无路径绑定 / 索引器。

用法：python tools/check-bindings.py [仓库根目录]
退出码：0 全部解析成功 / 1 有未解析路径
"""
import os
import re
import sys

BINDING = re.compile(r"\{(?:Binding|CompiledBinding)\s*([^}]*)\}")
BINDING_ELEMENT = re.compile(r"<Binding\b([^>]*)/?>")
TEMPLATE_OPEN = re.compile(r'<DataTemplate\b[^>]*DataType="\{x:Type\s+[\w:]*[:.](\w+)\}"[^>]*>')
TEMPLATE_CLOSE = re.compile(r"</DataTemplate>")
PROPERTY = re.compile(
    r"public\s+(?:static\s+|virtual\s+|override\s+|sealed\s+|new\s+)*"
    r"([\w<>\[\]?.]+)\s+(\w+)\s*(?:=>|\{|$)",
    re.MULTILINE)
CLASS = re.compile(r"(?:class|record|struct)\s+(\w+)")


def simple_type(type_name):
    return type_name.replace("?", "").split(".")[-1].split("<")[0]


def collect_properties(root):
    """类名 -> {属性名: 声明类型（已归一成简单类型名）}。"""
    table = {}
    for dirpath, _, files in os.walk(root):
        if f"{os.sep}obj{os.sep}" in dirpath + os.sep or f"{os.sep}bin{os.sep}" in dirpath + os.sep:
            continue
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            try:
                text = open(path, encoding="utf-8-sig").read()
            except OSError:
                continue

            # 每个文件里可能有多个类型：按出现位置把属性归给最近的那个类头
            headers = [(m.start(), m.group(1)) for m in CLASS.finditer(text)]
            if not headers:
                continue

            def owner(position):
                current = None
                for start, name_ in headers:
                    if start <= position:
                        current = name_
                    else:
                        break
                return current

            for match in PROPERTY.finditer(text):
                type_name, property_name = match.group(1), match.group(2)
                class_name = owner(match.start())
                if class_name is None:
                    continue
                table.setdefault(class_name, {})[property_name] = simple_type(type_name)
    return table


def parse_binding(raw):
    """从 {Binding ...} 的参数里取出路径；不针对 DataContext 的绑定返回 None。"""
    args = raw.strip()
    for skip in ("RelativeSource", "ElementName", "Source=", "ConverterParameter={"):
        if skip in args:
            return None
    segments = [part.strip() for part in args.split(",") if part.strip()]
    path = None
    for segment in segments:
        if segment.startswith("Path="):
            path = segment[len("Path="):].strip()
            break
        if "=" not in segment:
            path = segment
            break
    if path is None:
        return None
    path = path.strip().strip("'\"")
    if not path or path == "." or "[" in path or path.startswith("("):
        return None
    return path


def resolve(path, context, table):
    """逐段解析；返回第一处失败的 'Type.Property' 或 None。"""
    current = context
    for segment in path.split("."):
        if current is None:
            return None  # 类型来自外部程序集：不误报
        properties = table.get(current)
        if properties is None:
            return None
        if segment not in properties:
            return f"{current}.{segment}"
        current = properties[segment]
    return None


def scan(path, table):
    problems = []
    lines = open(path, encoding="utf-8-sig").read().splitlines()
    context = "MainViewModel"
    in_template = False

    for number, line in enumerate(lines, start=1):
        opened = TEMPLATE_OPEN.search(line)
        if opened:
            in_template, context = True, opened.group(1)
        if in_template and TEMPLATE_CLOSE.search(line):
            in_template, context = False, "MainViewModel"

        expressions = [m.group(1) for m in BINDING.finditer(line)]
        expressions += [m.group(1) for m in BINDING_ELEMENT.finditer(line)]
        for raw in expressions:
            binding = parse_binding(raw)
            if binding is None:
                continue
            failure = resolve(binding, context, table)
            if failure:
                problems.append((path, number, binding, context, failure))
    return problems


def main(argv):
    root = argv[1] if len(argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    table = collect_properties(os.path.join(root, "src"))
    if "MainViewModel" not in table:
        print("[FAIL] 没能从 src 里解析出 MainViewModel 的属性表，检查脚本与源码路径。")
        return 1

    xaml_dir = os.path.join(root, "src", "DirMover.Wpf")
    files = []
    for dirpath, _, names in os.walk(xaml_dir):
        if f"{os.sep}obj{os.sep}" in dirpath + os.sep:
            continue
        files += [os.path.join(dirpath, n) for n in names if n.endswith(".xaml")]
    files.sort()

    total = 0
    for path in files:
        total += len(BINDING.findall(open(path, encoding="utf-8-sig").read()))

    problems = []
    for path in files:
        problems += scan(path, table)

    print(f"扫描 {len(files)} 个 XAML / {total} 处 {{Binding}}；"
          f"已知类型 {len(table)} 个。")
    if not problems:
        print("OK：所有可静态解析的绑定路径都能对上 C# 属性。")
        return 0

    for path, number, binding, context, failure in problems:
        print(f"[FAIL] {os.path.relpath(path, root)}:{number}  {binding}"
              f"  上下文 {context}  找不到 {failure}")
    print(f"\n共 {len(problems)} 处未解析。")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
