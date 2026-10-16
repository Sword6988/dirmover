# 用户数据转移工具（DirMover）

[![ci](https://github.com/Sword6988/dirmover/actions/workflows/ci.yml/badge.svg)](https://github.com/Sword6988/dirmover/actions/workflows/ci.yml)

把 Windows 当前用户的个人文件夹（文档、图片、音乐、视频、下载、桌面、收藏夹）
从系统盘迁移到其它磁盘，并同步修改注册表，让系统继续正常工作。

效果等同于资源管理器「属性 → 位置 → 移动」，区别是可以**一次处理多项**，
并且把**数据安全**当成第一优先级：**复制 → 源↔目标全量校验 → 写注册表 → 校验通过才删源**。

- 单文件免安装，双击即用
- 运行时**零第三方依赖**（纯 Python 标准库 tkinter + winreg）
- 支持一键回滚、源目录不丢、失败可定位

---

## 下载

前往 **[Releases](https://github.com/Sword6988/dirmover/releases/latest)** 下载 `DirMover.exe`：

| 产物 | 说明 |
|---|---|
| `DirMover.exe` | 单文件免安装（约 12.4 MB）。双击运行，**需要管理员权限**（会弹 UAC） |

**运行要求**：64 位 Windows 10 / 11。exe 已内嵌 CPython 3.14，本机**无需安装 Python**。

> 校验（v3.0.0）：
> ```
> SHA-256  7a188cc994821bf8d9aab4898f61378db59974146e29c8dcbe8037185d8937e9
> ```

---

## 快速开始

### 用打包好的 exe

```
DirMover.exe        # 双击运行
```

日志 `logs\` 与恢复快照 `backups\` 自动生成在 exe 旁边，绿色便携；
程序**不做任何设置持久化**，每次启动都用内置默认值。

### 从源码运行

```bash
cd pydm
python run.py              # 图形界面
python run.py --scan       # 只读列出各文件夹当前位置与状态
python run.py --dry-run    # 完整规划但不做任何改动
```

源码运行只需 **Python 标准库**（tkinter 随 Python 安装自带），无第三方运行时依赖；
版本要求 Python ≥ 3.10（程序用到 `dataclass(slots=True)`）。

> 两种方式的 Windows 门槛不同：**源码运行**支持 Windows 7+（64 位）；
> **交付 exe** 需 Windows 10+（内嵌的 CPython 3.14 已放弃 Win7/8.1）。详见 [`pydm/README.md`](pydm/README.md)。

---

## 安全模型

每一项目标文件夹都按下面**顺序不可颠倒**的事务执行：

1. **规划**：读注册表当前位置 → 合成计划 → 逐项校验
   （目标是绝对路径、不为空、不含 Windows 保留名、不是系统目录、不在源之内）。
2. **执行**：
   - 同盘且目标不存在 → 原子 `os.rename`，一步到位、无中间态；
   - 否则走安全通道：**清点源**（不漏项才敢删源）→ 重建目录与链接 →
     **分块复制并同时计算内容摘要** → **源↔目标全量校验**（内容、大小、源未改动、清单差集为空）
     → **写注册表** → **最后**删除源目录。
3. **善后**：重启资源管理器使改动生效（后台线程执行，界面不冻结）。

**任一步失败，源目录一定还在。** 每项的阶段都登记在 `backups\intent.json`；
进程被中断（关窗 / 断电）后下次启动会提示，并可用快照一键回滚注册表。

> 回滚的边界：快照回滚**只还原注册表值，不搬回文件**。
> 要把文件也搬回去，请用「**恢复系统默认值**」——那是**完整恢复**（搬回 + 校验 + 改回默认路径）。

**有意为之的取舍**：不因一项失败而停下全部；同名冲突**绝不静默覆盖**（内容一致则复用，
不一致则另存为 `name (1).ext`，两份都保留）；`explorer.exe` 只在设置允许时优雅重启，
且只结束本用户会话的进程。

---

## 仓库结构

```
.
├── .github/workflows/ci.yml   # CI：push / PR 时跑与本地相同的门禁
└── pydm/                      # 全部源码（+ 文档 + 测试 + 构建脚本）
    ├── run.py                 # 入口：GUI / --scan / --dry-run
    ├── build.py               # 一键 PyInstaller 打包（含构建断言与打包后自检）
    ├── docmover/              # 核心库（常量 / 注册表 / 文件系统 / 业务编排 / 界面）
    ├── tests/                 # 八个测试文件
    ├── tools/                 # 本地门禁 ci_check.py、产物核验 verify_release.py 等
    ├── docs/                  # 界面规范与审计报告
    └── README.md              # ★ 详细文档（使用要点、已知边界、构建说明）
```

**详细文档请看 [`pydm/README.md`](pydm/README.md)** —— 里面写清了全部使用要点、
**已知边界**（哪些行为与早期版本有意不同、为什么）、以及开发与构建流程。

---

## 开发与测试

本地门禁零依赖，一条命令跑「语法编译 + 全量单测 + 红线守卫」：

```bash
cd pydm
python tools/ci_check.py     # 全部通过输出 [OK] 并返回 0
```

CI（`.github/workflows/ci.yml`）在 push / PR 时执行**同一个脚本**，
不存在「本地过了、CI 挂了」的双份逻辑。

打包 exe 需要隔离的 Python 3.14 环境（构建脚本会强断言解释器与 PyInstaller 版本）：

```bash
cd pydm
py -3.14 -m venv .venv-build
.venv-build\Scripts\python -m pip install -r requirements-build.txt
python build.py              # 输出 ../dist/DirMover.exe，打包后自动核验产物
```

---

## 许可证

以 **MIT 许可证**发布，详见 [`pydm/LICENSE`](pydm/LICENSE)。

```
Copyright (c) 2026 ajian
```
