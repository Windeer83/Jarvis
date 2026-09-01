# T24 用户主动语音输入与有边界的语音回应

## 交付边界

- 麦克风只在用户点击“开始说话”后启用，用户点击“结束录音”后停止，单次最长 60 秒；Jarvis 不做唤醒词或后台持续监听。
- 使用 Windows 本机 `System.Speech` 普通话识别组件。识别不到普通话组件、麦克风不可用或识别失败时，界面明确保留原有文字入口。
- 转写先进入可编辑文本框。用户必须点击“确认转写并继续”，之后才会：发送普通对话、生成自然语言候选，或填入承诺回顾/每日复盘文字框。
- 自然语言安排仍只生成候选卡；承诺回顾和每日复盘仍需各自原有提交按钮。语音不会绕过任何正式确认边界。
- 识别直接读取默认麦克风，不创建音频文件；SQLite、聊天和导出中只可能出现用户确认后经既有入口提交的文字。
- 用户主动以语音发起普通对话时，本轮 AI 回答可朗读；相同回答始终先显示为文字。监督提醒、主动问候和后台事件不会突然朗读。
- 朗读优先选择 Windows 已安装的成年普通话女声，语速稍慢、音量 70%；不可用时退回其他普通话系统声音，仍不可用则只显示文字。
- 支持全局静音、30 分钟临时静音、停止当前朗读和“仅确认默认输出为耳机时播放”。不能确认耳机时按静音处理，不冒险外放。
- 专业表达、安静/会议/演示或全屏边界下不朗读私人内容，文字仍可查看。

实现使用 Microsoft `System.Speech` 10.0.10；该包基于 Windows Speech API，仅用于 Windows，并提供语音识别与合成能力。[官方 NuGet 说明](https://www.nuget.org/packages/System.Speech/10.0.10)

## 自动验证

```powershell
& ..\..\Jarvis\.tools\dotnet\dotnet.exe build Jarvis.slnx -c Release
& ..\..\Jarvis\.tools\dotnet\dotnet.exe test Jarvis.slnx -c Release
& ..\..\Jarvis\.tools\dotnet\dotnet.exe format Jarvis.slnx --verify-no-changes
& ..\..\Jarvis\.tools\dotnet\dotnet.exe list Jarvis.slnx package --vulnerable --include-transitive
```

`VoiceInteractionScenarios` 覆盖四个明确目标、静音/公开场合/仅耳机 fail-closed 策略、只保存呈现设置，以及实际 WPF 控件可达性。麦克风、系统语音包、默认音频端点和音色属于目标机人工验收。

## 人工验收保留项

- 点击开始说话、说一句中文、点击结束录音；确认转写可编辑，且未确认前没有生成候选或提交复盘。
- 分别验证普通对话、自然语言候选、承诺回顾和每日复盘的文字落点；后两项仍需原按钮提交。
- 普通对话验证成年普通话女声、较慢语速、文字兜底、停止朗读。
- 验证全局静音、临时静音、仅耳机，以及专业/安静/全屏下不外放。
- 在未安装普通话识别或拒绝麦克风权限时，确认错误可理解且文字功能不受影响。
