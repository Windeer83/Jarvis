# Jarvis GitHub 开源复用审计

> 审计日期：2026-08-29
> 审计范围：GitHub 仓库 `Windeer83/Jarvis` 的全部 31 个已发布 Issue（开放与关闭）以及 [`development-manual-v2.md`](../product/development-manual-v2.md) 中尚未发布为 Issue 的 V2-A / V2-B 工作。
> 目标：优先复用成熟开源底座，把自研限制在 Jarvis 特有的产品规则和跨模块胶水层。

## 1. 结论

Jarvis 不需要从零搭建活动监测、桌面安装更新、AI SDK、语音识别、桌宠引擎、备份加密、二维码扫描或 Android 应用阻断的通用基础能力。对应能力已经有可复用项目。

真正需要自研的部分是：

- `工作承诺` 的版本化状态机、三态活动判断、偏离 episode、限时休息和真实恢复规则；
- “记录 / 提醒 / 监督”三种意图的风险分级，以及 AI 候选操作的确认门；
- 承诺回顾、每日/每周复盘的事实组合与用户确认语义；
- 一个 Windows Core 与一台华为手机之间的最小策略协议、冲突处理和降级状态；
- Mate 70 Pro+ / HarmonyOS 4.3 的真机适配和验收。

审计建议把 31 个 Issue 收敛为三类动作：

1. **关闭或由 V2 取代**：#1、#5、#16、#30；其中 #5、#16 是已被 ADR-0019 明确替代的飞书工作。
2. **合并重复任务**：#7 与 #28 合并为安装/卸载交付；#8 与 #29 合并为更新、迁移和回滚交付。
3. **保留业务目标但改成集成任务**：#20、#25、#27、#31 等不再描述为从零实现，而是围绕已选开源底座编写适配、许可、真机和故障验收；#23 已有简单 WPF 形象时不为“更通用”重构。

以上是建议，审计本身不修改、关闭或重新发布任何 Issue。

## 2. 复用决策规则

| 等级 | 含义 | Jarvis 的处理 |
|---|---|---|
| 直接采用 | 稳定库或工具与目标边界高度吻合 | 固定版本，通过包、CLI 或公开 API 使用 |
| 复用源码 / 薄适配 | 机制吻合，但需要删减 UI、替换领域模型或做华为适配 | fork 或抽取小模块，保留许可证、版权头和变更记录 |
| 参考实现 | 产品或架构值得借鉴，但技术栈、范围或许可证不适合嵌入 | 只学习交互和测试清单，不复制代码 |
| 自研 | Jarvis 特有业务语义，或没有合适且许可清晰的实现 | 保持模块小、接口明确，用开源库承担通用底层 |
| 取消 / 合并 | 已被 V2 替代，或与另一 Issue 重复 | 关闭为 superseded，或只保留一个权威 Issue |

选择顺序是：许可证可用性 → 与当前单用户范围的贴合度 → 维护活跃度 → 集成复杂度 → star 数。star 只能作为辅助信号，不能替代许可证和真机验证。

## 3. 推荐复用清单

下表的维护状态和许可证来自各项目 GitHub 仓库在审计日的公开信息。

| 能力 | 首选项目 | 许可证 | 采用方式 | 判断 |
|---|---|---:|---|---|
| V2-B 全天活动事实账本 | [ActivityWatch](https://github.com/ActivityWatch/activitywatch) / [aw-watcher-window](https://github.com/ActivityWatch/aw-watcher-window) | MPL-2.0 | 参考事件/heartbeat 模型和作为测试 oracle；不整体嵌入 | Jarvis 已有通过 Gate 的 Windows 探针；ActivityWatch 的 Python/server/watchers 会增加常驻进程和采集范围。若用户本来已安装它，可另做可选 REST provider，但不作为 V2-B 前提 |
| Windows 安装 | [WiX Toolset](https://github.com/wixtoolset/wix) | MS-RL | 保留现有开发分支采用 | #28 分支已经具备 WiX 4 安装、自启动和卸载实现，先完成目标机验收；不为了换工具重写安装器 |
| 未来标准化应用更新 | [Velopack](https://github.com/velopack/velopack) | MIT | 仅作为失败后的替代 spike | 能负责安装包、差分更新和应用版本回退，但不覆盖 SQLite 数据回滚；当前手动更新边界和已有 WiX 实现没有证明需要迁移 |
| Core / Desktop IPC | [StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc) | MIT（源码版权头明确） | 条件采用 | 已支持 .NET Stream、Pipe、WebSocket、取消和代理；现有已验证 IPC 若稳定则不强制重写，只在协议维护成本出现时替换 |
| 时间与时区语义 | [Noda Time](https://github.com/nodatime/nodatime) | Apache-2.0 | 直接采用 | 用于本地时间、时区和夏令时；当前单用户提醒不需要先引入完整调度平台 |
| 复杂 iCalendar 重复规则 | [iCal.NET](https://github.com/ical-org/ical.net) | MIT（README 明示） | 按需采用 | 只有确认需要 RFC 5545 导入导出时才使用；不要为了简单重复提醒引入额外模型 |
| 重型作业调度 | [Quartz.NET](https://github.com/quartznet/quartznet) | Apache-2.0 | 暂不采用 | 能力成熟但超出当前少量本机承诺/提醒需求；保留为规模真正增长后的候选 |
| AI 供应商抽象 | [Microsoft.Extensions.AI](https://github.com/dotnet/extensions/tree/main/src/Libraries/Microsoft.Extensions.AI.Abstractions) + [OpenAI .NET](https://github.com/openai/openai-dotnet) | MIT | 直接采用 | 使用 `IChatClient` 和官方 provider，不自写模型 HTTP 客户端；Jarvis 的候选操作类型和确认门仍自研 |
| WPF MVVM 基础 | [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | 直接采用 | 复用 `ObservableObject`、`RelayCommand` 和 messenger，不自建 MVVM 基建；消息卡、风险分级与 Core 确认仍是 Jarvis 业务 |
| 中文日期时间候选解析 | [Microsoft.Recognizers.Text](https://github.com/microsoft/Recognizers-Text) | MIT | 直接采用并加金标测试 | 已包含简体中文 DateTime recognizer；只生成候选，用户时区、歧义追问和提醒状态仍由 Core 决定 |
| 本地主动语音 | [.NET System.Speech](https://github.com/dotnet/wpf)；后备 [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) / [whisper.cpp](https://github.com/ggml-org/whisper.cpp) | .NET Foundation / Apache-2.0 / MIT | 保留现有 System.Speech，先做中文真机验收 | 只有中文识别或合成不达标才比较后备引擎，避免无必要引入模型、Native 运行时和手机耗电 |
| 桌宠运行核心 | [VPet](https://github.com/LorisYounger/VPet) | 程序核心 Apache-2.0 | 条件复用 | 当前五状态 WPF 形象已经通过 Gate，不重构；只有帧动画/拖拽引擎明显变复杂时才复用 `VPet.Core`。仓库内角色素材有独立条件，Jarvis 必须使用自己的美术资产 |
| WPF 通用控件 | [WPF UI](https://github.com/lepoco/wpfui)、[wpf-notifyicon](https://github.com/hardcodet/wpf-notifyicon) | MIT | 有缺口时直接采用 | 只补通用 UI 和托盘能力，不借此重做已经验收的桌面架构 |
| 密码保护导出与备份 | [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) | MIT | 保留现有开发分支采用 | #27 分支已用 AES ZIP 包装一致性 SQLite 快照；继续验证 manifest、隔离恢复和密码错误路径，不再换 age 或另造加密格式 |
| SQLite 迁移 | [FluentMigrator](https://github.com/fluentmigrator/fluentmigrator) | Apache-2.0 | 按需采用 | 负责数据库 schema migration；不能替代 Jarvis 对应用版本、备份、迁移、启动健康检查的一体化回滚 |
| Windows 凭据库 | [CredentialManager](https://github.com/AdysTech/CredentialManager) | MIT | 正式骨架采用薄封装 | 当前分支仍手写 `CredWriteW/CredReadW`；换成这个小封装可以删除通用 P/Invoke，Jarvis 只保留 target 命名、日志和删除范围 |
| 华为手机应用阻断（Usage Stats 路径） | [TapBlok](https://github.com/cajdata/TapBlok) | Apache-2.0 | 抽取/对照监测、前台服务、阻断页、启动恢复、定时开放和 Room 模块 | Android 7+ 且范围接近，但每秒轮询可能赶不上“有效交互前阻断”；只作为 #31 一条真机候选，不盲目 fork 整个产品 |
| 华为手机应用阻断（无障碍事件路径） | [Curfew](https://github.com/DavidRodriguez-create/curfew-android) | Apache-2.0 | 抽取/对照 service、overlay、policy、clock、override 和测试向量 | 机制贴合且 minSdk 26，但仓库在审计日前五天才创建、0 star、作者明确只在 Android 15 模拟器验证；是代码/测试来源，不是成熟生产依赖 |
| Android 阻断对照 | [SelfLock](https://github.com/EtashTyagi/SelfLock) | MIT | 参考或抽取小模块 | Accessibility + Usage Stats 备援、前台服务、精确闹钟和 Room 值得对照；其目标 Android 版本和 Pixel 测试环境不能代替 Mate 70 Pro+ 验收 |
| Android 阻断对照 | [OpenLock](https://github.com/MalicKAbdullah/openlock) | MIT | 参考或抽取小模块 | 有 UsageStats 前台轮询、前台服务、boot receiver 和 native lock screen；项目仅 2 star，仍只能作为实现对照而非可靠性证据 |
| 手机配对二维码 | [ZXing Android Embedded](https://github.com/journeyapps/zxing-android-embedded) | Apache-2.0 | 直接采用 | 只负责扫码；配对身份、密钥、撤销、重放防护和策略版本仍是 Jarvis 协议 |
| Windows 二维码与局域网发现 | [QRCoder](https://github.com/Shane32/QRCoder) / [Zeroconf](https://github.com/novotnyllc/Zeroconf) | MIT | 按需采用 | 生成配对码和 mDNS 发现，不承担 TLS pinning、设备身份或策略幂等 |
| 局域网加密交互参考 | [LocalSend](https://github.com/localsend/localsend) | Apache-2.0（主仓库） | 仅参考 | 可借鉴无中心 REST、现场证书和 SHA-256 指纹；其独立 protocol 仓库未确认许可证，且文件传输语义不等于 Jarvis 策略协议，不整体嵌入 |
| 待办、计时和复盘交互参考 | [Super Productivity](https://github.com/super-productivity/super-productivity) | MIT | 参考实现 | 可借鉴 timeboxing、休息提醒、反拖延和个人指标；不要把完整 Angular/Electron 应用嵌入 WPF 产品 |
| AI 回归评测 | [Promptfoo](https://github.com/promptfoo/promptfoo) | MIT | 仅开发/CI 使用 | 管理固定中文意图、结构化输出和红队样例，不进入 Jarvis 常驻进程 |
| WPF UI 自动验收 | [FlaUI](https://github.com/FlaUI/FlaUI) | MIT | 测试依赖 | 自动覆盖可访问窗口主链；透明点击穿透、DPI、睡眠和资源预算仍需目标机验证 |

### 明确不直接复制的项目

- [Mindful](https://github.com/akaMrNagar/Mindful) 是 GPL-2.0；可以比较功能和测试场景，不能在没有整体 GPL 决策的情况下复制到 Jarvis。
- [Khoj](https://github.com/khoj-ai/khoj) 是 AGPL-3.0；只作为个人 AI 助手产品参考。
- 没有明确 LICENSE 的仓库即使 README 自称开源，也按“不可复制”处理。
- 带非标准附加署名、商标或 UI 展示义务的许可证，在进入依赖清单前单独复核。

## 4. 已发布 Issue 逐项审计

| Issue | 结论 | 开源复用与后续动作 |
|---:|---|---|
| [#1](https://github.com/Windeer83/Jarvis/issues/1) MVP v0.1 总规格 | **由 V2 取代** | 关闭为 superseded，V2 以开发手册或新的 V2 epic 为权威；不要同时维护 V1/飞书与 V2/华为两条主线 |
| [#2](https://github.com/Windeer83/Jarvis/issues/2) WPF 桌面形象 Gate | **保留已完成证据** | 不返工；#23 继续现有简单 WPF 五状态和自有美术，复杂动画确有需要时才评估 VPet.Core |
| [#3](https://github.com/Windeer83/Jarvis/issues/3) Windows 活动证据 Gate | **保留已完成实现** | V1 监督路径已经验证；V2-B 沿用现有探针并参考 ActivityWatch 事件模型，不整体引入它的 server/watchers 多进程 |
| [#4](https://github.com/Windeer83/Jarvis/issues/4) Core/Desktop IPC Gate | **Gate 保留，正式骨架复用** | 原型自写协议已达约 1,300 行且仍需 framing、序列、取消等通用能力；正式骨架优先改用 StreamJsonRpc，故障恢复、权威快照和熔断仍是 Jarvis 规则 |
| [#5](https://github.com/Windeer83/Jarvis/issues/5) 飞书账号与锁屏隐私 Gate | **取消** | ADR-0019 已明确 V2 用华为专用端替代飞书；关闭为 superseded，不再做飞书真机 Gate |
| [#6](https://github.com/Windeer83/Jarvis/issues/6) 凭据与密码备份 Gate | **保留已完成证据** | Windows Credential Manager 路径保留，正式骨架用 CredentialManager 去掉重复 P/Invoke；备份保留现有 SharpZipLib AES ZIP |
| [#7](https://github.com/Windeer83/Jarvis/issues/7) 安装/自启动/卸载 Gate | **与 #28 合并** | #28 已有 WiX 4 开发分支；唯一交付任务继续验证自启动、默认保留数据卸载和彻底删除，不另起 Velopack 重写 |
| [#8](https://github.com/Windeer83/Jarvis/issues/8) 升级/迁移/整体回滚 Gate | **与 #29 合并** | 保留现有 SQLite snapshot/migration/健康回滚编排；Velopack 只在现有手动更新包不能满足时做替换 Gate |
| [#9](https://github.com/Windeer83/Jarvis/issues/9) 一次性工作承诺 | **保留自研** | Jarvis 核心领域，不用通用待办库替代；时间语义可依赖 Noda Time |
| [#10](https://github.com/Windeer83/Jarvis/issues/10) 偏离、轻提醒、真实恢复 | **保留自研** | 偏离 episode 与真实恢复是差异化规则；活动底层沿用 #3，ActivityWatch 只作数据模型/测试参考 |
| [#11](https://github.com/Windeer83/Jarvis/issues/11) 三态分类和模式 | **保留自研** | 相关/分心/未确定及“无法观察”是 Jarvis 领域语义，不交给 AI 或通用 blocker |
| [#12](https://github.com/Windeer83/Jarvis/issues/12) 限时休息 | **保留自研** | 使用系统定时与 Noda Time，状态转换自研 |
| [#13](https://github.com/Windeer83/Jarvis/issues/13) 承诺模板 | **保留自研** | 简化的领域模板，不引入工作流平台或通用模板引擎 |
| [#14](https://github.com/Windeer83/Jarvis/issues/14) 重复承诺 | **保留已完成实现** | 当前重复规则够用时不迁移 iCal.NET；只有要导入/导出 RFC 5545 时再接入 |
| [#15](https://github.com/Windeer83/Jarvis/issues/15) 承诺修订历史 | **保留自研** | 版本、旧卡失效和可解释历史是核心一致性规则；可用常规 SQLite migration，不采用事件溯源框架 |
| [#16](https://github.com/Windeer83/Jarvis/issues/16) 飞书升级闭环 | **取消** | 与 V2 手机端方向冲突，关闭为 superseded；需要保留的回应语义迁移到新的手机同步 Issue |
| [#17](https://github.com/Windeer83/Jarvis/issues/17) 承诺回顾 | **保留并改写为 V2 对话中枢** | 事实聚合和确认自研；Super Productivity 只作交互参考 |
| [#18](https://github.com/Windeer83/Jarvis/issues/18) 每日对话式复盘 | **保留并改写 V2-B** | 时间线来自正式承诺/提醒和 V2-B 活动事实账本；ActivityWatch 只提供模型参考，复盘结论仍由用户确认 |
| [#19](https://github.com/Windeer83/Jarvis/issues/19) 周期复盘 | **保留并改写 V2-B** | 原票默认 14 天、输出 1—3 个重心；V2 改为每周、最多一项监督调整建议。复用同一事实投影，不另造分析数据库或实验平台 |
| [#20](https://github.com/Windeer83/Jarvis/issues/20) 云端 AI 与闲聊 | **改为集成** | 采用 Microsoft.Extensions.AI + 官方 OpenAI .NET；不自写 provider HTTP、重试、流式协议或遥测管道 |
| [#21](https://github.com/Windeer83/Jarvis/issues/21) AI 候选操作 | **底层复用、规则自研** | 模型调用复用 MEAI；使用强类型 DTO/JSON 校验，候选操作、风险分级、确认与幂等由 Core 确定性实现 |
| [#22](https://github.com/Windeer83/Jarvis/issues/22) AI 辅助复盘 | **改为集成** | 复用 #20 的同一 AI 适配层，不建立第二套代理/记忆系统；模型证据和用户确认自研 |
| [#23](https://github.com/Windeer83/Jarvis/issues/23) 桌宠外观与互动 | **保留简单 WPF 实现** | 当前五状态足够时不引入完整宠物核心；只有动画复杂度真实增长才选择性复用 VPet.Core，始终使用自有素材 |
| [#24](https://github.com/Windeer83/Jarvis/issues/24) 陪伴人格 | **保留轻量自研** | 只做语气策略和边界，不 fork Leon/Khoj 等完整个人助理，也不建设独立记忆代理 |
| [#25](https://github.com/Windeer83/Jarvis/issues/25) 主动语音 | **保留现有 System.Speech，设后备 Gate** | 先验收目标机普通话；失败才比较 sherpa-onnx/whisper.cpp，不训练或自写 ASR/TTS，不做持续唤醒 |
| [#26](https://github.com/Windeer83/Jarvis/issues/26) 保留/查看/导出/删除 | **保留并按 V2 改写** | 原票详细轨迹为 90 天，V2 权威已改成 30 天；基于 SQLite 和标准 JSON/CSV，数据类别、级联删除、手机撤销和审计语义由 Jarvis 定义 |
| [#27](https://github.com/Windeer83/Jarvis/issues/27) 密码备份与恢复 | **保留 SharpZipLib 集成** | 继续已有一致性 SQLite 快照 + AES ZIP + 用户选择的百度同步目录；不要另换格式或开发网盘客户端 |
| [#28](https://github.com/Windeer83/Jarvis/issues/28) 正式安装/卸载 | **吸收 #7** | 继续现有 WiX 4 分支，验收保留数据卸载、彻底删除和自启动；失败后才比较 Velopack |
| [#29](https://github.com/Windeer83/Jarvis/issues/29) 更新/回滚/删除 | **吸收 #8** | 保持手动更新政策和现有 DB/健康/删除编排；明确应用回退不能自动等同数据库回退 |
| [#30](https://github.com/Windeer83/Jarvis/issues/30) MVP v0.1 最终验收 | **由 V2 取代** | 关闭或重写为 V2-A 交易复盘端到端验收，删除飞书验收项，加入手机阻断/降级门禁，并使用已经修订的 320 MiB 平均、350 MiB 峰值资源门槛 |
| [#31](https://github.com/Windeer83/Jarvis/issues/31) 华为阻断 Spike | **保留 Gate，但必须补正文** | 当前 body 只有 `## Goal`。TapBlok/OpenLock 提供 Usage Stats 路径，Curfew/SelfLock 提供无障碍事件路径；先复用小模块和测试向量，再在 Mate 70 Pro+ 比较两条路径的 100 次切换、清后台、息屏、重启、离线和到期解除。实测前不预判赢家 |

## 5. 尚未发布的 V2 工作审计

当前 31 个 Issue 中有 21 个开放、10 个关闭，但绝大部分仍描述 V1。尤其 #31 的正文目前只有 `## Goal`，并不是可执行的 Spike 规格。V2 尚缺对话中枢纵切、单一记录库、普通提醒生命周期、正式手机端、局域网同步、活动事实账本和 V2-A 端到端验收的权威任务。

| V2 工作 | 复用方案 | 必须自研的最小部分 | 是否应发新 Issue |
|---|---|---|---|
| 对话中枢与结构化卡片 | CommunityToolkit.Mvvm + 现有 WPF + 按需 WPF UI；AI 调用复用 MEAI | 会话投影、候选操作卡、确认/撤销、错误与降级显示 | 是，作为 V2-A 垂直切片 |
| 记录 / 提醒 / 监督意图 | MEAI + 官方模型 SDK；Recognizers.Text + Noda Time | 三意图风险分级、歧义追问、强类型候选、Core 写入门 | 是，可与对话垂直切片合并，避免拆成三个框架项目 |
| 普通提醒生命周期 | Recognizers.Text + 现有 Core 时钟/持久恢复 | 到期与 30 分钟一次重提醒、完成/推迟/取消、原始原因 | 是；不引入 Quartz 或完整待办系统 |
| 简化交易复盘模板 | 沿用已完成模板/重复承诺能力 | V2 精简字段和自然语言匹配 | 是，作为现有 #13/#14 的 V2 增量而非重建 |
| 电脑轻监督 | 沿用 #3、#9—#15；V2-B 账本参考 ActivityWatch 事件模型 | 默认被动型切换、回归测试和全天事实授权边界 | 是，但只发增量/账本持久化，不重发底层监测 |
| 华为应用阻断与临时开放 | TapBlok/OpenLock（Usage Stats）与 Curfew/SelfLock（无障碍事件）复用对照；AndroidX/平台前台服务与 Alarm | Jarvis 策略映射、5 分钟原因门、权限三态、华为保活适配 | 先补全 #31 Gate；通过后再发正式集成 Issue |
| 局域网扫码配对 | Android ZXing、Windows QRCoder、按需 Zeroconf；平台 TLS/KeyStore/Windows 凭据库；标准 HTTPS/WebSocket | 一机一机的配对载荷、证书指纹、撤销、版本冲突、幂等补传 | Gate 通过后发一个端到端同步 Issue，不拆账号/发现/消息总线平台 |
| 手机最近消息和快速记录 | AndroidX 常规 UI/Room；复用同一同步协议 | 最小队列、离线待同步、回应到候选操作的映射 | 与手机同步 Issue 合并，避免第二套手机业务层 |
| 记录库 | SQLite 与现有持久化层 | 原始表达、AI 派生、撤销、转提醒/监督草稿 | 是，V2-A |
| 承诺回顾 | 沿用现有事件和 #17；Super Productivity 作 UX 参考 | 事实投影、用户原文和确认后的结果 | 改写 #17，不新建重复 Issue |
| 主动语音 | 先沿用 System.Speech；失败才用 sherpa-onnx / whisper.cpp | 按下说话、转写确认、录音删除策略 | V2-B 单一验收 Issue，不先发布引擎迁移票 |
| 全天活动事实账本 | 沿用 Jarvis 已验证探针，参考 ActivityWatch event/heartbeat 模型 | Jarvis 授权、30 天保留、每日汇总、复盘事实投影 | V2-B 单一账本 Issue；不整体嵌入 ActivityWatch，也不新建 watcher 平台 |
| 每日/每周复盘 | 复用 #18/#19、Jarvis 活动事实和同一 MEAI 层 | 事实草稿、原因原文、用户确认、每周最多一项建议 | 改写 #18/#19，不新建第二套复盘系统 |
| 桌宠与语气 | 现有 WPF + 自有美术；VPet.Core 仅复杂动画后备 | Jarvis 状态映射和安全语气 | 改写 #23/#24，不新增角色引擎 Issue |
| 安装、升级、备份 | 现有 WiX + SharpZipLib + SQLite migration；Velopack 仅失败后备 | 数据一致性快照、健康门、回滚编排、彻底删除 | 合并 #7/#28 和 #8/#29；保留 #27 已有集成 |

## 6. 建议的最小技术骨架

为避免又因为“可扩展”引入一套新平台，建议正式骨架只预留下面的薄接口：

```text
IActivityEvidenceProvider
  └─ ExistingWindowsEvidenceProvider   # V1 已验证；V2-B 沿用并扩展存储

IChatClient                            # Microsoft.Extensions.AI
  └─ Official provider adapter

IMobilePolicyTransport
  └─ LAN HTTPS/WebSocket + QR pairing

MobilePolicyExecutor
  └─ adapted TapBlok mechanisms + Jarvis rules

IBackupProtector
  └─ SharpZipLib AES ZIP              # 已有分支实现
```

不建议在正式骨架前加入通用插件系统、代理框架、消息总线、云账号、跨平台 UI 抽象、工作流引擎、向量数据库或事件溯源框架。

## 7. 许可证与供应链门禁

当前 Jarvis GitHub 仓库没有声明顶层许可证。该状态不妨碍私人开发，但在复制或分发第三方代码前必须完成以下动作：

1. 新建 `THIRD-PARTY-NOTICES.md`，记录项目、精确版本/commit、用途、许可证、是否修改和来源链接。
2. Apache-2.0 / BSD / MIT 项目保留版权和许可证文本；Apache 源码抽取保留原文件头和 NOTICE 要求。WiX 作为 MS-RL 构建工具使用，不复制其源码进产品。
3. ActivityWatch 的 MPL-2.0 代码不复制进 Jarvis；只参考公开事件模型。若未来增加可选本机 API provider，保持进程隔离；若修改其源码文件，按 MPL 的文件级要求提供对应源码。
4. GPL/AGPL 项目只作研究参考，除非以后明确决定采用兼容的整体开源方式。
5. VPet 的代码与美术分开审查；本项目不带入其角色素材。
6. 没有 LICENSE、许可证元数据不明或带自定义附加条款的代码，在人工确认前不进入仓库。
7. 每个依赖固定版本并检查发布来源、哈希、维护状态和已知漏洞；不要从 README 复制未经版本化的大段代码。

## 8. Issue 去重操作清单

经用户确认后再执行：

1. 关闭 #5、#16，原因统一写为“由 ADR-0019 和 V2 华为专用手机端取代”。
2. 关闭或改写 #1、#30 为一个 V2 epic + 一个 V2-A 端到端验收，不再把 V1 飞书路线标为当前主线。
3. 选择 #28 为安装交付权威 Issue，吸收 #7；选择 #29 为更新/迁移/回滚权威 Issue，吸收 #8。
4. 改写 #20、#25、#27、#31 的描述，把既有开源采用、候选项目、许可证、集成边界和验收条件写入正文；#23 明确“简单实现优先，VPet 仅后备”。
5. 改写 #17—#19 为 V2 对话式回顾/复盘；活动账本沿用 #3 已验证探针并参考 ActivityWatch 数据模型，不发布另一套 watcher 平台。
6. 新 V2 Issue 模板增加必填的 `Reuse check`：检索过的仓库、许可证、采用/不采用理由、计划固定的版本、需要保留的 Jarvis 自研边界。

## 9. 当前决策

在 Issue 去重完成前不继续发布 V2 实现任务。正式骨架的第一项仍是 #31 手机 Gate，但其实现策略改为“用 TapBlok 对照 Usage Stats 路径，用 Curfew / SelfLock 对照无障碍事件路径，复用通用模块后在华为真机择优”，而不是从空白 Android 工程重新实现应用识别、前台服务、阻断页、定时与持久化。Curfew 极新且只在模拟器验证，TapBlok 轮询可能偏慢，两者都不能跳过 Gate。
