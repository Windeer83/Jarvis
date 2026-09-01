# T08 一次性工作承诺纵切

## 已交付

- `Jarvis Desktop` 提供简短 WPF 表单，支持电脑型与线下工作承诺、开始时间与时长、投入目标和成果目标、软件与网站目标、监督模式及完整提醒默认值。
- Core 补齐默认值后返回承诺卡片；候选承诺只有一个暂态槽，新预览覆盖旧预览，确认前不写入 SQLite，也不占用电脑监督时段。
- 用户显式确认后，Core 在一个事务内重新检查半开区间 `[start, end)` 冲突并写入正式承诺；相邻电脑时段允许，重叠时明确拒绝。
- 承诺相位由 Core 的时钟和 SQLite 中的确认安排导出：等待开始、五分钟准备缓冲、监督中、线下进行中（不自动监督）或待回顾。计划结束只进入待回顾，不推断是否完成。
- 线下工作不占电脑自动监督槽，不调用活动输入；到点提示后可由用户手动确认开始。
- Core 是 SQLite 唯一写者。Desktop 通过当前用户、当前会话命名管道提交操作并渲染 Core 快照；Core 托盘也从同一快照显示状态。
- Core 与 Desktop 都限制为当前会话单实例。Desktop 第二次启动会唤起已有窗口，不会产生第二个正式状态投影。
- 生产活动适配器在读取空闲和前台软件前先检查 Windows `WTSSessionInfoEx.SessionFlags`；锁屏、未知或查询失败一律返回无法观察。

本纵切没有提前实现承诺模板、重复承诺、偏离提醒升级、飞书、AI、承诺回顾、安装器或备份。

## 验证

使用已配置的 .NET 10 SDK：

```powershell
dotnet build Jarvis.slnx --configuration Release
dotnet test Jarvis.slnx --configuration Release --no-restore
dotnet list Jarvis.slnx package --vulnerable --include-transitive
```

真实双进程 smoke 会使用隔离临时 SQLite，通过 IPC 预览并确认一条未来承诺，停止初始 Core，再以同一数据库重启 Core 并核对承诺 ID、字段和相位；随后验证 WPF Desktop 单实例，按脚本实际捕获的精确进程对象关闭测试实例并删除隔离目录：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-t08.ps1
```

## 本地启动

先完成 Release 构建，再用 .NET 10 启动 Core；Core 会按 `--desktop-path` 启动 Desktop：

```powershell
$dotnet = "D:\Desktop\codex\20260806时间管理项目\Jarvis\.tools\dotnet\dotnet.exe"
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
& $dotnet exec .\src\Jarvis.Core\bin\Release\net10.0-windows\Jarvis.Core.dll `
  --desktop-path .\src\Jarvis.Desktop\bin\Release\net10.0-windows\Jarvis.Desktop.exe
```

默认正式数据库位于当前 Windows 用户的 `%LOCALAPPDATA%\Jarvis\jarvis.db`。开发或人工验证时可增加 `--data-dir <隔离目录>`。

## 剩余人工观察

自动化已经验证规则、真实 SQLite 重启、IPC 和双进程启动；仍建议在目标电脑上做一次简短人工观察：表单和承诺卡片文字是否完整、准备缓冲期间 Desktop 与托盘是否显示一致，以及跨日时间是否清楚。锁屏活动证据的目标机 Gate 已由 T02 验证，本纵切沿用其 fail-closed 边界。

## 2026-08-12 目标机验收

- 目标机系统全局仅安装 .NET 8。首次人工启动暴露出 Core 直接启动 framework-dependent `Jarvis.Desktop.exe` 时找不到 .NET 10 的问题；修复后，Core 会仅向 Desktop 子进程传递当前 bundled .NET 10 的运行时根目录，不要求安装全局 .NET 10。
- 确定性 smoke 把父进程的 `DOTNET_ROOT` 与 `DOTNET_ROOT_X64` 指向空目录，先确认直接启动 Desktop 必然失败，再确认 bundled Core 能启动保持存活且创建主窗口的 `Jarvis.Desktop.exe`；同时继续覆盖同一 SQLite 重启恢复与 Desktop 单实例。
- 用户在隔离测试数据库中建立电脑型承诺，投入目标为“验证 Jarvis 一次性承诺”，成果目标为“留下一条正式承诺记录”，并亲自完成从确认、到点开始到到点结束的一次完整目标机流程。
- 结束后的 Core 权威快照显示 `ActiveComputerCommitmentId = null`、承诺相位为 `AwaitingReview`：自动监督已经停止，但系统没有擅自把承诺判断为已完成。
