# 一次性发布脚本：创建 GitHub Release 并上传资产（临时脚本，不入库）
import json
import os
import subprocess
import sys
import urllib.request
from pathlib import Path

REPO = "Sword6988/dirmover"
TAG = "v3.0.2-winui3"
ASSET = Path("dist/DirMover-3.0.2-wpf-net48/DirMover.exe")
ASSET_NAME = "DirMover-3.0.2-wpf-net48.exe"

BODY = """# DirMover 3.0.2（C# 重写版，WinUI 3 / WPF 双宿主）

本仓库 `main` 分支为 Python 原版；本 release 对应 **`winui3` 分支**（C# 全面重写，行为对等：同样的判定顺序、拒绝条件、失败文案与"校验通过才删源"）。

## 本轮重点修复

- **Bug 1（重启资源管理器）实机闭环**：原实现在 `System32` 下找 `explorer.exe`（该文件实际在 `%WINDIR%`），导致拉起必失败（日志 `Win32Error=2`）。已改 `%WINDIR%\\explorer.exe`，拉起降级改真 CreateProcess，存活确认改 250ms 轮询（10s 预算 + 补拉一轮），杀掉 → 确认退出 → 拉起 → 存活全流程落诊断日志。
- **Bug 2（目录名变英文）**：磁盘路径保持英文物理名（`DefaultPath` 叶子）；中文显示名通过目标目录 desktop.ini 的 `LocalizedResourceName` 实现，源显示名在移动/复制前缓存。
- **全面审计修复 12 处**：链接重建 fail-closed、目标重叠阻断（防静默合并）、UI 重入闸门、单实例提前禁交互、空清单复核、TryReuse mtime 对齐等。
- 测试 **320/320** 全绿（0 失败 0 跳过）；Core / WPF / WinUI 三处 Rebuild 0 警告 0 错误。

## 交付物

| 文件 | 说明 |
|---|---|
| `DirMover-3.0.2-wpf-net48.exe` | **WPF 单文件版**（推荐，附件同名 zip 亦可） |

- **单文件**：1,024,512 B，Costura 织入依赖，无 `exe.config`、无 pdb，目标机只需 Win10 1809+ 自带的 .NET Framework 4.8。
- **SHA-256**：`2a35a3e877aa728e61c2baabc9521898c589276697f2a4750fbf45f4ef0ddc7b`
- 清单声明 `requireAdministrator`，启动即请求提权。
- 便携模式：程序目录可写时日志/备份落在同目录 `logs\\`、`backups\\`（属正常现象），只读位置回落 `%LOCALAPPDATA%\\DirMover`。

## 运行参数

```
DirMover.exe              正常界面
DirMover.exe --dry-run    试运行：完整规划与判定，零副作用
DirMover.exe --scan       只读扫描：写 logs\\scan-*.txt + 弹窗展示
DirMover.exe --version    版本与参数说明（GUI 弹窗）
```

> WinUI 3 便携版（全自包含、免 .NET Framework）的构建产物不入版本库，需要时用 `build.cmd portable` 自行构建；源码同在 `winui3` 分支。

## 数据安全红线（不变）

复制/校验失败不删源不写注册表；`SafeRemoveDirectory` 是唯一删源入口；写注册表失败即回滚；目标侧 9 条校验 + 盘根拒绝；`--dry-run` 零副作用。

---

原始项目：[Sword6988/dirmover](https://github.com/Sword6988/dirmover)（Python + tkinter，MIT）。本重写沿用 MIT。
"""

# 1) 从 git 凭据管理器取 token（不打印）
fill = subprocess.run(
    ["git", "credential", "fill"],
    input="protocol=https\nhost=github.com\n\n",
    capture_output=True, text=True,
)
token = None
for line in fill.stdout.splitlines():
    if line.startswith("password="):
        token = line.split("=", 1)[1]
if not token:
    sys.exit("ERROR: 未取到 GitHub 凭据")

headers = {
    "Authorization": f"token {token}",
    "Accept": "application/vnd.github+json",
    "User-Agent": "dirmover-release-script",
}
proxy_url = os.environ.get("HTTPS_PROXY") or os.environ.get("https_proxy")
handler = urllib.request.ProxyHandler(
    {"https": proxy_url, "http": proxy_url} if proxy_url else None
)
opener = urllib.request.build_opener(handler)

# 2) 创建 release
payload = json.dumps({
    "tag_name": TAG,
    "target_commitish": "winui3",
    "name": f"DirMover {TAG[1:]} — C# 重写版（英文物理路径 + 中文显示 + 全面审计修复）",
    "body": BODY,
    "draft": False,
    "prerelease": False,
}).encode()

req = urllib.request.Request(
    f"https://api.github.com/repos/{REPO}/releases",
    data=payload, headers=headers, method="POST",
)
try:
    with opener.open(req) as resp:
        rel = json.loads(resp.read())
    print(f"release created: id={rel['id']} html={rel['html_url']}")
except urllib.error.HTTPError as e:
    print(f"ERROR {e.code}: {e.read().decode()[:500]}")
    sys.exit(1)

# 3) 上传资产（exe）
data = ASSET.read_bytes()
up_req = urllib.request.Request(
    f"https://uploads.github.com/repos/{REPO}/releases/{rel['id']}/assets?name={ASSET_NAME}",
    data=data,
    headers={**headers, "Content-Type": "application/octet-stream"},
    method="POST",
)
with opener.open(up_req) as resp:
    asset = json.loads(resp.read())
print(f"asset uploaded: {asset['name']} size={asset['size']} state={asset['state']}")
