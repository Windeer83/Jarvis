# 六款执行监督与 AI 陪伴产品深度调研

> 调研日期：2026-08-20。范围：PlanCoach、Focus Bear、Forfeit / Overlord、Focusmate、Tiimo、Project AIRI。本文只使用官网、帮助中心、隐私政策、应用商店、官方 GitHub/API、官方发布说明和平台奖项等一手来源。应用商店评分、下载档位、GitHub Star 与发行包下载量只是当日热度代理，不等于活跃用户；厂商自报用户量和成功率均明确标作厂商口径。

## 结论先行

六款产品并不是六个同质竞品，而是覆盖了执行困难的六段不同机制：

1. **PlanCoach 把模糊任务压缩成“现在只做这一小步”**，强项是启动与逐步执行，不是自动监督。
2. **Focus Bear 把工作意图转成应用/网站访问约束**，最接近 Jarvis 的电脑活动分类与即时干预，但它的严格阻断和 AI 网页判断比 Jarvis 当前边界更激进。
3. **Forfeit 把承诺、证据、判定、申诉、后果锁成正式合约**，治理闭环最完整；同时也展示了金钱惩罚、持续定位、健康数据、照片视频和联系人升级会带来的巨大伦理与隐私成本。
4. **Focusmate 用最少机制制造稳定的社会在场感**：预约、向真人说出目标、共同工作、结束汇报。它不判断用户工作内容，却能提高“有人在等我”的承诺强度。
5. **Tiimo 把时间和下一步变得可见，并允许计划温和重排**，强项是降低执行功能负担，不是识别偏离。
6. **Project AIRI 把角色身体、语音、模型提供商、聊天和扩展能力拆成可替换模块**，适合作为 Jarvis 陪伴表现层的技术参照，但它没有工作承诺、证据、提醒升级或申诉闭环。

对 Jarvis 第二版最重要的不是增加六套功能，而是形成一个可解释的闭环：

> **把承诺具体化 → 让当前一步持续可见 → 只用经授权的非内容证据判断 → 不确定先问 → 分级提醒 → 用户可纠正/修订/退出 → 结束时不自动宣判完成 → 用回顾改进下一次。**

## 1. 统一评估框架

| 维度 | 核心问题 | 对 Jarvis 的判定标准 |
|---|---|---|
| 目标用户与阻力 | 产品主要解决启动困难、持续注意、时间盲、孤独工作，还是后果不足？ | 必须说明针对哪一种阻力，不能用同一提醒强度覆盖所有情况。 |
| 价值主张 | 用户付出的设置、权限与隐私成本，换来什么确定收益？ | 每一项权限和干预都应对应一个明确、可验证的用户价值。 |
| 首次使用闭环 | 从安装到第一次“得到帮助”需要几步？是否先付费、先授权或先配置模型？ | 第一价值时刻应在低权限、低配置下出现；高级能力渐进授权。 |
| 执行闭环 | 任务如何创建、拆解、开始、进行、完成、失败和复盘？ | 计划、监督、结果和回顾必须是可区分状态。 |
| 监督与证据 | 产品观察什么：自报、摄像头、照片、位置、健康、前台应用、网页标题，还是同伴在场？ | 证据最小化；内容与非内容信号分层；每个判定可解释。 |
| 提醒升级 | 提醒是否按时间、风险、回应和上下文升级？ | 升级有上限、冷却、静音与公共场合边界，不能演化为轰炸。 |
| 误判、申诉与退出 | 用户能否纠正错误、暂停、修改规则、申诉后果或安全退出？ | 纠正不抹历史；重大规则变更需确认；任何强约束都必须有紧急出口。 |
| 数据与隐私 | 收集什么、发给谁、保存多久、能否导出/删除？ | 默认本地、目的限定、可查看、可撤销；遥测不得复制工作内容。 |
| 商业与使用信号 | 平台、价格、评分、下载量、Star、厂商活动量是什么？ | 将“代表性”与“真实活跃使用”分开，不把宣传值写成事实。 |
| 可借鉴与禁区 | 哪个机制能增强 Jarvis，哪个机制会破坏产品原则？ | 与“确定性监督核心、非内容活动证据、无负担陪伴、用户控制权”一致。 |

## 2. 单产品深研

### 2.1 PlanCoach：把“想做”压缩成第一步

**目标用户与价值主张。** 产品明确面向 ADHD、拖延和启动困难用户，主张“杀死拖延的是极致的具体”，用 AI 把模糊任务拆成可立即执行的步骤。官网把主要阻力归纳为任务模糊、完美主义、即时满足偏好、自我否定和情绪逃避，并用不同教练风格应对不同阻力。[官网](https://plancoach.freemindworkshop.com/) · [官方资料页](https://plancoach.freemindworkshop.com/resources/)

**从首次使用到完成/失败的闭环。** 用户输入或口述任务，选择教练，AI 生成步骤和建议；用户可以二次修改，再进入导航模式。导航模式用大字常显、渐进式披露和“偏航修正”只呈现当前步骤；免手模式会朗读当前步骤，用户说“下一步”或“继续”即可标记完成并推进。完成任务获得咖啡豆、等级和成就，也可写“感想”，后续计划可参考近期相关计划与感想。路线库与世界书把有效步骤沉淀为个人 SOP。[官网功能说明](https://plancoach.freemindworkshop.com/) · [免手模式说明](https://plancoach.freemindworkshop.com/blog/2026/03/26/hands-free-mode/) · [App Store 版本记录](https://apps.apple.com/cn/app/plancoach-%E8%AE%A1%E5%88%92%E6%95%99%E7%BB%83-%E5%B0%8F%E7%BA%A2%E4%B9%A6%E6%8A%96%E9%9F%B3%E7%88%86%E6%AC%BE%E6%8A%97%E6%8B%96%E5%BB%B6app/id6748287561)

**具体交互。** 除逐步导航外，产品有语音输入、步骤计时、聚光当前步骤、灵动岛/小组件/Apple Watch、灵感快速捕捉、历史筛选、完成步骤同步日历、行动锦囊、教练 DLC 与自定义教练。官方“连招”把捕捉灵感—拆解计划—专注执行，以及执行—写感想—教练整合定义为闭环。[官方资料页](https://plancoach.freemindworkshop.com/resources/) · [App Store](https://apps.apple.com/cn/app/plancoach-%E8%AE%A1%E5%88%92%E6%95%99%E7%BB%83-%E5%B0%8F%E7%BA%A2%E4%B9%A6%E6%8A%96%E9%9F%B3%E7%88%86%E6%AC%BE%E6%8A%97%E6%8B%96%E5%BB%B6app/id6748287561)

**监督、提醒与失败治理。** 监督信号主要是用户主动推进步骤、语音口令、计时和自报感想；官网所称“贴脸提醒”是在任务期间持续提醒，但具体触发条件、频率上限、静音策略和偏航判断信号均**未核验**。没有找到它读取前台应用、网页、键鼠或屏幕内容来自动验证工作的官方证据。步骤跳过、计划归档/重开在版本记录中存在，但“失败”如何建模、误判如何申诉、奖励是否因未完成受损，均**未核验**。

**数据、平台、定价与信号。** 官网称支持数据导出与 iCloud 全量同步；App Store 开发者申报为“不收集数据”，但 Apple 明确提示该申报未经验证。App Store 所链接的 Notion 隐私政策在调研时返回 404，因此任务文本发往哪个 AI 服务、保留多久、语音和画像怎样处理均**未核验**。当前支持 iPhone、iPad、Apple Watch，Apple Silicon Mac 可运行；中国区商店显示 826 个评分、4.8，内购包括核心功能终身 ¥48、高级版终身 ¥128、月度 ¥12、季度 ¥22、年度 ¥128。官网“用户数量超 6 位数”只作为厂商口径。[App Store](https://apps.apple.com/cn/app/plancoach-%E8%AE%A1%E5%88%92%E6%95%99%E7%BB%83-%E5%B0%8F%E7%BA%A2%E4%B9%A6%E6%8A%96%E9%9F%B3%E7%88%86%E6%AC%BE%E6%8A%97%E6%8B%96%E5%BB%B6app/id6748287561) · [官网](https://plancoach.freemindworkshop.com/)

**优势、短板与 Jarvis 取舍。**

- 可借鉴：生成后先让用户修改确认；只显示“当前一步”；语音推进；把一次有效拆解沉淀为可复用模板；结束时保留原始感想。
- 不应照搬：用角色语气替代监督规则；把完成奖励做成需要维持的货币/等级；在没有透明频率和退出机制时“持续催命”；让 AI 拆解直接成为正式承诺。
- 结论：Jarvis 应新增“可选执行步骤”，但步骤属于承诺的辅助计划，不应把每步勾选等同于履约证据。

### 2.2 Focus Bear：把工作意图变成跨设备访问边界

**目标用户与价值主张。** Focus Bear 由 AuDHD 团队面向 AuDHD 及需要专注支持的人设计，组合晨晚例程、专注时段、跨设备应用/网站阻断、微休息和迟到提醒。官网强调一次设置例程后逐步引导，每次只显示一步；阻断覆盖 Windows、macOS、iOS、Android。[官网](https://www.focusbear.io/) · [Google Play](https://play.google.com/store/apps/details?id=com.focusbear)

**从首次使用到完成/失败的闭环。** 用户先配置晨晚例程与微休息活动（时长、星期、必做/可选、说明、清单和图片），再配置工作时间、Focus Mode / Block List 和严格度；开始专注或到达自动阻断时段后，系统允许/阻断应用与网站并插入微休息。Focus Block 结束时复盘原意图、完成项和干扰，可休息或继续下一块，并决定是否永久放行本次临时允许的网站。例程按单步引导；用户可跳过、推迟或暂停。自动阻断支持 Chill（立即暂停）、Wait for It（等待 15 分钟）和 Iron Focus（不能切换列表；可要求伙伴密码）等不同摩擦等级。[习惯设置](https://support.focusbear.io/portal/en/kb/articles/how-to-edit-habits-in-focus-bear) · [阻断时段说明](https://support.focusbear.io/portal/en/kb/articles/blocking-schedule) · [结束复盘](https://support.focusbear.io/portal/en/kb/articles/relax-block-nothing) · [关闭/暂停流程](https://support.focusbear.io/portal/en/kb/articles/how-to-turn-off-focus-bear)

**监督与证据信号。** 常规阻断读取当前应用或 URL；官方说明不记录按键、不截图、不保存浏览历史。启用 Pro 的 AI Blocking 时，发送给 OpenAI 的字段包括当前 focus mode、任务意图、URL、页面标题和网页正文前 100 个字符；晨间激励还会发送 routine streak 和晨间活动名称。Android 使用 AccessibilityService 检测前台应用并实时阻断，商店声明只读包名、不读文字、点击或屏幕内容。[AI 隐私政策](https://www.focusbear.io/ai-privacy-policy) · [阻断时段说明](https://support.focusbear.io/portal/en/kb/articles/blocking-schedule) · [Google Play](https://play.google.com/store/apps/details?id=com.focusbear)

**提醒升级、误判与退出。** “Late No More”先视觉提醒、后升级为语音；文字提醒从事件前 2 分钟开始，并随临近程度改变紧迫文案，用户也可选择“不参加”终止提醒，或进入会议并在结束后复盘。语音的精确触发时间、重复上限及完全不响应时的终止条件仍**未核验**。阻断误判时可手工调整 Block List、当场放行、结束后永久白名单，或关闭 AI Blocking 后重启专注时段；支持紧急出口。完全停用会先给 2 秒反思，再要求选择原因；部分原因立即恢复访问并取消未来 4 小时习惯。其误判治理仍主要是改配置或停用，不是带版本和纠正记录的正式申诉。[文字提醒配置](https://support.focusbear.io/portal/en/kb/articles/configuring-text-alerts) · [参会闭环](https://support.focusbear.io/portal/en/kb/articles/joining-a-meeting) · [自定义 Block List](https://support.focusbear.io/portal/en/kb/articles/customizing-your-block-list-in-focus-bear) · [关闭流程](https://support.focusbear.io/portal/en/kb/articles/how-to-turn-off-focus-bear)

**数据、平台、定价与信号。** 隐私政策列出账号、习惯、可选职业/ADHD 调查、订阅、产品使用与网站分析数据；习惯数据声称“双重加密”，但未公开算法和密钥管理。服务商包括 Auth0、AWS、Cloudflare、OpenAI、PostHog、UXCam、Stripe、Paddle 和 RevenueCat 等；可请求访问、纠正、删除、限制、可携与反对自动决策。免费层限制 25 分钟专注、5 个网站/应用和每例程 3 个习惯；Pro 价格页主口径为 $9.99/月、7 天免卡试用，但同页和条款还残留 $4.99 / $10 的不同口径，应以下单页为准。支持 macOS、Windows、iOS、Android 并跨设备同步；Google Play 当日为 10K+ 下载、约 90 个评分、3.4，官网“20,000+ focused brains”仅作厂商口径。[定价](https://www.focusbear.io/pricing) · [隐私政策](https://www.focusbear.io/privacy-policy) · [App Store](https://apps.apple.com/us/app/focus-bear-built-for-audhders/id1673296334) · [Google Play](https://play.google.com/store/apps/details?id=com.focusbear)

**优势、短板与 Jarvis 取舍。**

- 可借鉴：工作承诺关联独立活动分类规则；相关性误判可当场纠正；严格度由用户预先选择；紧急出口带摩擦但不羞辱；会议/通话时静默。
- 不应照搬：默认强阻断；把网页标题和内容预览送给 AI；伙伴密码让第三方掌握退出权；把“阻断成功”直接当作任务完成。
- 结论：Jarvis 当前“三态分类 + 不确定先问 + 非内容活动证据”比 Focus Bear 更适合第一版；第二版可借鉴它的按承诺规则集、误判快速修正和可配置退出摩擦。

### 2.3 Forfeit / Overlord：证据、申诉与后果的完整合约

**目标用户与价值主张。** 用户先声明做什么、何时完成、用什么验证、失败触发什么后果；缺少证据或验证失败就扣款、通知联系人、阻断应用或锁 Mac。Overlord 是其 24/7 AI accountability 层，可结合日历、健康、位置、Screen Time、Mac 使用和外部集成主动提醒和执行动作。[官网](https://www.forfeit.app/) · [Overlord 官网](https://www.overlord.app/)

**从首次使用到完成/失败的闭环。** 创建 Commitment 时选择截止时间、重复规则、证据类型、金额/其他后果和 leniency；规则启动后锁定，不能在临近失败时软化。用户按时提交证据，AI 或真人判定通过；无证据或不通过则触发后果。Forfeit 的宽松度可允许提交说明/证明，Hard 模式可禁止申诉；Overlord 则把 skip/excuse、改目标、反转失败/退款和删目标都建模为 appeal，能预设“提前 24 小时”“不退款”“全部接受”等规则并上锁，AI 拒绝退款后还可升级人工。服务条款另授权同一承诺连续未验证时最多连续扣三次，体现产品文档与法律文本需要同时阅读。[Forfeit 官网](https://www.forfeit.app/) · [Overlord 文档](https://overlord.app/docs/) · [服务条款](https://www.forfeit.app/terms-and-conditions) · [App Store](https://apps.apple.com/us/app/forfeit-habit-contracts/id1633125787)

**监督与证据信号。** Forfeit 的证据包括现场照片、视频/延时摄影、GPS 到达/避开、Apple Health、Screen Time、Mac/RescueTime 活动、朋友验证、自我验证和第三方服务。Overlord 进一步接入 Android 前台 App、Google Fit、日历、位置、Shortcuts、IFTTT、Webhook、聊天和语音。照片可由 AI 检查，视频/延时摄影可由真人检查；官网称媒体验证后删除，Mac 原始活动留在本机、服务端只接收 pass/fail，但 Overlord 文档又支持查询历史 Mac screen time、browser history 和 coding activity，原始记录、本地派生数据与云端摘要的边界没有解释清楚。Android 隐私说明称前台应用监测只读 package name，不读取文字、键盘、截图或页面内容。[Forfeit 官网](https://www.forfeit.app/) · [Overlord 文档](https://overlord.app/docs/) · [Overlord 隐私政策](https://overlord.app/privacy.html)

**提醒升级、误判与退出。** Overlord 没有单一默认升级链，而允许按目标组合推送/浏览器提醒、反复消息、电话、通知伙伴、App/网站/Mac 阻断以及即时或递增扣款。证据判错可重新分析；失败可申诉，申诉难度在规则成立前选择，反转失败会退回相关扣款，AI 拒绝退款还可转人工，但文档称只处理 14 天内收费。部分模式可完全关闭申诉；goal 活跃时也可能不能直接删除，需先在设备端禁用或等到期。支付方式可移除以关闭金钱后果，但其本身也可上锁。紧急退出、删除账号与已授权扣款的最终关系仍受服务条款和公司裁量约束。[Overlord 文档](https://overlord.app/docs/) · [服务条款](https://www.forfeit.app/terms-and-conditions)

**数据、平台、定价与信号。** 隐私政策覆盖账户、财务、证据照片视频与文字、连续/会话位置、日历、设备权限和健康数据等；Health Connect 默认用于目标验证，云同步可选且声称端到端加密，关闭后同步指标 24 小时内删除、备份最长 30 天清除。Overlord 还会保存目标历史、完整聊天、偏好、证据模式、申诉历史与 memory notes；用户可查看、编辑和删除这些 notes。支付由 Stripe 处理，产品称不保存卡信息。iOS 隐私标签同时列出可能用于跨 App/网站追踪的联系方式、用户内容与使用数据，隐私成本显著高于其他五款。Forfeit 支持 iOS/Android 与 Apple Silicon Mac；Overlord 文档另列 Web、Mac、iMessage、WhatsApp 与 Telegram。App Store 内购显示 Premium $7.99、Pro / Overlord $12.99；Google Play 当日 10K+ 下载、约 342 个评分、4.6，App Store 332 个评分、4.7。官网更大的累计金额/目标数和商店的 20K+ 用户、94% 成功率均为厂商自报。[Overlord 隐私政策](https://overlord.app/privacy.html) · [Overlord 文档](https://overlord.app/docs/) · [Google Play](https://play.google.com/store/apps/details?id=app.forfeit.forfeit) · [App Store](https://apps.apple.com/us/app/forfeit-habit-contracts/id1633125787)

**优势、短板与 Jarvis 取舍。**

- 可借鉴：承诺成立前锁定证据规则；不同目标匹配不同证据；判定记录与后果分离；纠错/申诉是一级对象；规则修改保留版本。
- 不应照搬：金钱惩罚、羞辱式联系人升级、持续定位、健康/银行数据、照片视频监督、锁机、让 AI 自主决定后果，以及无申诉模式。
- 结论：Jarvis 应借鉴的是“治理结构”，不是惩罚强度。活动分类纠正应像申诉一样可追踪，但后果只限提醒、休息、修订和回顾。

### 2.4 Focusmate：真人社会在场感的最小闭环

**目标用户与价值主张。** Focusmate 面向任何需要启动和持续工作的人，以真人一对一 body doubling 替代内容监督。用户预约 25/50/75 分钟场次或使用 Focus Now，系统匹配另一位也在工作的成员。[官网](https://www.focusmate.com/) · [使用流程](https://www.focusmate.com/how-it-works/)

**从首次使用到完成/失败的闭环。** 选择 25/50/75 分钟时段和 Desk/Moving/Anything、Quiet Mode、Anyone/Prefer Favorites 等偏好，预约后收到确认和日历邀请；最多提前 10 分钟进入视频。开场互相说出本次目标，可在聊天中写下；过程中保持摄像头开启、双方安静工作；结束时互报进展并庆祝。若 partner 爽约，开始 1 分钟后系统尝试 rematch；仍无人可用则退出并另约。它不验证成果真实性，核心证据是“按时出现、保持可见、结束汇报”。[预约说明](https://support.focusmate.com/en/articles/5577752-how-do-i-book-a-focusmate-session) · [新手指南](https://support.focusmate.com/en/articles/9110188-getting-started) · [爽约处理](https://support.focusmate.com/en/articles/4044431-what-if-i-don-t-get-a-match-or-my-partner-doesn-t-show)

**监督、提醒与失败治理。** 系统自动追踪迟到/缺席，社区规则要求避免临时取消、全程在场、沟通变动。没有自动读取用户应用、网站或工作内容；用户可自愿屏幕共享增强问责。预约有确认/日历邀请，桌面浏览器可在场次前播放提示音和 banner；当前没有移动端通知，也没有逐级增强或联系人升级。失败更多表现为迟到、缺席、提前离开或没有达到自报目标，不会自动判定“任务失败”。不适/不安全时可 snooze、block 或 report；公开资料未披露爽约阈值、匹配降权、attendance 暂停/申诉或封禁分级的具体算法，也没有单场“目标没完成”的申诉流程，因为平台本身不裁决成果。[社区规则](https://support.focusmate.com/en/articles/4044467-the-five-community-rules) · [桌面提醒](https://support.focusmate.com/en/articles/6210023-desktop-notifications) · [屏蔽用户](https://support.focusmate.com/en/articles/4044457-how-to-block-a-user-on-focusmate) · [Snooze](https://support.focusmate.com/en/articles/5950429-snooze)

**数据、平台、定价与信号。** 浏览器原生视频覆盖桌面与移动端，无需独立 App；视频由 Daily.co 提供且场次不录制。平台收集账户、资料、交易、聊天/互动、设备和使用分析数据；可在设置删除账号，官方称按 GDPR 删除数据。免费层每周 3 场；Plus 为 $8/月（年付）或 $12/月（月付），无限场次。官网当日显示 12M+ 完成场次、500M+ 专注分钟、覆盖 150+ 国家，均为厂商活动量口径而非用户数。[安全与合规](https://support.focusmate.com/en/articles/4371101-security-compliance) · [隐私政策](https://www.focusmate.com/privacy/) · [定价](https://www.focusmate.com/pricing/) · [官网活动量](https://www.focusmate.com/)

**优势、短板与 Jarvis 取舍。**

- 可借鉴：开场目标陈述和结束汇报；稳定、安静的在场感；无需监视内容；把“不完成”视为可汇报结果而不是道德失败。
- 不应照搬：默认摄像头常开、真人匹配与社区治理成本；把桌面角色伪装成人类；用社交压力强迫用户保持在线。
- 结论：Jarvis 可增加可选的“开场一句承诺 + 结束一句汇报”和安静 body-double 动画，但必须明确角色是 AI，且忽略角色不会产生关系惩罚。

### 2.5 Tiimo：视觉时间、柔性计划与温和重排

**目标用户与价值主张。** Tiimo 由神经多样性团队为 ADHD、自闭谱系及任何需要执行功能支持的人设计，组合视觉日程、To-do mental inbox、AI Co-Planner、Focus Timer、部件和温和提醒。Apple 将其评为 2025 iPhone App of the Year，理由包括视觉规划和把愿望转成可行动下一步的 AI。[官网](https://www.tiimoapp.com/) · [Apple 获奖公告](https://www.apple.com/newsroom/2025/12/apple-unveils-the-winners-of-the-2025-app-store-awards/)

**从首次使用到完成/失败的闭环。** 用户可先把想法扔进未排期 To-do，也可手动或通过 Co-Planner 说/写一段混乱想法；AI 给出任务、时间、图标、标签、优先级、预计时长和步骤，用户调整后保存。任务可固定时间或设为 Anytime；固定任务开始时 Focus Timer 自动启动，用户可暂停、续播、加一分钟、切换任务或提前拖到终点完成。日末 Review Today 允许选择未完成任务并移到新日期，再记录心情；周日可做 Review your week。[任务指南](https://www.tiimoapp.com/faq/manage-tasks) · [Focus Timer](https://www.tiimoapp.com/faq/focus-timer) · [通知与重排](https://www.tiimoapp.com/faq/notifications)

**监督、提醒与失败治理。** 产品不读取当前应用/网页，也不自动判断用户是否在做任务；执行信号是计时器和用户完成/暂停/切换操作。通知可选晨间概览、时间提醒、激励提醒、日回顾和周回顾，可选择声音并全部关闭；官方建议出现通知疲劳时只保留一两个或暂停。Live Activities、Dynamic Island、锁屏/主屏/Watch 小组件让当前任务持续可见。未完成任务可以重排，不把漏做自动标记为失败，也不存在活动误判申诉。[通知指南](https://www.tiimoapp.com/faq/notifications) · [Widgets / Live Activities](https://www.tiimoapp.com/faq/widgets) · [自定义](https://www.tiimoapp.com/faq/customize-and-add-profiles)

**数据、平台、定价与信号。** 隐私政策列出姓名、邮箱、订阅/支付、设备、IP、大致位置和产品使用数据，并与支付、托管、IT 和分析服务商共享；取消后数据最多保留两年，可请求访问、纠正、删除、限制和可携。美国 App Store 隐私标签还列出跨 App/网站追踪用标识符，以及与身份关联的联系方式、用户内容、标识符和使用数据。支持 iOS/iPadOS/watchOS、Android 和 Web；免费版有基本计划、有限 AI 与一个 profile，Pro 提供完整功能，年订阅有 7 天试用，网页端需订阅。美国 App Store 当日 18K 评分、4.6；Google Play 50K+ 下载、1.92K 评分、4.6。官网“50 万+使用者”和商店“300 万下载/100 万用户”是厂商口径。[隐私政策](https://www.tiimoapp.com/privacy-policy) · [FAQ 与订阅](https://www.tiimoapp.com/faq) · [App Store](https://apps.apple.com/us/app/tiimo-to-do-list-ai-planner/id1480220328) · [Google Play](https://play.google.com/store/apps/details?id=com.tiimo.androidappreactnative)

**优势、短板与 Jarvis 取舍。**

- 可借鉴：计划草稿必须由用户调整后保存；固定时间与 Anytime 分开；当前步骤跨界面持续可见；漏做后提供批量重排而非惩罚；通知可组合且主动防疲劳。
- 不应照搬：把任意 To-do 都升级为正式承诺；将计时器自动启动视为用户已经开始；让 AI 直接大范围重排已确认承诺；为了同步默认收集较多账号与使用数据。
- 结论：Jarvis 第二版应补“未承诺的想法/候选计划”和“已确认工作承诺”之间的显式边界，并为承诺结束后的未完成项提供修订建议而不是自动续期。

### 2.6 Project AIRI：可替换的角色、语音与模型容器

**目标用户与价值主张。** AIRI 是受 Neuro-sama 启发的开源数字伙伴，强调 self-hosted、user-owned 和可替换灵魂/身体；当前最容易使用的是 Web 与桌面端。桌面端基于 Electron，可让 Live2D/VRM 常驻桌面，提供托盘、窗口穿透、悬停淡化、本地模型接入和插件实验。[官方概览](https://airi.moeru.ai/docs/zh-Hans/docs/overview/) · [GitHub](https://github.com/moeru-ai/airi)

**从首次使用到完成/失败的闭环。** 首次引导选择语言，配置聊天提供商/API Key 或登录官方服务，再选模型；聊天成功后可独立添加 TTS、ASR/STT、视觉和网络搜索。角色卡定义名称、性格与行为，并可为不同身体模块选择不同提供商；用户可换 Live2D/VRM、文本或语音对话、手动停止发声。它的闭环是“配置能力—与角色对话—调整模块”，不是执行监督闭环。[桌面说明书](https://airi.moeru.ai/docs/zh-Hans/docs/manual/tamagotchi/setup-and-use/) · [服务商配置](https://airi.moeru.ai/docs/zh-Hans/docs/manual/config/) · [语音配置](https://airi.moeru.ai/docs/zh-Hans/docs/manual/config/audio)

**监督、提醒、误判与退出。** AIRI 没有正式工作承诺、活动分类、提醒升级、履约判定或申诉模型，这些均**未核验/不属于当前核心产品**。桌面开发者工具可以读取屏幕、麦克风和插件上下文，但官方要求仅为明确目的开启，测试后关闭，并警告不要公开含 API Key、聊天、屏幕或 WebSocket 数据的截图。数据设置支持导入/导出或删除聊天、清除模型、模块偏好、凭据和本地设置；用户也可关闭使用数据和崩溃报告。[开发者工具](https://airi.moeru.ai/docs/zh-Hans/docs/contributing/desktop-developer-tools) · [桌面说明书](https://airi.moeru.ai/docs/en/docs/manual/tamagotchi/setup-and-use/)

**数据、平台、定价与信号。** 隐私政策说明可能收集 IP、页面/使用时长、OS、大致位置、邮箱、用户 ID、配置的模型提供商名称（不含密钥）、Agent/MCP/工具调用、AI trace 与错误，并使用 PostHog；可卸载停止收集，删除服务端用户数据需邮件联系。API Key 应只保存在设备中。开源代码为 MIT；外部模型、语音服务和官方商业服务可能产生费用，统一当前价格**未核验**。2026-08-20 GitHub API 为 48,104 Stars、4,765 Forks；最新正式版 v0.11.3 提供 Windows、macOS、Linux、Android 与需手动签名的 iOS 包，但官方概览仍把移动端标为开发中、建议手机优先 Web，因此不能把发行资产等同于成熟平台支持。最新 Windows 安装包约 673 MB，发布资产下载量只作热度代理。[隐私政策](https://airi.moeru.ai/docs/zh-Hans/about/privacy) · [GitHub API](https://api.github.com/repos/moeru-ai/airi) · [v0.11.3 Release API](https://api.github.com/repos/moeru-ai/airi/releases/latest) · [官方概览](https://airi.moeru.ai/docs/zh-Hans/docs/overview/)

**优势、短板与 Jarvis 取舍。**

- 可借鉴：角色卡、身体、语音、模型和工具解耦；TTS 与 STT 可独立关闭；提供本地语音/模型选项；数据清理入口按类别拆分；遥测征求同意。
- 不应照搬：Electron + Live2D/VRM 的重资源栈；把角色提示词当业务规则；默认扩大到视觉、工具和屏幕上下文；把自托管等同于“不会出站”。
- 结论：Jarvis 当前 Core/Desktop 两进程、确定性监督核心与轻量 2D 素材方向应保留；第二版可把 `CompanionRenderer`、`VoiceIO`、`ModelProvider`、`SupervisionCore` 的接口边界写得像 AIRI 的机体模块一样清楚，但不引入其完整渲染栈。

## 3. 横向对比矩阵

| 维度 | PlanCoach | Focus Bear | Forfeit / Overlord | Focusmate | Tiimo | Project AIRI |
|---|---|---|---|---|---|---|
| 主要阻力 | 不知从哪开始 | 数字分心、例程中断 | 后果不足、可糊弄 | 独自工作、难启动 | 时间盲、计划过载 | 数字陪伴与角色表达 |
| 正式承诺 | 弱：计划/步骤 | 中：Focus Mode/时段 | **强：规则锁定的合约** | 中：预约场次和口头目标 | 弱：任务/日程 | 无 |
| 计划拆解 | **强：多教练、逐步导航** | 弱 | 中：目标规则配置 | 无 | **强：AI 步骤、估时、重排** | 无业务拆解 |
| 监督证据 | 用户勾选/语音/计时 | 前台 App、URL；可选标题与内容预览 | **照片、视频、位置、健康、屏幕时长、Mac、人工** | 真人在场、摄像头、口头汇报 | 用户操作与计时 | 对话/模块上下文，不是履约证据 |
| 自动判定 | 低 | 中：允许/阻断相关性 | **高：pass/fail + 后果** | 仅迟到/缺席 | 低 | 无 |
| 提醒升级 | 声称持续提醒，细节未核验 | 视觉→语音；阻断严格度 | **提醒→阻断/电话/联系人/扣款/锁机** | 日历/场次预期 | 可选温和提醒、日/周回顾 | 无监督升级 |
| 误判/申诉 | 未核验 | 改列表、停 AI、break-glass | **预设 leniency + 真人申诉** | block/report 处理人际安全 | 不自动判偏离，无申诉 | 数据清理/关闭模块；无申诉 |
| 失败定义 | 未核验 | 被阻断/未完成例程 | 未提交或证据不通过 | 未出现/未保持在场/自报未完成 | 漏做可重排 | 无任务失败 |
| 内容隐私成本 | AI 任务/语音去向未核验 | 可选标题/预览送 AI | **最高** | 摄像头常开但不录制 | 账号、任务、使用与分析数据 | 取决于提供商/模块/遥测 |
| 社会在场感 | 角色教练 + 语音 | 轻教练 | 强硬 AI 监督者 | **真人最强** | 低压力助手 | **角色表现最强** |
| 最值得借鉴 | 当前一步 | 按意图判断、规则集与退出摩擦 | 证据—判定—申诉治理 | 开场承诺/结束汇报 | 柔性重排与通知防疲劳 | 模块化陪伴层 |
| 最大禁区 | 催命式提醒、养成义务 | 强阻断和内容 AI | 金钱/羞辱/持续监视 | 摄像头与社区成本 | To-do 与承诺混淆 | 重型渲染和权限膨胀 |

## 4. 对 Jarvis 第二版开发手册的明确修改建议

以下是研究结论，不直接修改现有规格；进入开发手册前仍应由用户确认优先级。

### 4.1 新增或强化的产品原则

1. **先解决启动，再判断偏离。** 承诺卡片可选生成 1—5 个“执行步骤”，默认只显示当前一步；AI 输出始终是草稿，用户确认后才保存。
2. **证据、判断、后果三层分离。** `EvidenceObservation` 只记录事实；`ClassificationDecision` 记录规则与结果；`Intervention` 单独记录提醒。任何一层都不能追溯覆盖上一层。
3. **不确定不是违约。** 未确定活动只触发询问，不直接升级为“分心”；无回应可以继续偏离计时，但回顾中必须展示不确定占比。
4. **失败可修订，不可静默改写。** 计划变化通过承诺修订或结束后重排建议处理；旧版本、原证据和提醒继续保留。
5. **社会在场感不等于情感债。** 可有“我陪你做”状态、开场一句承诺和结束一句汇报；忽略 Jarvis 不扣数值、不表现受伤、不影响关系。
6. **提醒升级必须可预演、可封顶、可退出。** 用户在承诺卡片上看到本机提醒、手机提醒、上限与紧急出口；不引入扣款、联系人羞辱或锁机。
7. **默认最小权限，能力渐进开启。** 第一次价值时刻不要求屏幕、麦克风或内容访问；语音、成长上下文和任何未来的内容分类分别授权。

### 4.2 功能需求

| ID | 需求 | 最小验收标准 | 来源启发 |
|---|---|---|---|
| FR-V2-01 | 承诺创建后可选“拆成下一步” | AI 生成 1—5 步；逐步可编辑、删除、排序；整组需确认；失败/断网时可手工创建 | PlanCoach、Tiimo |
| FR-V2-02 | 当前一步常显 | 桌宠悬停、小面板和承诺页显示同一当前步；可关闭；不会遮挡输入区 | PlanCoach、Tiimo |
| FR-V2-03 | 步骤推进不等于完成证据 | 勾选/语音推进只写 `StepProgressEvent`；不能重置偏离或自动完成成果目标 | 对 PlanCoach 的边界修正 |
| FR-V2-04 | 每个承诺拥有活动规则集版本 | 支持相关/分心/未确定规则；修正后产生新版本；历史判定保留 | Focus Bear、Forfeit |
| FR-V2-05 | 误判快速纠正 | 本机和飞书均提供“这是相关工作”；显示判定依据类别；可选作用域：一次、当前承诺、模板；重大范围先确认 | Focus Bear、Forfeit |
| FR-V2-06 | 提醒策略预览与退出 | 承诺卡显示预计 5 分钟本机、20 分钟手机、最多 3 条；提供静音、限时休息、修订、取消和完全退出说明 | Focus Bear、Tiimo |
| FR-V2-07 | 返回意图与实际返回分离 | “马上回去”只记录意图；只有稳定相关证据才结束连续偏离；界面同时展示两者 | Forfeit 的证据治理 + 现有规格 |
| FR-V2-08 | 结束时提供三路处理 | 结束后选择回顾、明确跳过、稍后提醒；未完成可生成“修订/新承诺”草稿，不自动续期 | Tiimo、Focusmate |
| FR-V2-09 | 轻量 body-double 模式 | 用户可在开始时口述/输入一句目标；角色进入安静工作动画；结束询问一句结果；全程明确为 AI | Focusmate、AIRI |
| FR-V2-10 | 语音独立开关 | STT、TTS、提示音分别配置；监督默认不朗读私人内容；可一键停止发声 | PlanCoach、AIRI |
| FR-V2-11 | 数据与权限中心 | 分开展示活动证据、语音、成长上下文、AI 提供商、遥测；每类可查看用途、最近访问、关闭和删除范围 | AIRI、六款隐私差异 |
| FR-V2-12 | 规则模拟器 | 在保存模板/承诺前，用示例 App/URL/空闲片段演示会判为哪一类、何时提醒，不发送真实提醒 | Focus Bear 的误判成本 |
| FR-V2-13 | 可选 Idea Parking | 监督中可把突然想到的事项以最小交互放入 `IdeaParkingEntry`；该动作不暂停执行、不重置偏离、不修改当前承诺，结束后再决定是否转计划 | PlanCoach 灵感捕捉；对承诺边界的修正 |

### 4.3 非功能需求

| ID | 要求 | 建议验收 |
|---|---|---|
| NFR-V2-01 可解释性 | 每次活动分类记录规则版本、证据类别、结果和触发原因；AI 不得成为唯一理由 | 任取一条提醒，可在本机还原“什么证据—哪条规则—何时升级” |
| NFR-V2-02 隐私最小化 | 默认不存窗口/网页标题、截图、输入、正文；步骤/承诺文本不进入产品遥测 | 自动化测试扫描遥测 payload 与日志字段；禁止内容字段序列化 |
| NFR-V2-03 离线可靠性 | 无 AI、无网、飞书失败时，本机承诺、分类、计时、提醒和记录继续 | 断网/模型 5xx/飞书 5xx 故障注入通过 |
| NFR-V2-04 幂等与版本化 | 每次回复、修订、卡片操作有幂等键并绑定承诺版本和连续偏离 ID | 重复点击、延迟消息、重启恢复不产生重复修订或重复提醒 |
| NFR-V2-05 可撤销性 | 新规则、语音和实验可关闭；强约束不依赖外部人密码 | 完全退出始终可达，且明确留下无法观察时段 |
| NFR-V2-06 感官安全 | 声音、动画、移动、语速、通知频率分别可调；公共场合默认不泄露目标 | 静音/会议/全屏/公开模式矩阵测试 |
| NFR-V2-07 性能 | 不因步骤常显、语音模块或状态遥测突破现有常驻资源基线 | 按现有 320 MiB 平均/350 MiB 峰值与 CPU <1% 原型验收 |
| NFR-V2-08 数据权利 | 用户可导出、按类别删除并看到保留期限；删除与备份范围一致 | 删除演练 + 备份恢复演练 + 可读导出校验 |

### 4.4 数据模型建议

现有 `WorkCommitment` 之外建议增加以下一级对象，避免把功能堆进单表：

| 对象 | 关键字段 | 约束 |
|---|---|---|
| `ExecutionPlan` | `id`, `commitment_id`, `version`, `source(manual/ai)`, `confirmed_at` | AI 草稿未确认前不是正式计划 |
| `ExecutionStep` | `plan_id`, `ordinal`, `text`, `status`, `estimated_minutes` | 文本属于内容数据，不进入遥测 |
| `StepProgressEvent` | `step_id`, `event_type(start/pause/skip/complete/reopen)`, `at`, `input_mode` | 只追加，不等同履约证据 |
| `ActivityRuleSet` | `scope`, `version`, `rules`, `effective_from`, `supersedes_id` | 承诺、模板和全局分层；旧版不可覆盖 |
| `EvidenceObservation` | `commitment_id`, `observed_at`, `evidence_type`, `normalized_value`, `availability` | 沿用当前边界，只保存非内容标准化值 |
| `ClassificationDecision` | `observation_id`, `rule_set_version`, `class`, `reason_code`, `confidence_source` | AI 若参与只提交候选，核心验证 |
| `CorrectionCase` | `decision_id`, `requested_scope`, `user_statement`, `resolution`, `resolved_at` | 误判纠正作为一级对象；保留原判定 |
| `DeviationEpisode` | `commitment_version`, `started_at`, `ended_at`, `uncertain_ms`, `distracting_ms`, `unobservable_ms` | 不用单一“偷懒时长”抹平证据差异 |
| `InterventionEvent` | `episode_id`, `level`, `channel`, `sent_at`, `reason_code`, `dedupe_key` | 与证据/判定分离；支持上限和幂等 |
| `ReturnIntent` | `episode_id`, `declared_at`, `source`, `confirmed_by_evidence_at` | 自报意图不直接重置计时 |
| `CommitmentReview` | `raw_text`, `structured_outcome`, `created_at`, `ai_summary_version` | 原文优先；结构化结果可重新整理 |
| `IdeaParkingEntry` | `commitment_id`, `captured_at`, `text`, `source`, `promoted_to_plan_id` | 属于候选想法，不暂停计时、不改变证据/判定、未经确认不得升级为计划或承诺 |
| `ExperimentAssignment` | `experiment_id`, `variant`, `started_at`, `expires_at`, `consent_id` | 行为实验有期限、退出和同意记录 |

### 4.5 正交状态机建议

不要用一个巨大 `status` 同时表达任务、证据和提醒。至少拆成四个状态机：

1. **承诺生命周期**：`draft → confirmed → scheduled → active → review_pending → closed`；旁路为 `revised / postponed / cancelled`，并产生新版本而非覆盖。
2. **观察可用性**：`observable ↔ unobservable`；不可观察只产生证据缺口，不改变完成结论。
3. **活动分类**：`related / uncertain / distracting`；用户纠正产生 `correction_pending → corrected/rejected`，原决定保留。
4. **干预升级**：`none → local_nudge → mobile_1 → mobile_2 → mobile_3 → capped`；`return_intent` 是回应状态，不是 `related`；稳定相关证据、确认休息、修订或取消才终止 episode。

关键不变量：

- `ExecutionStep.complete` 不能令 `WorkCommitment.outcome = completed`。
- `ReturnIntent` 不能直接令 `DeviationEpisode` 结束。
- `unobservable` 不能累计到 `distracting_ms`。
- `CorrectionCase` 不能删除原 `ClassificationDecision`。
- 旧飞书卡片不能操作新承诺版本或新偏离 episode。
- AI 不得直接写承诺、规则、结果或后果，只能提交带来源的候选命令。

### 4.6 隐私友好的遥测建议

Jarvis 是单用户本地产品，第二版首先使用**本机产品诊断事件**；是否发送匿名产品遥测应单独征得同意。建议只记录：

- `commitment_created`, `plan_confirmed`, `step_started`, `step_reopened`；
- `classification_corrected` 的作用域与原因代码，不含 App、域名或标题；
- `intervention_sent/acknowledged` 的级别、通道、延迟和是否封顶；
- `return_intent_confirmed_by_evidence` 的耗时；
- `review_completed/skipped/deferred`；
- `voice_enabled/disabled`、STT/TTS 成功率和延迟，不含音频与转写；
- Core/Desktop 重启、飞书失败、模型失败、资源预算超限。

推荐派生指标：首次承诺完成时间、AI 计划草稿采纳率、步骤重开率、活动纠正率、未确定占比、提醒后恢复中位时长、每 episode 提醒数、封顶率、回顾完成率、第二天重复承诺率。所有指标默认本机可查看；任何上传均使用随机安装 ID、粗粒度版本/系统信息，并明确禁止承诺文本、步骤文本、域名、窗口标题、聊天/复盘正文、个人记忆和凭据。

### 4.7 验证实验

| 实验 | 假设 | 设计 | 成功指标 | 退出/风险 |
|---|---|---|---|---|
| E1 下一步卡片 | 当前一步常显能降低启动延迟 | 同一用户交替使用“仅承诺标题”和“标题+当前一步”，各 10 次承诺 | 生效到首次稳定相关活动的中位时间下降 ≥20% | 步骤阅读负担上升则关闭 |
| E2 AI 拆解确认 | 先给草稿再确认比直接聊天更可靠 | 记录生成、编辑、删除、确认；访谈为何修改 | ≥60% 草稿被确认，且平均修改不超过 2 项 | 不把采纳率当完成率 |
| E3 三态误判治理 | “不确定先问”能降低错误升级 | 对 30 段脱敏模拟活动做规则回放 | 明确分心召回率可接受且错误手机升级 <5% | 不发送真实提醒 |
| E4 提醒剂量 | 一次本机轻提醒 + 状态标记优于重复气泡 | 对比一次提醒和 5 分钟重复提醒 | 恢复率相近、打扰评分显著更低 | 任一方案出现焦虑立即停 |
| E5 AI body double | 开场一句承诺和结束一句汇报能提高回顾率 | 用户自选开启/关闭，各 10 场 | 开始率、回顾率提升；忽略后无负面感 | 明确 AI 身份，不用失落文案 |
| E6 温和重排 | 结束后生成新承诺草稿比自动续期更可控 | 未完成时提供“结束/新草稿/修订”三选一 | 次日计划保留率提高，误创建为 0 | 默认不选中任何选项 |
| E7 语音边界 | 只在用户主动语音回合输出语音可兼顾陪伴和隐私 | 耳机、扬声器、会议、公开模式场景矩阵 | 私密内容误播为 0；手动停声 <1 秒 | 失败立即降级文字 |
| E8 解释面板 | 显示“证据类别—规则—决定”能建立信任 | 让用户解释 20 条模拟判定为何发生 | 90% 能正确复述；纠正路径 <3 次点击 | 不显示敏感内容 |

### 4.8 V2 决策优先级

**应进入开发手册（机制成熟、与现有边界一致）：**

- 可选执行步骤、当前一步常显、手工 Idea Parking；三者都只是辅助计划，不构成履约证据。
- `EvidenceObservation → ClassificationDecision → CorrectionCase → InterventionEvent` 分层，规则集和承诺都版本化。
- 不确定先问、误判快速纠正、提醒预览/封顶/静音/退出、返回意图与实际返回分离。
- 结束后“回顾 / 明确跳过 / 稍后提醒”，以及未完成项生成新草稿而非自动续期。
- 权限/数据中心与离线确定性核心；若语音实验启用，STT、TTS、提示音必须独立开关。

**先做实验再决定（价值合理，但行为效果或成本未知）：**

- AI 拆解是否真实缩短启动时间，而非只增加计划编辑；当前一步常显是否造成遮挡和认知负担。
- AI body-double 的开场/结束仪式、语音交互、规则解释面板和通知剂量。
- 匿名产品遥测、任何云端成长上下文与内容类 AI 功能；必须先验证最小字段和单独同意。

**第二版明确排除（违反当前产品原则或成本远高于价值）：**

- 金钱扣罚、联系人/伙伴羞辱、伙伴密码、不可退出阻断、锁机和无申诉模式。
- 摄像头常开、照片/视频、位置、健康数据、网页正文/标题、键盘和截图监督。
- 将 AI 判定直接写成事实或后果、自动续期承诺、把步骤勾选/计时/角色好感当完成证据。
- Electron + Live2D/VRM 重型渲染迁移，以及为了陪伴主动扩大工具、屏幕和插件权限。

## 5. 建议修改现有文档的位置

- `docs/product/supervision-spec.md`：在 2.1/2.2 增加可选 `ExecutionPlan` 与确认规则；在 4.2 增加判定解释和 `CorrectionCase`；在 5 增加干预上限/预览与正交 episode；在 8 增加未完成后的“新承诺草稿”而非自动续期。
- `docs/product/companion-spec.md`：在 5 增加可选当前一步常显和 body-double 工作动画；在 6 明确监督时不做主动闲聊；在 7 增加 STT/TTS/提示音独立开关和一键停声。
- `docs/product/requirements-discovery.md`：新增需用户确认项——是否进入第二版的步骤计划、规则模拟器、匿名遥测、AI body-double 与语音输入；没有确认前不扩展权限。
- `CONTEXT.md`：若上述概念进入正式产品，新增“执行计划”“执行步骤”“分类纠正”“返回意图”等统一语言，继续避免把 To-do、计时和工作承诺混用。
- 若未来读取网页标题/内容、屏幕、摄像头、位置、健康或联系人，必须先新建 ADR，并重新确认活动证据/成长上下文边界；本报告不建议在第二版默认加入这些能力。

## 6. 仍需实测而不能靠公开资料回答的问题

1. PlanCoach 贴脸提醒的实际频率、偏航判断、跳过/失败状态，以及 AI/语音数据的真实出站链路。
2. Focus Bear AI Blocking 的误判率、标题/预览的实际出站 payload、跨设备同步延迟和 break-glass 的真实摩擦。
3. Forfeit 真人审核的一致性、申诉通过标准、证据误判与扣款的先后时序，以及中国地区支付/可用性。
4. Focusmate 在中国网络环境的实时视频质量、匹配等待、爽约后的平台处置和摄像头常开接受度。
5. Tiimo Co-Planner 的中文拆解质量、任务重排是否会越过用户确认、通知在 iOS/Android 的一致性。
6. AIRI v0.11.3 在目标 Windows 机的常驻资源、窗口穿透、多显示器、STT/TTS 延迟、遥测关闭后的网络行为；发行包存在不等于成熟支持。
