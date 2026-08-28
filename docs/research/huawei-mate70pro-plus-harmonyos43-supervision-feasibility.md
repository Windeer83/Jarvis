# Jarvis V2：HUAWEI Mate 70 Pro+ / HarmonyOS 4.3 手机监督端可行性研究

> 结论基准日：2026-08-26。目标设备限定为 **HUAWEI Mate 70 Pro+（PLA-AL10）、HarmonyOS 4.3.0**，私人手动安装、非 root、非企业托管。本文只采用华为/鸿蒙和 Android 官方资料；凡是把多个 API 组合成产品方案的内容均明确标为工程推断。由于华为没有公开 HarmonyOS 4.3 与 Android API 级别的完整映射，所有 Android API 路径都必须在这台真机上验证后才能写入开发承诺。

## 结论先行

**可以做私人侧载原型，但现阶段只能承诺“尽力阻断”，不能依据公开资料承诺普通应用拥有系统级强制禁用其他应用的能力。**

- 华为官网确认 Mate 70 Pro+ 可运行 HarmonyOS 4.3；HarmonyOS 4.3 设备可以从外部来源安装应用包，关闭纯净模式的增强防护并经过风险确认后可手动安装。因此，制作一个仅供本人安装的签名 APK 是有官方依据的。[Mate 70 Pro+ 规格](https://consumer.huawei.com/cn/phones/mate70-pro-plus/specs/)；[HarmonyOS 4.3 外部安装包安装说明](https://consumer.huawei.com/cn/support/content/zh-cn01089223/)
- Android 官方的 `UsageStatsManager` 能在用户授予“使用情况访问”后查询其他应用的前后台事件和历史时长；`ACTIVITY_RESUMED`、`ACTIVITY_PAUSED` 事件带包名和时间，可用于计算前台区间。但 HarmonyOS 4.3 是否完整、及时地实现这些事件，官方公开资料没有确认，必须真机测。[UsageStatsManager](https://developer.android.com/reference/android/app/usage/UsageStatsManager)；[UsageEvents.Event](https://developer.android.com/reference/android/app/usage/UsageEvents.Event)
- 普通非 root、非设备所有者应用没有 Android 官方的“暂停任意其他包”权限。`setPackagesSuspended()` 只允许设备所有者、资料所有者或获委托管理应用调用；锁定任务模式也要求设备策略控制器预先将应用加入许可名单。[DevicePolicyManager.setPackagesSuspended](https://developer.android.com/reference/android/app/admin/DevicePolicyManager#setPackagesSuspended(android.content.ComponentName,%20java.lang.String%5B%5D,%20boolean))；[锁定任务模式](https://developer.android.com/work/dpc/dedicated-devices/lock-task-mode)
- 华为确实提供“应用运行黑名单”，但它属于 MDM Kit。官方要求注册企业开发者、申请 MDM 证书和 Profile，并激活企业设备管理扩展能力；这不符合本项目的私人普通应用边界。[华为 MDM Kit 开发指南](https://developer.huawei.com/consumer/cn/doc/doccenter-capabilities/mdm-kit-guide)；[应用运行黑名单 API](https://developer.huawei.com/consumer/cn/doc/harmonyos-references-V14/js-apis-enterprise-applicationmanager-V14)
- 当前最小候选方案是：**已确认承诺策略 + `UsageStatsManager` 前台检测 + 全屏悬浮阻断层 + 前台服务可见通知 + 本地离线策略库**。这是由官方原语组合出的工程方案，不是平台承诺的应用管控 API。它能否做到“点开即拦、无法操作”，取决于 Mate 70 Pro+ 真机上的事件延迟、悬浮层行为和后台存活。
- 无障碍服务可更及时地收到窗口事件并执行返回主页/覆盖窗口，但 Android 官方说明平台级无障碍服务应只用于帮助残障用户交互。即使私人侧载不经过应用商店审核，也不应把这条路径直接当成已定架构；只能作为受控技术对照，并且必须限制为包名/窗口状态，不读取界面文字。[Android 无障碍服务指南](https://developer.android.com/guide/topics/ui/accessibility/views/service)
- 华为官方明确说明 HarmonyOS 4.x 的第三方无障碍服务在清后台或重启后可能自动关闭；即使配置后台保护，官方也承认应用长时间后台运行仍可能因系统休眠和省电策略终止。因此，“持续可靠阻断”是本次最大的真机风险，而不是 UI 或局域网同步。[华为：第三方无障碍服务开关自动关闭](https://consumer.huawei.com/cn/support/content/zh-cn00410039/)；[华为：应用无法后台运行](https://consumer.huawei.com/cn/support/content/zh-cn00428704/)

### 可行性分级

| 能力 | 文档结论 | 当前判定 |
|---|---|---|
| 私人手动安装 | HarmonyOS 4.3 官方支持外部来源应用包；需要关闭增强防护、确认风险并可能输入密码 | **可行** |
| 读取指定应用的使用历史 | Android 官方有 `UsageStatsManager`；需用户在设置中授权 | **候选可行，真机验证** |
| 实时识别前台包 | Android 使用事件或无障碍窗口事件可提供信号，但 HarmonyOS 4.3 的完整实现和时延未公开 | **原型验证项** |
| 普通应用系统级暂停抖音/B站/小红书/微信 | 官方暂停包、应用黑名单、锁定任务能力均要求设备/企业管理角色 | **在既定边界下不可直接获得** |
| 检测后盖全屏阻断层 | Android 有应用悬浮层和无障碍悬浮层；华为 HarmonyOS 4.3 有悬浮窗授权入口 | **尽力型候选，真机验证** |
| 手机离开电脑后执行已缓存策略 | 本地数据库、系统定时与可见前台服务可组合实现；后台和重启恢复受系统限制 | **有限可行，需降级状态** |
| 同局域网与 Windows 配对同步 | Android 官方支持 DNS-SD/NSD 和 TLS 网络通信；也可用二维码直接提供连接信息 | **可行** |

## 1. 平台与安装边界

### 1.1 目标系统不是 HarmonyOS NEXT

华为产品规格把 Mate 70 Pro+ 的操作系统列为 HarmonyOS 4.3；同一机型另有出厂 HarmonyOS 5.0 的先锋版本，且 4.3 设备可以升级至 5.0。[Mate 70 Pro+ 规格](https://consumer.huawei.com/cn/phones/mate70-pro-plus/specs/)

这一区别是架构前提：华为针对 HarmonyOS 4.3 的消费者支持页仍明确描述“外部来源应用”和安装包流程，而 HarmonyOS 5 采用不同的应用获取和兼容方案。[HarmonyOS 4.3 外部安装包](https://consumer.huawei.com/cn/support/content/zh-cn01089223/)；[HarmonyOS 5 应用下载安装](https://consumer.huawei.com/cn/support/content/zh-cn16061787/)

**工程边界：**第二版手机端应锁定 HarmonyOS 4.3.0，不承诺升级 HarmonyOS 5 后继续工作。系统升级前必须显示兼容性警告；升级后的支持需要重新研究和真机验证。

### 1.2 私人侧载可行，但必须保管同一签名密钥

HarmonyOS 4.3 的手动安装流程是：为文件管理等安装来源开启“允许安装应用”，必要时关闭纯净模式增强防护，在安装界面确认未经检测风险并完成密码验证。[华为：允许外部来源安装](https://consumer.huawei.com/cn/support/content/zh-cn00766399/)；[华为：使用安装包安装应用](https://consumer.huawei.com/cn/support/content/zh-cn01089223/)

APK 必须签名。Android 的更新模型会比较新旧版本证书；只有证书匹配才能覆盖更新。因此私人发行仍应创建长期 release keystore，离线备份，并让所有更新保持同一包名和签名。[Android：为应用签名](https://developer.android.com/studio/publish/app-signing)

不需要为第二版建设应用商店、账号或公众分发系统。安装向导只需要逐项检查本机所需的特殊访问，并给出进入系统设置的明确路径。

## 2. 能否可靠识别前台应用及使用时长

### 2.1 首选候选：使用情况访问

Android `UsageStatsManager` 的多数方法要求 `android.permission.PACKAGE_USAGE_STATS`。仅在清单声明不够，用户还必须通过系统设置授予使用情况访问；官方提供 `Settings.ACTION_USAGE_ACCESS_SETTINGS` 进入相应设置。[UsageStatsManager](https://developer.android.com/reference/android/app/usage/UsageStatsManager)

可用信息包括：

- `queryEvents(begin, end)` 返回一段时间内的使用事件；系统只保留几天的事件，且 Android R 起设备未解锁时返回 `null`。[UsageStatsManager.queryEvents](https://developer.android.com/reference/android/app/usage/UsageStatsManager#queryEvents(long,%20long))
- `ACTIVITY_RESUMED` 表示 Activity 进入前台，`ACTIVITY_PAUSED` 表示 Activity 进入后台；二者包含包名、类名和事件时间。API 29 之前对应的 `MOVE_TO_FOREGROUND` / `MOVE_TO_BACKGROUND` 已被替代。[UsageEvents.Event](https://developer.android.com/reference/android/app/usage/UsageEvents.Event)
- `queryUsageStats()` 和 `queryAndAggregateUsageStats()` 可以取得按包汇总的历史使用信息，更适合复盘摘要，不适合单独承担即时阻断。[UsageStatsManager](https://developer.android.com/reference/android/app/usage/UsageStatsManager)

**建议实现：**在监督承诺有效期间，用一个用户可见的前台服务低频查询最近事件，维护当前前台包和前台区间；原始事件只落包名、进入/离开时间和策略命中结果。不要读取通知、页面内容或输入内容。

**尚未确认：**公开资料没有给出 HarmonyOS 4.3 对这些 Android 事件的映射、批处理延迟、最小查询间隔和省电状态下的行为。因此不能仅凭 API 存在就写成“可靠实时监测”。

### 2.2 对照候选：无障碍窗口事件

Android 无障碍服务在系统绑定后可以接收 `AccessibilityEvent`；窗口状态事件带来源包名和事件时间。无障碍服务也能调用全局 Home/Back 动作，并创建 `TYPE_ACCESSIBILITY_OVERLAY` 窗口。[AccessibilityEvent](https://developer.android.com/reference/android/view/accessibility/AccessibilityEvent)；[AccessibilityService.performGlobalAction](https://developer.android.com/reference/android/accessibilityservice/AccessibilityService#performGlobalAction(int))；[TYPE_ACCESSIBILITY_OVERLAY](https://developer.android.com/reference/android/view/WindowManager.LayoutParams#TYPE_ACCESSIBILITY_OVERLAY)

如果只监听指定包的窗口变化，技术上不需要遍历界面文本；服务配置可以把 `packageNames` 限定为抖音、B站、小红书、微信，并保持 `canRetrieveWindowContent=false`。这是减少隐私面的建议，不代表平台保证事件不会缺失。

但是存在三项硬风险：

1. Android 官方把无障碍服务定义为帮助残障用户与应用交互，并明确要求平台级无障碍服务只用于这一目的。[Android 无障碍服务指南](https://developer.android.com/guide/topics/ui/accessibility/views/service)
2. 用户必须在“无障碍 > 已安装的服务/下载服务”手动打开服务，且可以随时关闭。
3. 华为说明 HarmonyOS 4.x 清后台或重启后，第三方无障碍服务可能被清理并显示为关闭，需要重新开启；后台保护只能降低风险，不能消除。[华为无障碍服务开关自动关闭](https://consumer.huawei.com/cn/support/content/zh-cn00410039/)

因此无障碍方案只能作为对照原型，用来回答“它是否显著提高检测和阻断成功率”；不能在技术验证前成为默认实现。

### 2.3 指定包可见性

Android 11 起，应用查询其他已安装应用会受到包可见性过滤。Jarvis 只需要四个明确目标，不应申请查看全部应用；应在 `<queries>` 中列出目标包名并在安装后核验实际包名。[Android：声明包可见性需求](https://developer.android.com/training/package-visibility/declaring)

抖音、B站、小红书和微信在这台手机上的实际包名、是否存在分身/应用市场渠道变体，不应凭记忆写死，必须通过真机安装清单确认。

## 3. 普通应用能否阻止指定应用打开

### 3.1 官方系统级能力不属于普通私人应用

Android `DevicePolicyManager.setPackagesSuspended()` 会让被暂停包无法启动 Activity、隐藏通知并从最近任务中消失，但调用方必须是设备所有者、资料所有者或拥有包管理委托的应用；普通已激活 Device Admin 不等于 Device Owner。[DevicePolicyManager](https://developer.android.com/reference/android/app/admin/DevicePolicyManager#setPackagesSuspended(android.content.ComponentName,%20java.lang.String%5B%5D,%20boolean))

Android 的锁定任务模式同样面向专用设备：只有设备策略控制器加入许可名单的应用才能运行。未列入许可名单的普通应用调用 `startLockTask()` 只会进入需要用户参与的屏幕固定，而不能按 Jarvis 的承诺时间动态禁用四个应用。[Android 锁定任务模式](https://developer.android.com/work/dpc/dedicated-devices/lock-task-mode)

普通 Device Admin 仍可在用户激活并声明 `force-lock` 后调用 `lockNow()` 锁定整台设备，但这用于丢失/被盗等紧急安全场景；它不能选择性封锁四个应用，而且会违背已经确认的“按应用屏蔽，不锁整机”。[DevicePolicyManager.lockNow](https://developer.android.com/reference/android/app/admin/DevicePolicyManager#lockNow())

华为 MDM Kit 有正式的应用运行黑名单接口，但官方要求企业开发者资质、MDM 证书/Profile、企业管理组件和激活流程。该能力不能作为私人侧载普通应用的依赖。[MDM Kit 开发指南](https://developer.huawei.com/consumer/cn/doc/doccenter-capabilities/mdm-kit-guide)；[applicationManager.addDisallowedRunningBundlesSync](https://developer.huawei.com/consumer/cn/doc/harmonyos-references-V14/js-apis-enterprise-applicationmanager-V14)

对 HarmonyOS 2.0 以上旧架构设备，华为另有 HEM/DPC 部署路线，但官方 codelab 明确要求未激活的企业级设备、企业开发者和 DPC License，并写明“不支持 BYOD（自带设备）场景”。当前已经作为个人手机使用的 Mate 70 Pro+ 不符合该路线。[华为 HEM Kit codelab](https://developer.huawei.com/consumer/cn/codelab/HMSHEMKit/)

### 3.2 华为系统自身能限制应用，但没有公开给普通应用的自动化接口

HarmonyOS 4.3 的“健康使用手机”支持应用限额和停用时间；达到限额或处于停用时段后应用图标变灰、无法使用，用户可以按系统流程申请延时。[华为：合理规划用机时长](https://consumer.huawei.com/cn/support/content/zh-cn16094834/)；[华为：应用图标变灰](https://consumer.huawei.com/cn/support/content/zh-cn00674149/)

这证明系统本身具备强限制能力，但官方消费者文档只说明用户在设置中手工配置；本次没有找到允许普通第三方应用按任意工作承诺动态改写这些限额的官方 API。这里的否定结论仅限已检索的公开官方资料，仍应向华为开发者支持确认一次。

### 3.3 可做的是“检测后覆盖/退回”，不是暂停包

普通应用可申请 `SYSTEM_ALERT_WINDOW`，用 `TYPE_APPLICATION_OVERLAY` 在其他 Activity 上方显示窗口；用户必须通过特殊权限页面显式授予。华为 HarmonyOS 4.3 也提供悬浮窗权限开关。[Android SYSTEM_ALERT_WINDOW](https://developer.android.com/reference/android/Manifest.permission#SYSTEM_ALERT_WINDOW)；[华为：管理应用访问权限](https://consumer.huawei.com/cn/support/content/zh-cn16077846/)

由此可组合出以下**工程推断方案**：

1. 监督策略有效时监听最近前台包；
2. 一旦发现目标包，立即显示全屏、不透明、可交互的 Jarvis 阻断层；
3. 阻断层展示当前承诺、结束时间和“填写原因后临时开放 5 分钟”；
4. 临时开放到期后重新监听并阻断；
5. 承诺到期必须以手机本地时间状态自动解除，不依赖电脑在线。

这不是无条件强制：应用悬浮窗位于普通 Activity 上方、关键系统窗口之下；用户仍可进入系统设置关闭悬浮权限、强制停止 Jarvis、解除后台权限或卸载应用。[WindowManager.LayoutParams](https://developer.android.com/reference/android/view/WindowManager.LayoutParams)

Android 10 起限制后台 Activity 启动；虽然获得悬浮窗权限是官方列出的例外之一，MVP 仍应直接使用 overlay，而不是依赖后台不断拉起普通 Activity。[Android 后台 Activity 启动限制](https://developer.android.com/guide/components/activities/secure-bal)

**产品表述要求：**技术验证通过前，开发手册只能写“目标设备上的尽力型应用阻断”；不能写“系统级禁用”“无法绕过”或“100% 锁定”。

## 4. 权限与特殊访问矩阵

| 能力 | 建议权限/机制 | 用户动作 | 结论与限制 |
|---|---|---|---|
| 局域网通信 | `INTERNET`、`ACCESS_NETWORK_STATE` | 安装时自动授予普通权限 | Android 官方说明二者是普通权限；传输仍必须使用 TLS。[连接网络](https://developer.android.com/develop/connectivity/network-ops/connecting) |
| 读取使用事件 | `PACKAGE_USAGE_STATS` + `ACTION_USAGE_ACCESS_SETTINGS` | 在特殊访问页面手动允许 | 仅声明清单无效；用户可撤销；HarmonyOS 4.3 行为需真机验证。[UsageStatsManager](https://developer.android.com/reference/android/app/usage/UsageStatsManager) |
| 全屏阻断层 | `SYSTEM_ALERT_WINDOW` / `TYPE_APPLICATION_OVERLAY` | 手动开启悬浮窗 | 不是系统级包暂停；关键系统窗口可在其上方；用户可关闭。[Manifest.permission.SYSTEM_ALERT_WINDOW](https://developer.android.com/reference/android/Manifest.permission#SYSTEM_ALERT_WINDOW)；[华为悬浮窗权限](https://consumer.huawei.com/cn/support/content/zh-cn16077846/) |
| 即时窗口事件对照 | `BIND_ACCESSIBILITY_SERVICE` 声明，由用户开启下载服务 | 手动开启无障碍服务 | 平台用途边界和华为保活风险高；默认不读取窗口内容。[无障碍服务指南](https://developer.android.com/guide/topics/ui/accessibility/views/service) |
| 可见持续工作 | Foreground Service + 常驻通知 | 用户能看到并可停止 | Android 把前台服务定义为用户可感知的持续任务；不是永久保活保证。[前台服务概览](https://developer.android.com/develop/background-work/services/fgs) |
| 精确开始/结束 | `AlarmManager`；必要时 `SCHEDULE_EXACT_ALARM` | 特殊访问由用户授予 | 只用于用户明确安排的精确承诺；权限可能被撤销。[Android 定时闹钟](https://developer.android.com/develop/background-work/services/alarms) |
| 重启恢复 | `RECEIVE_BOOT_COMPLETED` + 本地策略重建 | 无额外 UI，但应用至少启动过一次 | 系统重启会清空 alarm，需要收到开机广播后重建；受电池限制和华为后台策略影响。[重启后重建 alarm](https://developer.android.com/develop/background-work/services/alarms#boot) |
| Device Admin | 不纳入 MVP | 用户可激活设备管理员 | 只为 `lockNow()` 等整机安全能力，不提供普通应用所需的选择性暂停；增加高风险权限却不解决问题。[Device Admin 概览](https://developer.android.com/work/device-admin) |
| Device Owner / 华为 MDM | 明确排除 | 需要设备部署/企业授权 | 与个人普通安装边界冲突。[Android DPC](https://developer.android.com/work/dpc/build-dpc)；[华为 MDM](https://developer.huawei.com/consumer/cn/doc/doccenter-capabilities/mdm-kit-guide) |

安装引导必须逐项显示当前状态。任何关键权限失效时，手机和电脑都显示“手机监督不可用”，不得继续显示“正在完整监督”。

## 5. 后台常驻、自启动、电池优化与恢复

### 5.1 Android 基础限制

Android 8 起限制普通后台服务；用户可感知的持续工作应使用前台服务并显示通知。Android 12 起从后台启动前台服务也受到限制，只在用户操作、精确闹钟、开机广播、特定角色或已关闭电池优化等场景例外。[Android Services](https://developer.android.com/develop/background-work/services)；[后台启动前台服务限制](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start)

Android 13 起用户可以从系统任务管理器停止带前台服务的应用；受限电池状态还会停止前台服务、alarm 和 job，并可能延迟开机广播。[Android 13 后台行为](https://developer.android.com/about/versions/13/behavior-changes-all)；[后台优化](https://developer.android.com/topic/performance/background-optimization)

因此前台服务提高存活优先级，但不能把应用变成不可停止的守护进程。

### 5.2 HarmonyOS 4.3 必须执行的人工后台保护

华为对 HarmonyOS 4.x 给出的排查步骤包括：

- 在应用启动管理中关闭自动管理，允许自启动、关联启动和后台活动；
- 在电池优化中把 Jarvis 设置为“不允许优化”；
- 关闭省电模式，并按需要开启休眠时保持网络连接；
- 在最近任务中锁定 Jarvis 卡片，防止一键清理；
- 即便如此，华为仍说明应用长时间后台不活动时，受系统休眠和第三方省电策略影响，无法持续运行是普遍现象。[华为：应用无法后台运行](https://consumer.huawei.com/cn/support/content/zh-cn00428704/)

这些步骤应进入一次性的“手机监督准备检查”，但不能让用户每次承诺都重新设置。

### 5.3 建议恢复策略

1. 手机收到确认策略后先持久化，再回复 `policy_ack`；未落盘不算同步成功。
2. 策略包含绝对 `startAt` / `endAt` 和单调计时参考，任何恢复路径都不能延长 `endAt`。
3. 承诺开始和结束分别设置本地 alarm；开始后运行带持久通知的监督服务。
4. 开机广播只负责读取仍有效策略、重新设 alarm 并检查特殊访问状态；不在广播回调中做长期工作。
5. 无障碍或悬浮权限丢失、应用被限制、电池保护未配置时，本地写入 `supervision_unavailable`；电脑在线后同步。
6. Windows 端根据手机心跳显示“已连接”“手机离线但策略已确认”“手机监督不可用”三种事实状态，不能把离线等同失败，也不能把长期无心跳等同正常。

开机后无障碍服务能否自动恢复是明确真机验证项；华为官方案例表明它可能重新变为关闭状态，因此原型应预期出现需要用户重新开启的降级路径。[华为无障碍服务开关自动关闭](https://consumer.huawei.com/cn/support/content/zh-cn00410039/)

## 6. 同局域网与 Windows 配对、离线缓存

### 6.1 最小配对方案

不建设账号和云端。最简单的流程是：

1. Windows Jarvis 启动一个当前用户可见的本地 TLS 服务，并生成一次性配对二维码；
2. 二维码只包含局域网地址/端口、一次性配对码和服务证书指纹；
3. 手机扫码后要求 Windows 端再次确认同一个短码；
4. 双方生成设备密钥，手机私钥保存在 Android Keystore；后续请求使用 TLS 加应用层签名/重放防护；
5. 只允许一台已配对手机，重新配对会显式替换旧设备。

Android 官方要求网络权限并建议所有流量使用 SSL；Network Security Configuration 可为内部 CA 或固定证书配置自定义信任。Android Keystore 可使密钥材料保持不可导出。[Android 网络连接](https://developer.android.com/develop/connectivity/network-ops/connecting)；[Network Security Configuration](https://developer.android.com/privacy-and-security/security-config)；[Android Keystore](https://developer.android.com/privacy-and-security/keystore)

可选地用 Android NSD/DNS-SD 自动发现 Windows 服务；官方 NSD 能发现同一局域网的 HTTPS/TCP 服务。但为减少第一版权限和厂商兼容变量，二维码直连应作为基线，NSD 只作为便利增强。[Android NSD](https://developer.android.com/develop/connectivity/wifi/use-nsd)

### 6.2 最小同步协议

手机至少持久化以下记录：

- `paired_device`：电脑公钥/证书指纹、配对时间；
- `policy`：`commitmentId`、`version`、开始/结束时间、目标包集合、临时开放时长；
- `temporary_access`：申请时间、结束时间、原始原因；
- `event_outbox`：策略生效、阻断、临时开放、不可用、结束等未确认事件；
- `inbox_receipt`：最近命令 ID，用于幂等和防重放。

Room 是 Android 官方推荐的 SQLite 抽象，适合离线缓存结构化数据；数据库位于应用私有目录。配对密钥放 Keystore，不放数据库明文。[Android Room](https://developer.android.com/training/data-storage/room)；[应用私有存储](https://developer.android.com/training/data-storage/app-specific)

同步规则保持简单：

- 只接受版本更高且签名有效的同一承诺策略；
- 已确认策略在电脑离线后继续执行到原定结束；
- 离线时手机不能自行延长、加强或新增策略；
- 恢复连接后先上送 outbox，再拉取新版本；每条消息以 ID 幂等；
- 到期解除由手机本地执行，不能等待 Windows 的“结束”消息；
- 配对失效不删除仍有效的本地策略，但到期后不得再次启动新策略。

## 7. 公开资料无法确认、必须真机验证的事项

以下项目在测试完成前都必须标为“未知”，不能写成需求已可实现：

1. HarmonyOS 4.3.0 的底层 Android API level、`targetSdk` 兼容上限，以及 `UsageStatsManager` 各事件是否齐全。
2. 使用情况访问的真实设置路径、授权能否长期保留、锁屏/息屏时返回值和事件延迟。
3. 抖音、B站、小红书、微信的实际包名、应用分身包名，以及从桌面、通知、深链、最近任务启动时上报是否一致。
4. `TYPE_APPLICATION_OVERLAY` 能否在四个目标应用上稳定覆盖，是否出现可交互的时间窗口，手势导航、多窗口、横屏和画中画如何表现。
5. 无障碍事件在四个目标应用上的时延，以及 `TYPE_ACCESSIBILITY_OVERLAY` / Home 全局动作在 HarmonyOS 4.3 上的可用性。
6. 最近任务一键清理、单独上划、熄屏数小时、低电量/省电模式、内存压力后，监督服务和特殊访问是否仍有效。
7. 重启后 alarm、前台服务、使用情况访问、悬浮窗和无障碍服务分别如何恢复；尤其是无障碍开关是否再次关闭。
8. 外部签名 APK 是否被纯净模式或恶意应用检测拦截，后续用相同签名覆盖更新是否顺利。
9. Windows 防火墙、访客 Wi-Fi/AP 隔离、网络切换和休眠时，局域网连接能否恢复。
10. 手机系统时间被手动修改时，策略结束和临时开放时长如何表现；5 分钟临时开放应以单调时钟计时，承诺绝对结束仍需检测时间异常。
11. HarmonyOS 4.3 后续安全补丁是否改变后台/悬浮/无障碍行为；升级 HarmonyOS 5 后本方案默认视为不兼容。

## 8. 最小技术验证原型

原型目的不是开发完整手机 App，而是尽快回答唯一高风险问题：**这台手机上的普通侧载应用，能否在已确认承诺期间足够稳定地阻止四个目标应用被使用。**

### Spike 0：设备能力探针

- 输出系统版本、Android API level、厂商 build 信息和 Jarvis 包版本；不采集设备序列号、IMEI 或广告标识。
- 列出目标四个应用的实际包名和版本。
- 提供使用情况访问、悬浮窗、无障碍、通知、精确 alarm、后台活动/电池优化的状态检查按钮。
- 验证签名 APK 首装与同签名覆盖升级。

### Spike 1：前台识别对照

- 路径 A：`UsageStatsManager` 最近事件；记录事件类型、包名、事件时间、收到时间。
- 路径 B：仅开发构建启用无障碍窗口事件，不读取节点树；记录相同字段。
- 对四个目标应用和五个非目标应用执行桌面、通知、深链、最近任务切换，比较漏报、误报和延迟。
- 若路径 A 已满足阻断时延和稳定性，删除路径 B，避免依赖无障碍。

### Spike 2：阻断层

- 首选 `TYPE_APPLICATION_OVERLAY`：命中目标包后显示不透明全屏阻断页。
- 对照 `TYPE_ACCESSIBILITY_OVERLAY` + `GLOBAL_ACTION_HOME`，仅用于判断技术差距。
- 阻断页只提供承诺摘要、剩余时间和“填写原因后开放 5 分钟”；没有永久关闭入口。
- 临时开放使用单调计时，5 分钟到期后自动重新阻断。

### Spike 3：生命周期与离线

- Windows 通过二维码配对，发送一条 30 分钟测试策略；手机落盘并确认。
- 依次测试断开 Wi-Fi、Windows 关机、手机息屏、清最近任务、低电量、省电模式和手机重启。
- 验证策略不被延长、到期必定解除，事件在重连后按 ID 去重同步。
- 权限或服务失效时必须生成明确的“不可用”状态，不将其记为用户分心。

## 9. 原型验收清单

### 必须通过才能进入 V2-A

- [ ] 在目标 Mate 70 Pro+ / HarmonyOS 4.3.0 上完成签名 APK 手动安装和同签名升级。
- [ ] 能准确取得四个目标应用的包名，且只声明这些包的可见性，不申请全部应用清单。
- [ ] 使用情况访问路径在 100 次前台切换中无漏报目标应用；记录 P50/P95/最大事件延迟。
- [ ] 每个目标应用分别从桌面、通知、深链、最近任务启动至少 25 次；每次都在用户完成有效交互前显示阻断层。
- [ ] 阻断层不读取、截取或上传目标应用内容；事件日志只有包名、时间、策略命中和用户动作。
- [ ] 临时开放必须填写原始原因，只开放 5 分钟；到期后下一次打开目标应用会再次被阻断。
- [ ] Windows 断网或关机后，手机继续执行已确认策略；原定结束后 10 秒内解除，不等待电脑。
- [ ] 手机权限/后台服务失效时，界面和同步事件明确标为“手机监督不可用”，不显示完整监督成功。
- [ ] 8 小时压力测试覆盖息屏、网络切换和内存压力；记录后台被杀次数、事件漏报和额外耗电，不先虚构耗电门槛。
- [ ] 重启中断后的实际行为有明确产品处理：能恢复则恢复，不能恢复则在解锁后立即提示重新授权；不得静默失效。

### 触发“不可进入 V2-A”的失败条件

- 任一目标应用存在稳定可复现的绕过入口，而普通用户无需关闭 Jarvis 权限或强制停止即可继续使用；
- 使用事件经常延迟到已经能刷内容之后，且不使用无障碍就无法改善；
- 必须读取界面文本、截图、通知正文或输入内容才能识别目标应用；
- 必须 root、解锁 bootloader、企业 MDM、设备所有者部署或升级为整机锁定才能满足要求；
- 后台服务会在正常息屏/清理流程中频繁静默失效，无法可靠暴露“监督不可用”状态；
- 只能依靠 HarmonyOS 5 / NEXT 的 API，不能在当前 4.3.0 设备运行。

## 10. 对第二版开发手册的建议措辞

在真机 spike 通过前，手册应采用以下边界：

- **目标设备：**HUAWEI Mate 70 Pro+（PLA-AL10），HarmonyOS 4.3.0；私人签名 APK 手动安装。
- **手机监督：**只在用户提前创建并确认的监督承诺期间运行；默认阻断抖音、B站、小红书和手机微信。
- **阻断语义：**当前为目标设备上的尽力型前台检测与覆盖阻断，不声称系统级暂停、不可绕过或适配其他设备。
- **临时访问：**填写原因后开放 5 分钟；原因和时间进入复盘；原定承诺结束必定解除。
- **权限透明：**使用情况访问、悬浮窗、后台活动等权限逐项检查；任何失效都展示“手机监督不可用”。
- **隐私：**只保存包名和时间摘要，不采集目标应用内容、通知正文、聊天文本、截图、录屏或键盘输入。
- **明确非目标：**不 root、不解锁 bootloader、不做设备所有者/企业 MDM、不锁整机、不用无障碍读取界面内容、不承诺 HarmonyOS 5 兼容。

如果 `UsageStatsManager + TYPE_APPLICATION_OVERLAY` 在真机上通过验收，它应成为 V2-A 最小实现。只有当它失败、且无障碍对照原型能稳定解决问题时，才重新讨论是否接受无障碍用途边界和维护风险；不要在验证前同时实现两套长期方案。
