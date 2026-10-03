# MemGuardian.Next

MemGuardian.Next 是面向 Windows 10/11 x64 的本地内存压力诊断与自适应 Working Set 回收工具，使用 C# / .NET 8。项目同时提供自定义 WPF 可视化工作台和可选 CLI；界面不依赖网络，不上传指标，也不发送遥测。

## 功能

- 多维诊断：区分 Physical RAM / Available RAM、进程 Working Set、进程 Private Commit、系统 Commit 与 Commit Limit、System Cache、Paged / Nonpaged Pool、Pagefile 使用，以及 Page Reads/sec、Pages Input/sec。
- 压力判断：输出驻留集压力、Commit 压力、持续 paging、内核池压力、疑似进程泄漏和疑似驱动泄漏等证据；状态包含 Normal、Watch、Pressure、Critical、Cooldown、Backoff，并使用持续时间确认和恢复迟滞。
- 自定义桌面工作台：状态卡片、15 分钟趋势、进程指标、诊断证据和策略设置；按钮、输入框、开关、窗口栏和矢量趋势图均使用自定义 WPF 样式。
- 托盘常驻和开机启动：可设置关闭窗口后继续监控，以及登录后在当前用户会话启动；只使用 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，不申请管理员权限。
- 受控回收：只在确认 Pressure / Critical、冷却结束且候选通过全部安全检查时调用 documented `EmptyWorkingSet`。桌面端默认关闭自动回收，需主动开启。
- 自适应反馈：回收前后分别记录 Available RAM、Working Set、系统 Commit、Page Reads/sec 和 Pages Input/sec。paging 恶化会触发更长冷却 / Backoff；工作集下降而 Commit 基本不变时只报告驻留页回收，不宣称释放了 Commit。
- 有界趋势：固定容量 ring buffer 最多记录 60 分钟系统样本；进程 Commit 和 Nonpaged Pool 的增长需要覆盖配置窗口才会提示泄漏。
- 安全与容错：前台、最近 120 秒使用过的进程、系统关键进程、工具自身、denylist、身份未知的进程不会被处理。进程退出、Access Denied、不可用 PDH counter 会按缺失或跳过处理。

## 快速开始

### 运行桌面版

在 Windows 10/11 x64 安装 .NET 8 SDK 后，在仓库根目录运行：

```powershell
dotnet run --project src/MemGuardian.Next.Desktop
```

打开“策略与启动”可配置监控与阈值。自动回收默认关闭。打开“关闭窗口时继续在托盘后台运行”并保存后，关闭窗口会隐藏到托盘；双击托盘图标可恢复，点击界面中的“退出应用”会停止监控并退出。

如需发布为独立目录：

```powershell
dotnet publish src/MemGuardian.Next.Desktop -c Release -r win-x64 --self-contained true
```

发布产物位于 `src\MemGuardian.Next.Desktop\bin\Release\net8.0-windows\win-x64\publish`。框架依赖部署可使用 `--self-contained false`，目标设备需要安装 .NET 8 Desktop Runtime。桌面端使用 WPF 自定义 ControlTemplate，参见 [WPF 样式与模板说明](https://learn.microsoft.com/dotnet/desktop/wpf/controls/styles-templates-overview)。

### 可选 CLI

CLI 保留给脚本与高级用户使用：

```powershell
dotnet run --project src/MemGuardian.Next.Cli -- status
dotnet run --project src/MemGuardian.Next.Cli -- top --by working-set
dotnet run --project src/MemGuardian.Next.Cli -- top --by commit
dotnet run --project src/MemGuardian.Next.Cli -- diagnose --duration 60
dotnet run --project src/MemGuardian.Next.Cli -- run --dry-run
dotnet run --project src/MemGuardian.Next.Cli -- run
dotnet run --project src/MemGuardian.Next.Cli -- once
```

`status`、`top`、`diagnose` 和 `run --dry-run` 不会回收进程。CLI 的 `run` / `once` 可以按已有安全策略执行回收；首次建议先使用 `run --dry-run` 查看候选和拒绝原因。桌面端与 CLI 使用同一互斥运行锁，避免同时修改回收状态。

### 构建和测试

安装 .NET 8 SDK x64 后执行：

```powershell
dotnet build -c Release
dotnet test -c Release
```

## 诊断和回收原则

| 指标 | 采集方式 | 用途 |
|---|---|---|
| Physical RAM / Available RAM | `GlobalMemoryStatusEx` | 判断当前物理内存余量 |
| Commit / Commit Limit、System Cache、Paged / Nonpaged Pool | `GetPerformanceInfo` | 区分系统提交压力、缓存和内核池 |
| Working Set / Private Commit | `GetProcessMemoryInfo(PROCESS_MEMORY_COUNTERS_EX)` | 区分当前驻留页与进程私有提交 |
| Page Reads/sec、Pages Input/sec、Pagefile | `PdhAddEnglishCounterW` | 观察分页活动和页面文件计数器；Unavailable 不会被伪装为 0 |
| CPU、I/O、线程、句柄、用户、Session、前台与最近使用 | Win32 进程 API、前台窗口 API | 排除活跃进程并排序候选 |

候选必须属于当前用户和 Session，非前台，最近使用历史已知且空闲达到设定时间，Working Set、CPU、I/O 满足阈值，并且不在 denylist 或进程冷却期。默认每轮最多两个进程；全局冷却至少 5 分钟，同一进程至少 15 分钟内不重复处理。前台使用保护至少 120 秒，候选 Working Set 下限至少 256 MiB。

`EmptyWorkingSet` 的作用是让进程的驻留页可被回收，不保证降低 Private Commit。只有回收后 Available RAM 和 paging 反馈支持时策略评分才会上升；无法验证 paging、Available RAM 未明显增加或分页明显恶化时会降分并进入较长冷却 / Backoff。实现依据 documented Windows API；不会调用 undocumented `NtSetSystemInformation`，也不会强制 Purge Standby List。

## 配置与本地数据

设置、趋势、最近使用、冷却和反馈评分保存在 `%LOCALAPPDATA%\MemGuardian.Next`。桌面开关单独保存在 `desktop.json`，诊断阈值保存在 `settings.json`，趋势、控制器状态和最近 32 条结构化回收前后测量保存在 `state.json`。保存采用临时文件后原子替换。

策略界面可调整压力阈值、采样间隔、泄漏窗口、CPU / I/O 候选上限、空闲时间、回收冷却、回收后反馈等待、泄漏增长阈值与 denylist。安全下限由程序强制校验，例如最短 5 分钟全局冷却、15 分钟进程冷却、120 秒最近使用保护和每轮最多两个目标；不能通过 UI 或 JSON 把保护调低。

开机启动只写当前用户的 `Run` 项，不创建服务或计划任务；Windows 可能延迟运行该项。详细行为见 [Microsoft Run / RunOnce 文档](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys)。

## 性能验证

自动化测试验证诊断和策略规则，不等同于真实电脑卡顿测试。当前尚未在代表性 Windows 用户负载上证明回收能降低卡顿，也未测出实际 CPU 占用或页面错误变化。

建议在同一设备、同一工作负载下，分别记录“自动回收关闭”和“自动回收开启”的对照数据：Available RAM、系统 Commit、目标进程 Working Set / Private Commit、Page Reads/sec、Pages Input/sec、目标应用交互延迟分位数和 Hard Faults/sec。至少覆盖压力前、候选 trim 前后以及冷却窗口。若页面读取或应用交互延迟持续恶化，关闭自动回收并保留 Backoff 日志；不要只用回收后的 Working Set 或任务管理器百分比判定成功。

## 项目结构

```text
src/MemGuardian.Next.Core       纯诊断、状态机、候选策略和反馈模型
src/MemGuardian.Next.Windows    Win32 / PDH 适配器、本地状态、Working Set 回收
src/MemGuardian.Next.Cli        CLI 命令入口
src/MemGuardian.Next.Desktop    WPF 工作台、托盘、启动项与后台监控
tests/MemGuardian.Next.Tests    诊断、策略、回收反馈、配置与 API smoke 测试
doc/memguardian                架构决策、实现与验证记录
```
