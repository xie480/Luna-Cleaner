# MemGuardian.Next

<p align="center">
  <strong>看清内存压力来源，再决定是否回收。</strong><br />
  面向 Windows 10/11 x64 的本地内存诊断与自适应 Working Set 回收工具
</p>

<p align="center">
  C# / .NET 8 · 自定义 WPF 工作台 · 可选 CLI · 无联网与遥测
</p>

MemGuardian.Next 关注可用物理内存、系统 Commit、分页活动和内核池等多类指标。它不会把 Working Set 降低误报为 Commit 已释放；桌面版默认关闭自动回收。

<details>
  <summary>目录</summary>

- [功能概览](#功能概览)
- [快速开始](#快速开始)
- [桌面工作台](#桌面工作台)
- [可选 CLI](#可选-cli)
- [诊断与回收策略](#诊断与回收策略)
- [配置与本地数据](#配置与本地数据)
- [性能验证](#性能验证)
- [项目结构](#项目结构)
</details>

## 功能概览

| 模块 | 能力 |
| --- | --- |
| **内存诊断** | 区分 Physical RAM、Available RAM、Working Set、Private Commit、System Commit、Commit Limit、System Cache、Paged/Nonpaged Pool、Pagefile 和分页速率。 |
| **压力状态机** | 识别驻留集、Commit、Paging、Kernel Pool 压力及配置窗口内持续增长的进程/驱动泄漏；使用持续时间确认、恢复迟滞、冷却和 Backoff。 |
| **可视化工作台** | 提供系统态势、15 分钟趋势、进程观察、诊断证据和策略设置；桌面控件采用自定义 WPF 样式。 |
| **受控回收** | 只在确认 Pressure/Critical 且候选通过安全检查后，使用 documented `EmptyWorkingSet`；桌面端默认关闭自动回收。 |
| **自适应反馈** | 对比回收前后的 Available RAM、Working Set、系统 Commit 和分页活动；效果不佳或 paging 恶化时降低策略评分并延长冷却。 |
| **本地与隐私** | 使用固定容量 ring buffer 保存最多 60 分钟趋势；不联网、不上传指标、不发送遥测。 |

进程保护包括前台程序、最近 120 秒使用过的程序、Windows 关键进程、工具自身、denylist，以及身份或使用历史未知的进程。进程退出、Access Denied 和 PDH 计数器不可用都会按容错路径处理。

## 快速开始

### 1. 准备环境

- Windows 10/11 x64。
- **.NET 8 SDK x64**。仅安装 .NET Runtime 不够：运行已编译程序需要 Runtime，执行 `dotnet build` 和 `dotnet test` 需要 SDK。

使用 WinGet 安装：

```powershell
winget install --id Microsoft.DotNet.SDK.8 --exact
```

没有 WinGet 时，可在[微软 .NET 8 下载页](https://dotnet.microsoft.com/download/dotnet/8.0)选择 **SDK → Windows → x64**。安装后重新打开终端，使新的 PATH 和 SDK 探测结果生效。

### 2. 验证 SDK

```powershell
dotnet --list-sdks
dotnet --info
```

SDK 列表应包含 `8.0.xxx`，`dotnet --info` 应显示 `Architecture: x64`。如果 SDK 列表为空，而运行时列表中只有 `Microsoft.NETCore.App`，请安装 SDK。

### 3. 构建、测试并启动

在仓库根目录打开 PowerShell，运行：

```powershell
dotnet build -c Release
dotnet test -c Release
dotnet run --project src\MemGuardian.Next.Desktop -c Release
```

首次运行建议先保持“自动回收”关闭，观察系统状态与诊断证据。需要检查候选选择时，可使用 CLI 的 `run --dry-run`；它会完成诊断和候选评估，但不会 Trim 进程 Working Set。

## 桌面工作台

在“策略与启动”中配置监控、阈值和启动方式。桌面版自动回收默认关闭。开启“关闭窗口时继续在托盘后台运行”并保存后，关闭窗口会隐藏到托盘；双击托盘图标可恢复窗口，点击“退出应用”会停止监控并退出。

发布桌面版：

```powershell
dotnet publish src/MemGuardian.Next.Desktop -c Release -r win-x64 --self-contained true
```

发布目录为：

```text
src\MemGuardian.Next.Desktop\bin\Release\net8.0-windows\win-x64\publish
```

若使用框架依赖部署，将 `--self-contained true` 改为 `false`，目标电脑需要安装 .NET 8 Desktop Runtime。桌面控件使用自定义 WPF `ControlTemplate`，可参考[微软 WPF 样式与模板说明](https://learn.microsoft.com/dotnet/desktop/wpf/controls/styles-templates-overview)。

## 可选 CLI

CLI 适用于脚本和高级用户。所有命令均从仓库根目录运行：

```powershell
dotnet run --project src/MemGuardian.Next.Cli -- status
dotnet run --project src/MemGuardian.Next.Cli -- top --by working-set
dotnet run --project src/MemGuardian.Next.Cli -- top --by commit
dotnet run --project src/MemGuardian.Next.Cli -- diagnose --duration 60
dotnet run --project src/MemGuardian.Next.Cli -- run --dry-run
dotnet run --project src/MemGuardian.Next.Cli -- run
dotnet run --project src/MemGuardian.Next.Cli -- once
```

`status`、`top`、`diagnose` 和 `run --dry-run` 不会回收进程。CLI 的 `run` / `once` 可按安全策略执行回收；建议先运行 `run --dry-run` 查看候选和拒绝原因。桌面端与 CLI 共用互斥运行锁，避免同时修改回收状态。

## 诊断与回收策略

### 指标来源

| 指标 | Windows API / 来源 | 用途 |
| --- | --- | --- |
| Physical RAM、Available RAM | `GlobalMemoryStatusEx` | 判断物理内存总量和当前余量。 |
| Commit、Commit Limit、System Cache、Paged/Nonpaged Pool | `GetPerformanceInfo` | 区分系统提交压力、缓存与内核池占用。 |
| Working Set、Private Commit | `GetProcessMemoryInfo(PROCESS_MEMORY_COUNTERS_EX)` | 区分进程驻留页与私有提交。 |
| Page Reads/sec、Pages Input/sec、Pagefile | `PdhAddEnglishCounterW` | 观察分页活动和页面文件计数器；不可用时保留为 unavailable，不伪装为 0。 |
| CPU、I/O、线程、句柄、用户、Session、前台与最近使用 | Win32 进程 API、前台窗口 API | 排除活跃或受保护进程，并为候选排序。 |

### 回收条件与保护

候选必须属于当前用户和 Session、不是前台进程、最近使用历史可确认且闲置时间达标，同时满足 Working Set、CPU 和 I/O 阈值，并且不在 denylist 或进程冷却期内。

- 每轮最多处理 **2 个进程**。
- 全局冷却至少 **5 分钟**；同一进程至少 **15 分钟**内不重复处理。
- 最近使用保护至少 **120 秒**；候选 Working Set 下限至少 **256 MiB**。
- 只使用 documented `EmptyWorkingSet`，不调用 undocumented `NtSetSystemInformation`，不强制 Purge Standby List。

`EmptyWorkingSet` 尝试移除进程驻留页，但不保证降低 Private Commit。若 Working Set 明显下降而 Commit 基本不变，工具只报告驻留页回收。Available RAM 未明显增加、paging 恶化或 paging 指标不可用时，策略会降分并进入更长冷却 / Backoff。

## 配置与本地数据

设置和运行状态保存在 `%LOCALAPPDATA%\MemGuardian.Next`：

| 文件 | 内容 |
| --- | --- |
| `desktop.json` | 桌面监控、自动回收、托盘和开机启动偏好。 |
| `settings.json` | 诊断阈值与回收策略参数。 |
| `state.json` | 趋势样本、控制器状态以及最近 32 条回收前后测量。 |

趋势数据使用固定容量 ring buffer；文件通过临时文件和原子替换保存。策略界面可调整压力阈值、采样间隔、泄漏窗口、CPU/I/O 上限、空闲时间、冷却、回收后反馈等待、泄漏增长阈值和 denylist。安全下限由程序强制校验：全局冷却 5 分钟、进程冷却 15 分钟、最近使用保护 120 秒、每轮最多两个目标。

开机启动只写当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 项，不创建服务、计划任务，也不需要管理员权限。Windows 可能延迟运行该项，详见[微软 Run / RunOnce 文档](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys)。

## 性能验证

自动化测试覆盖诊断和策略规则，但不代表已经证明真实电脑卡顿改善。目前尚无代表性 Windows 用户负载下的卡顿、CPU 占用或页面错误测量结果。

建议在**同一设备、同一工作负载**下，对比自动回收关闭和开启时的以下数据：

- Available RAM、系统 Commit。
- 目标进程 Working Set 与 Private Commit。
- Page Reads/sec、Pages Input/sec、Hard Faults/sec。
- 目标应用交互延迟分位数。

至少记录压力出现前、候选回收前后和冷却窗口内的数据。如果分页或交互延迟持续恶化，应关闭自动回收并保留 Backoff 记录；不要只凭 Working Set 或任务管理器百分比判断效果。

## 项目结构

```text
src/MemGuardian.Next.Core       诊断、状态机、候选策略和反馈模型
src/MemGuardian.Next.Windows    Win32 / PDH 适配器、本地状态、Working Set 回收
src/MemGuardian.Next.Cli        CLI 命令入口
src/MemGuardian.Next.Desktop    WPF 工作台、托盘、启动项与后台监控
tests/MemGuardian.Next.Tests    诊断、策略、回收反馈、配置与 API smoke 测试
doc/memguardian                架构决策、实现与验证记录
```
