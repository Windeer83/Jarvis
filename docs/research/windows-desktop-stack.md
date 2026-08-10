# Jarvis MVP：Windows 桌面技术栈选型

> 结论基准日：2026-08-08。资料只采用 Microsoft、Tauri、Electron 的官方文档或官方源码/API 文档。本文把“文档事实”和“工程推断”分开；没有引用未经验证的内存基准。

## 结论

MVP 推荐采用 **.NET 10 LTS + C# + WPF，复杂面板按需嵌入 WebView2**：

- `Jarvis.Core.exe`：普通用户会话中的无主窗口进程；负责监督状态机、定时、Win32 采样、SQLite、飞书、AI 编排、托盘、自启动和备份。
- `Jarvis.Desktop.exe`：WPF 进程；负责透明置顶桌宠、动画、提醒气泡，以及聊天、设置、复盘等面板。
- Core 是数据库唯一写入者；Desktop 通过 `.NET System.IO.Pipes` 的命名管道请求/订阅事件。命名管道原生支持双工、异步和多客户端，本项目只允许当前 Windows 用户连接。[Microsoft：.NET named pipes](https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication)
- Win32 接入使用 C# `LibraryImport`/P/Invoke：`GetLastInputInfo` 用于当前会话的输入空闲检测，`GetForegroundWindow` 用于前台窗口识别。[Microsoft：GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo)、[Microsoft：GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow)、[Microsoft：P/Invoke](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke)
- UI 默认全部用 WPF。只有知识图谱等确实依赖成熟 Web 组件的页面才创建 WebView2；聊天、设置、复盘在 MVP 中先用 WPF，避免浏览器进程常驻。
- 发布为 `win-x64` 自包含应用，再由传统 Windows Installer 封装。用户得到一个安装包，但安装目录包含两个 EXE 和依赖文件；不追求“整个产品只有一个物理 EXE”。.NET 支持架构限定的 self-contained 与 single-file 发布，Windows Installer支持开始菜单、程序管理及卸载集成。[Microsoft：.NET single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)、[Microsoft：WPF deployment](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/app-development/deploying-a-wpf-application-wpf)

这是当前最稳妥的选择：它把常驻监督路径留在同一套 C#/.NET 技术中，Windows API、托盘、窗口和 IPC 都有直接入口；桌宠关闭复杂面板时不必保留 Chromium/WebView2 进程组；同时仍为以后需要的 Web 知识图谱保留出口。

## 推荐栈的具体组成

| 层 | MVP 选择 | 说明 |
|---|---|---|
| 语言与运行时 | C# / .NET 10 LTS | 截至基准日，.NET 10 为 Active LTS，官方支持到 2028-11-14；应跟进每月最新补丁。[.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) |
| Core | `net10.0-windows` WinExe + WinForms `ApplicationContext`/`NotifyIcon` | Core 不显示常规窗口，只维持消息循环和托盘；`NotifyIcon` 正是供后台进程在通知区域提供入口的官方组件。[NotifyIcon](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon?view=windowsdesktop-10.0) |
| Desktop | WPF | WPF 原生提供透明、无边框、置顶、命中测试、2D 图形和动画；`AllowsTransparency` 用于非矩形透明窗口并要求 `WindowStyle=None`，`Topmost` 控制置顶。[AllowsTransparency](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.allowstransparency?view=windowsdesktop-10.0)、[Topmost](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.topmost?view=windowsdesktop-10.0) |
| 桌宠输入行为 | WPF + HWND interop | WPF 负责可交互区域；“完全鼠标穿透、不抢焦点、隐藏 Alt+Tab/任务栏”等模式在 HWND 层处理并做真机验证。Windows 官方定义了 `WS_EX_NOACTIVATE`、`WS_EX_TOOLWINDOW`、`WS_EX_TOPMOST` 等扩展样式；不能把名字相近的 `WS_EX_TRANSPARENT` 直接当成完整点击穿透保证。[Extended Window Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles) |
| 动画 | WPF `Image`/序列帧或矢量 + Storyboard/Transform | WPF 有内建动画时间系统和硬件渲染管线，适合 MVP 的轻量表情、呼吸、移动和提醒动作。[WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)、[Animation overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/animation-overview) |
| 可选 Web UI | Microsoft Edge WebView2，仅按需创建 | 共用一个 `CoreWebView2Environment` 和 user-data folder；面板全部关闭后释放控制器，并验证辅助进程确实退出。WebView2 使用 Edge 多进程模型，包含 browser、renderer、GPU/utility 等进程；官方明确说明实例会增加启动和内存开销。[Process model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model)、[Performance guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance) |
| IPC | `NamedPipeServerStream` / `NamedPipeClientStream` + 长度前缀 JSON | Core 提供版本化命令/事件协议；Desktop 不直接打开正式数据库。使用异步读写、连接超时、心跳、重连和 request ID 幂等。[WaitForConnectionAsync](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream.waitforconnectionasync?view=net-10.0) |
| 数据 | SQLite，由 Core 独占写入 | 可用 `Microsoft.Data.Sqlite` 作为 .NET 访问层，但它不能让原版 SQLite 自动获得磁盘加密；Microsoft 明确说明 SQLite 默认不加密，必须另选支持加密的 SQLite 构建/供应商。加密 provider 必须在实现前单独定案。[Microsoft.Data.Sqlite encryption](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption) |
| 部署 | self-contained `win-x64` + Windows Installer | 不依赖目标机预装 .NET 10；安装器写入当前用户自启动项、开始菜单和卸载信息，并在 WebView2 功能启用前检测 Evergreen Runtime。 |

目标机现场事实只用于本项目，不是通用基准：Windows 11 x64 build 26200；已装 .NET 8.0.23 x64 runtime/WindowsDesktop runtime 和 WebView2 Evergreen 139；未装 .NET SDK；有 Node/npm，无 Cargo。采用 self-contained 后，运行不依赖现有 .NET 8；开发机仍需安装 .NET 10 SDK。

## 候选比较

| 方案 | 能否实现 | 与两进程设计的贴合度 | 资源预算风险（工程推断） | 维护与部署 | 结论 |
|---|---|---|---|---|---|
| **.NET 10 + WPF + 可选 WebView2** | 高。透明、置顶和动画有 WPF 原生能力；穿透等特殊行为可进入 HWND/Win32 | 高。两个独立 EXE、命名管道、Core 独占 DB 都是 .NET 直接能力 | **最低**。面板关闭时可以完全不创建 WebView2；但透明动画的填充率和重绘仍必须实测 | 单语言、.NET 10 LTS 到 2028；可 self-contained | **推荐** |
| .NET 10 + WinUI 3 | 高，但桌宠特殊窗口仍常需 HWND/AppWindow interop | 高 | 中。官方说明 unpackaged self-contained Windows App SDK 会更慢启动并使用更多内存；framework-dependent 则多一项运行时部署 | 截至基准日稳定分支 1.8.10 已进入 Maintenance，1.8 支持期到 2026-09-09；更新节奏比 .NET LTS 更短。[Windows App SDK channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel)、[Deployment overview](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview) | 不为“更现代外观”承担 MVP 的窗口与部署复杂度 |
| Tauri 2 + Rust + Web 前端 | 高。官方 API 有 transparent、always-on-top、skip-taskbar、ignore-cursor-events、tray 和 autostart；Windows 安装包可用 MSI/NSIS。[Tauri window API](https://docs.rs/tauri/latest/tauri/window/struct.Window.html)、[Tray](https://v2.tauri.app/learn/system-tray/)、[Autostart](https://v2.tauri.app/plugin/autostart/)、[Windows installer](https://v2.tauri.app/distribute/windows-installer/) | 中。Tauri 自己已有 Rust core + WebView 进程模型；若严格保留独立 Jarvis Core，还要再建一个 Rust EXE 和第二层 IPC | 中。它不捆绑 Chromium，但 Windows UI 仍使用 WebView2 多进程；“小安装包”不等于“低常驻内存”。无可靠官方数字可证明满足 300 MB | Tauri 2 自 2024-10 稳定，2026 年仍在 2.x 活跃维护；但项目同时维护 Rust 与 TypeScript，目标机当前也无 Cargo。[Tauri process model](https://v2.tauri.app/concept/process-model/)、[Tauri 2 stable](https://v2.tauri.app/blog/tauri-20/) | 若未来改成“Web UI 优先、跨平台优先”再考虑；当前不是首选 |
| Electron | 高。`BrowserWindow` 原生提供透明、置顶和忽略鼠标事件，另有 Tray、自启动和 Forge 安装工具。[BrowserWindow](https://www.electronjs.org/docs/latest/api/browser-window/)、[Tray](https://www.electronjs.org/docs/latest/api/tray)、[Login item](https://www.electronjs.org/docs/latest/api/app)、[Packaging](https://www.electronjs.org/docs/latest/tutorial/tutorial-packaging) | 中。可以把 main/renderer 当作逻辑隔离，也可再建外部 Core，但后者增加进程与协议层 | **最高**。官方架构至少包含 Node main 和每个窗口的 Chromium renderer，还可能有 utility/GPU 进程；这不证明一定超预算，但使 300 MB 常驻目标的余量最小。[Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model) | Web 开发最快，但官方只支持最新 3 个稳定大版本，约每 8 周一个大版本，需要持续升级。[Electron release policy](https://www.electronjs.org/docs/latest/tutorial/electron-timelines) | 不建议作为常驻监督型 MVP 的底座 |

Avalonia 和 Flutter 未进入最终短名单。这是工程判断：产品已明确只支持 Windows 11 x64，两者的主要跨平台价值不能抵消新增运行时/渲染层、Windows 特殊行为适配和插件依赖；若以后正式增加 macOS，再重新评估。

## 事实与推断边界

**已由官方资料确认：**

- WPF 支持透明非矩形窗口、置顶、动画、硬件加速和 HWND interop。
- Tauri 与 Electron 都能表达桌宠需要的主要窗口行为。
- WebView2、Tauri 和 Electron 都采用多进程 Web 渲染模型；WebView2 官方明确写有额外启动与内存开销。
- .NET 10 的支持终止日期、Windows App SDK 1.8 的维护状态、Electron 的三版本/八周支持策略均有官方政策。

**尚未被文献证明、必须以 Jarvis 原型验证：**

- 任一方案在此目标机上的实际 working set、private bytes、GPU memory、启动时间和 idle CPU。
- WPF 透明桌宠在具体动画尺寸、帧率、多显示器和 DPI 下是否稳定低于 idle CPU 1%。WPF 官方只说明硬件管线以及透明叠加会增加像素重绘成本，不能据此推导一个内存或 CPU 数字。[WPF hardware performance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-taking-advantage-of-hardware)
- WebView2 控制器释放后，Jarvis 的具体页面和引用管理是否能及时让进程组退出。
- 点击穿透、不抢焦点、锁屏/睡眠恢复、Explorer 重启后的托盘恢复是否在所有目标场景正确。

## 开工前的强制技术验证

在完整功能开发前做一个最小 WPF spike，并把以下结果作为继续使用该栈的门槛：

1. `Jarvis.Core.exe + Jarvis.Desktop.exe` 运行透明桌宠，复杂面板关闭，稳定 10 分钟后合计 working set 目标 `<=300 MB`，平均 idle CPU `<1%`。
2. 分别测试静止、15 FPS、30 FPS 序列帧；若 30 FPS 不满足预算，MVP 默认降为 15 FPS/事件触发动画，而非改技术栈。
3. 打开聊天、设置、复盘和一个 WebView2 知识图谱样例，总 working set 目标 `<=800 MB`；关闭后确认 WebView2 辅助进程和内存回落。
4. 验证多显示器/DPI、透明边缘、交互区与穿透区、全屏应用、睡眠恢复、Explorer 重启。
5. 分别杀掉 Desktop 与 Core，验证既定的自动重启、监督中断提示、命名管道重连和数据库单写者约束。
6. 用 self-contained 安装包在一台没有 .NET 10 runtime 的 Windows 11 x64 环境完成安装、自启动、升级和卸载测试。

若第 1 项在简化动画后仍失败，再用同一场景制作 Tauri 2 对照原型；不要仅凭“Rust/安装包更小”直接迁移。Electron 只有在开发速度被明确提升为高于常驻资源预算的优先级时才重新进入候选。
