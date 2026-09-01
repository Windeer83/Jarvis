# T22 桌面形象、互动与监督状态

## 交付边界

T22 在既有 `Jarvis.Desktop` 进程内增加一个轻量桌面形象窗口，不增加第三个常驻进程，也不复制 Core 状态：

- 五张临时成人半写实二次元状态素材：空闲、工作、提醒、开心、关怀；统一为银灰中发、深海军蓝办公装与青/淡紫发光细节。
- 休息、对话、无法观察分别叠加“休息中 / 对话中 / 休眠”标记；监督和计时仍由 Core 决定。
- 鼠标悬停显示当前承诺和监督摘要；左键打开自然语言与控制面板。
- 右键可新建承诺、按当前承诺默认值开始 15 分钟或其配置的限时休息、切换专业模式、空闲自动移动、尺寸、鼠标穿透、隐藏或完全退出。
- 拖动结束会限制在当前显示器工作区并吸附最近边缘；自动移动只在空闲状态小范围移动，不跨显示器。
- 位置、大小、自动移动、专业模式、鼠标穿透和指定软件隐藏列表保存在当前用户 `%LOCALAPPDATA%\Jarvis\desktop-pet.json`。
- 鼠标穿透后用 `Ctrl+Alt+J` 恢复；Core 托盘再次打开 Desktop 也会重新显示桌宠和控制面板。
- 专业模式会在全屏、PowerPoint、Zoom、Teams、OBS 中自动隐藏；右键可把当前外部软件加入自动隐藏列表。隐藏只影响外观，监督继续。

状态素材是 T22 的临时 WPF raster 资产，不是最终 Live2D/3D 角色，也不包含亲密人格、语音或长期记忆。T23–T24 在该窗口之上增加表达策略与语音入口。

## 状态映射

| Core/Companion 投影 | 桌面形象 |
| --- | --- |
| 没有活动承诺 | 空闲 |
| 准备缓冲或监督中 | 工作 |
| 当前提醒标记有效 | 提醒 |
| 最近 5 分钟完成承诺回顾 | 开心 |
| 活动未确定、待回顾或有待确认候选 | 关怀 |
| 有明确结束时间的休息 | 休息标记 |
| 云端 AI 请求处理中 | 对话标记 |
| 活动证据无法观察 | 休眠标记 |

桌宠不从窗口文本反推状态；`DesktopPetProjectionBuilder` 只消费 Core 返回的 `SupervisionSnapshot` 和 `CompanionSnapshot`。

## 自动验证

```powershell
& ..\..\Jarvis\.tools\dotnet\dotnet.exe build Jarvis.slnx -c Release
& ..\..\Jarvis\.tools\dotnet\dotnet.exe test Jarvis.slnx -c Release
& ..\..\Jarvis\.tools\dotnet\dotnet.exe format Jarvis.slnx --verify-no-changes
& ..\..\Jarvis\.tools\dotnet\dotnet.exe list Jarvis.slnx package --vulnerable --include-transitive
```

`DesktopPetScenarios` 覆盖状态映射、位置约束/吸附、设置持久化与实际 WPF resource 解码。完全退出通过显式 Core IPC 请求，由 Core 主消息循环统一结束，避免 Desktop 单独退出后监督仍静默运行。

## 人工验收保留项

- 在目标显示缩放和双屏布局上拖动、吸附、重启位置恢复，并确认自动移动不会跨屏。
- 观察五种状态的可读性、呼吸动效、悬停提示、右键菜单和不抢焦点体验。
- 在全屏/PowerPoint/指定软件中确认桌宠隐藏而 Core 监督不停止。
- 验证鼠标穿透与 `Ctrl+Alt+J` 恢复、隐藏后由 Core 托盘重新打开、完全退出确认。
- 资源边界采用 Core + Desktop 同窗稳定采样；T22 不以短时烟测替代 10 分钟证据。
