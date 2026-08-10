# Jarvis 手机通知通道研究：微信、企业微信与隐私优先替代方案

> 结论基准日：2026-08-07；官方页面复核日：2026-08-08。只采用平台官方/第一方文档。官方未明确说明的地方均标为“推论”，尤其是“没有某项 API”这类否定结论。

## 结论先行

Jarvis 不应接管、模拟点击或逆向登录普通个人微信号。微信开放平台列出的服务端身份是移动应用、网站应用、小程序、公众号/服务号等 AppID 主体，不包含普通个人微信号；这是根据官方能力目录作出的推论。更重要的是，《腾讯微信软件许可及服务协议》明确禁止未经授权的第三方工具接入微信，以及自动化访问、读取或控制微信。[微信开放平台介绍](https://developers.weixin.qq.com/doc/oplatform/open/intro.html)；[微信软件许可及服务协议，第 8.2.1.4–8.2.1.8 条](https://weixin.qq.com/cgi-bin/readtemplate?head=true&lang=zh_CN&s=default&t=weixin_agreement)

对当前产品需求，推荐分两阶段：

1. **MVP：企业微信自建应用 + 一个最小化云端中继/定时器。** 自建应用可以只给单个 `UserID` 发消息；支持能回调的交互式模板卡片；用户发给应用的自然语言消息也能回调给 Jarvis。云端只保管必要凭据、待发送提醒和短期动作状态，丰富上下文仍留在 PC。发送接口、交互卡片和接收消息能力均有一手文档支持。[发送应用消息](https://developer.work.weixin.qq.com/document/path/90236)；[接收消息与事件](https://developer.work.weixin.qq.com/document/path/90238)；[回调配置](https://developer.work.weixin.qq.com/document/path/90930)
2. **后续普通微信入口：小程序订阅消息 + 小程序内的 Jarvis 操作页。** 它能把通知送入普通微信的“服务通知”，点击后进入小程序做“完成/稍后/停止监督”或输入自然语言。但普通一次性订阅是“一次授权换一条消息”；长期多次订阅目前只向政务民生、医疗、交通、金融、教育等线下公共服务开放，所以不能假设个人时间管理产品可用长期每日提醒。[小程序订阅消息概览](https://developers.weixin.qq.com/miniprogram/dev/framework/open-ability/subscribe-message-overview.html)

若用户愿意安装独立通知 App，**自托管 ntfy** 是隐私优先的并行备选：它支持 HTTP 动作按钮、延迟投递和自托管，但没有内建的自然语言“快捷回复”，需要按钮打开 Jarvis 移动网页；iOS 即时通知还需要把不含正文的轮询请求经 APNs/Firebase 上游转发。[ntfy 发送与动作按钮](https://docs.ntfy.sh/publish/)；[ntfy 自托管与 iOS 即时通知](https://docs.ntfy.sh/config/#ios-instant-notifications)

## 三个场景的架构判断

### 场景 1：PC 在线，20 分钟后监督提醒，手机可操作或自然语言回复

- **企微自建应用：满足。** PC 或云端向单个成员发交互式模板卡片；按钮事件回调到 Jarvis。用户也可直接给应用发文本，企业微信将消息推送到配置的 URL。支持回调更新的卡片必须配置回调接口，`response_code` 72 小时有效且只能使用一次。[发送应用消息：模板卡片与回调](https://developer.work.weixin.qq.com/document/path/90236)；[接收消息](https://developer.work.weixin.qq.com/document/path/90238)
- **企微群消息推送/Webhook：只满足提醒，不满足直接回复。** 当前文档只定义向群组 Webhook 发送消息；卡片动作只跳 URL 或小程序，没有定义群消息或按钮事件回传给该 Webhook。因此“它是单向通道”是由接口定义作出的推论。可把按钮做成带签名的 HTTPS URL，但自然语言仍需另开网页/小程序。[消息推送（原“群机器人”）](https://developer.work.weixin.qq.com/document/path/91770)
- **小程序订阅消息：有条件满足。** 用户需在开始一次监督会话时主动订阅；一次订阅允许稍后下发一条通知。通知卡片只跳回小程序页面，动作和文本输入应在小程序页面里完成，不是通知上的聊天式回复。[订阅消息概览](https://developers.weixin.qq.com/miniprogram/dev/framework/open-ability/subscribe-message-overview.html)；[`wx.requestSubscribeMessage`](https://developers.weixin.qq.com/miniprogram/dev/api/open-api/subscribe-message/wx.requestSubscribeMessage.html)
- **ntfy：按钮满足，文本需落地页。** 通知可包含最多三个 `view`、Android `broadcast`、`http` 或 `copy` 动作；官方动作类型中没有文本输入动作，因此自然语言需用 `view` 打开 Jarvis 移动网页。[ntfy action buttons](https://docs.ntfy.sh/publish/#action-buttons)

### 场景 2：PC 离线，夜间复盘提醒仍须送达

任何由 PC 当场触发的通道，在 PC 关机后都不会自己产生新请求。必须把“何时发、发给谁、最少正文”提前交给一个持续在线的执行者：

- 对企微/服务号/小程序，使用小型云函数或服务端定时任务调用官方发送 API。腾讯云函数的定时触发器支持 Cron，并在指定时间异步调用函数。[腾讯云 SCF 定时触发器](https://cloud.tencent.com/document/product/583/9708)
- ntfy 可以接收带 `Delay` 的单次延迟消息，服务端到时发送；定时消息会在服务端缓存到投递后，因此要把正文控制到最低敏感度。其默认可延迟上限由 `message-delay-limit` 控制，官方默认值为 3 天。它适合“PC 在线时先排好今晚一条”，不等于长期循环 Cron。[ntfy scheduled delivery](https://docs.ntfy.sh/publish/#scheduled-delivery)；[ntfy 配置项](https://docs.ntfy.sh/config/)
- 若必须每天循环且 PC 可能连续多日离线，仍应有云端 Cron，或在手机本地创建周期通知；不要依赖 Windows 任务计划程序。

### 场景 3：个人单用户、低成本、强隐私

- 只把通知所需的最小字段放在云端，例如 `reminder_id`、计划时间、模板变量、短期动作令牌；屏幕截图、成长上下文、对话记忆和模型密钥留在 PC。
- 所有按钮 URL 使用短时效、单次 nonce 和 HTTPS；动作落地后只返回命令 ID，PC 在线时再取任务。
- 企微 Webhook URL 等同写权限密钥，官方特别警告不得公开；自建应用的 `corpsecret` 和 `access_token` 只能放服务端/系统密钥存储，不能进入前端包。[企微消息推送安全提示](https://developer.work.weixin.qq.com/document/path/91770)；[企微 access_token 安全要求](https://developer.work.weixin.qq.com/document/path/91039)
- 企业微信工作数据会经过腾讯企业微信体系；企业用户对提交和产生的工作数据承担个人信息处理者责任，应完成告知与同意。[企业微信隐私保护指引](https://work.weixin.qq.com/nl/privacy)
- 自托管 ntfy 应开启认证并将默认访问设为 `deny-all`；官方配置的默认匿名权限是 `read-write`，不改配置就不适合敏感提醒。[ntfy 私有实例与 ACL](https://docs.ntfy.sh/config/#example-private-instance)

## 通道逐项比较

| 通道 | 手机中的位置 | 开通与凭据 | 仅发送是否需公网回调 | 手机交互/回复 | PC 离线定时 | 主要约束与成本判断 | 结论 |
|---|---|---|---|---|---|---|---|
| 普通个人微信号 | 普通微信聊天 | 没有面向第三方后端的普通个人号机器人凭据；官方开放对象是 AppID 应用主体。这是根据能力目录作出的推论。[开放平台介绍](https://developers.weixin.qq.com/doc/oplatform/open/intro.html) | 不适用 | 非官方机器人可模拟聊天，但未经授权的接入和自动化控制违反软件协议。[微信软件协议](https://weixin.qq.com/cgi-bin/readtemplate?head=true&lang=zh_CN&s=default&t=weixin_agreement) | 不适用 | 有封号、隐私泄漏、协议和稳定性风险；不能作为产品依赖。 | **排除** |
| 微信认证服务号：模板/订阅通知 | 已关注时可进服务号会话；部分订阅通知对未关注用户进入“服务通知” | 认证服务号、AppID/AppSecret、`access_token`、用户 `openid`、模板 ID；订阅通知还需用户主动订阅。[模板消息](https://developers.weixin.qq.com/doc/service/guide/product/template_message/Template_Message_Interface.html)；[订阅通知](https://developers.weixin.qq.com/doc/service/guide/product/subscription_messages/intro.html)；[获取 access_token](https://developers.weixin.qq.com/doc/service/api/base/api_getaccesstoken.html) | 单向发送只需后台主动 HTTPS 调用，不需入站 URL；但凭据必须在后台。若处理用户回复，微信会把消息 POST 到开发者 URL。[发送模板消息](https://developers.weixin.qq.com/doc/service/api/notify/template/api_sendtemplatemessage)；[接收消息](https://developers.weixin.qq.com/doc/service/guide/product/message/Receiving_standard_messages.html) | 模板可跳网页/小程序；用户在服务号会话发文本后可走回调。客服消息额度受会话窗口限制：用户发消息后 48 小时内 5 条；菜单/关注/扫码触发为 1 分钟内 3 条。[客服消息规则](https://developers.weixin.qq.com/doc/service/guide/product/kf/intro.html) | 需要云端 Cron/发送服务 | 仅认证服务号可申请模板能力；模板只允许重要业务服务通知，要求用户接受过主体服务或由用户行为/同意触发，禁止营销与骚扰。长期订阅仅向政务民生、医疗等公共服务开放。[模板运营规范](https://developers.weixin.qq.com/doc/service/guide/product/template_message/Template_Message_Operation_Specifications.html)；[订阅通知介绍](https://developers.weixin.qq.com/doc/service/guide/product/subscription_messages/intro.html) | **已有合规服务号时可用；个人 MVP 不优先** |
| 微信小程序订阅消息 | 普通微信“服务通知” | 小程序 AppID/AppSecret、服务端 `access_token`、用户 `openid`、模板 ID；用户调用订阅 API 同意。[订阅概览](https://developers.weixin.qq.com/miniprogram/dev/framework/open-ability/subscribe-message-overview.html)；[发送订阅消息](https://developers.weixin.qq.com/miniprogram/dev/server/API/mp-message-management/subscribe-message/api_sendmessage) | 发送接口必须在服务器端调用，不能从小程序/网页/App 前端直接调用；只发通知无需入站回调，可用云函数。[发送订阅消息](https://developers.weixin.qq.com/miniprogram/dev/server/API/mp-message-management/subscribe-message/api_sendmessage) | 卡片点击只回到本小程序页面；动作与文本输入在页面完成。订阅弹窗一次最多请求 5 个不同标题模板，用户可接受或拒绝。[`wx.requestSubscribeMessage`](https://developers.weixin.qq.com/miniprogram/dev/api/open-api/subscribe-message/wx.requestSubscribeMessage.html) | 需要云端 Cron/发送服务 | 普通一次性订阅只换一条通知；长期多次仅面向指定线下公共服务行业，个人时间管理不能预设可获批。模板还须与小程序类目匹配。[订阅消息概览](https://developers.weixin.qq.com/miniprogram/dev/framework/open-ability/subscribe-message-overview.html) | **后续普通微信体验的首选，但夜间每日提醒仍需备用通道** |
| 企业微信群消息推送（原群机器人） | 企业微信群 | 在群内创建“消息推送”，得到含 `key` 的专属 Webhook URL；无需 CorpID、Secret 或 access_token。[配置说明](https://developer.work.weixin.qq.com/document/path/91770) | 不需要入站回调；任何能出站 HTTPS POST 的进程都能发送。这是由接口定义作出的推论。 | 支持文本、Markdown、图片、图文、文件、语音、模板卡片等 8 类；卡片只能跳 URL/小程序，Webhook 本身没有接收群回复或按钮事件的接口。[配置说明](https://developer.work.weixin.qq.com/document/path/91770) | 云端 Cron 调 Webhook 即可 | 每个消息推送不超过 20 条/分钟；Webhook 泄漏后他人可向群发消息。官方页未列按条价格，但这不等于所有企业微信账号/权益永久免费。[配置说明](https://developer.work.weixin.qq.com/document/path/91770) | **仅适合单向烟雾测试或兜底广播，不满足对话 MVP** |
| 企业微信自建应用 | 企业微信应用消息/应用会话 | 创建企业和自建应用；取得 CorpID、应用 Secret、AgentId、目标 `UserID`；用 CorpID + Secret 换 `access_token`。[获取 access_token](https://developer.work.weixin.qq.com/document/path/91039)；[发送应用消息](https://developer.work.weixin.qq.com/document/path/90236) | 主动发送不需入站 URL。自然语言或按钮回调需公开可访问的 URL、Token、EncodingAESKey；企业微信会验证 GET，再把业务事件加密 POST 到该 URL。[回调配置](https://developer.work.weixin.qq.com/document/path/90930) | 支持可回调更新的按钮/投票/多选模板卡片，也接收用户发给应用的文本等消息。5 秒不能完成处理时可先返回 200 空串，再异步回复；平台总计重试三次。[发送应用消息](https://developer.work.weixin.qq.com/document/path/90236)；[接收消息](https://developer.work.weixin.qq.com/document/path/90238) | 云端 Cron 调发送 API | 用户须在应用可见范围；发送接口返回可能包含 `unlicenseduser`（没有/已过期的基础接口许可），所以要在实际管理后台核验当前账号权益，不能直接写成永久免费。频率为每应用“企业账号上限 × 200 人次/日”，同一成员 30 次/分钟、1000 次/小时。[发送应用消息](https://developer.work.weixin.qq.com/document/path/90236) | **推荐 MVP；代价是安装/登录企业微信及一个最小公网后端** |
| 自托管 ntfy | ntfy iOS/Android/Web 通知 | 自建 ntfy 服务、HTTPS 域名、用户/访问令牌、私有 topic；手机订阅同一服务和 topic。[配置](https://docs.ntfy.sh/config/)；[发布 API](https://docs.ntfy.sh/publish/) | 不需要“回调”才能发，但自托管服务必须可被手机访问。HTTP 动作若要回 Jarvis，则动作 URL 也须可访问。 | 最多三个按钮，可打开 URL、发 HTTP 请求、Android 广播或复制；没有内建文本回复，需打开 Jarvis 网页。[动作按钮](https://docs.ntfy.sh/publish/#action-buttons) | 支持服务端单次延迟投递；长期循环仍用 Cron。[延迟投递](https://docs.ntfy.sh/publish/#scheduled-delivery) | 软件可自托管，成本是自己的主机/域名/运维。iOS 即时推送必须经连接 APNs/Firebase 的上游转发轮询请求；若不配置，送达可能延迟数小时。[iOS 即时通知](https://docs.ntfy.sh/config/#ios-instant-notifications) | **隐私优先备选，尤其 Android；不是微信体验** |

## 推荐 MVP 设计

```text
Windows Jarvis
  ├─ 实时监督事件 ──HTTPS──> 最小云中继 ──> 企业微信 send API ──> 手机
  │                              ▲                              │
  │                              └──── 加密回调：按钮/文本 ─────┘
  └─ 只同步待发提醒 ────────────> 云端 Cron（PC 离线仍运行）
```

### 最小凭据与数据

- 云端保存：CorpID、应用 Secret（托管密钥库）、AgentId、单个 UserID、回调 Token、EncodingAESKey；`access_token` 按应用缓存，不能返回前端。[获取 access_token](https://developer.work.weixin.qq.com/document/path/91039)
- 每条提醒只保存：随机 `reminder_id`、计划时间、通知模板变量、过期时间、一次性动作 nonce。不要保存屏幕截图、原始成长上下文或完整对话。
- 回调入口只做验签/解密、去重、落短命令队列并快速 200；模型推理放到异步工作线程或 PC。企业微信 5 秒超时并最多重试三次，需按 `msgid` 或事件键去重。[接收消息协议](https://developer.work.weixin.qq.com/document/path/90238)

### 一条监督提醒的交互

1. PC 在会话开始时登记 `reminder_id`；20 分钟仍未达标则发按钮交互卡片。
2. 卡片按钮建议为“已完成”“再给 10 分钟”“结束监督”，`EventKey` 只放无意义短令牌，不放任务正文。
3. 用户点击后云端立即更新卡片并把命令排给 PC；PC 在线时执行。
4. 用户输入自然语言时，回调把文本作为短期消息投递给 PC；若 PC 暂离线，云端仅保留加密正文并按既定 48 小时原始上下文上限清除。

### 夜间复盘

云端 Cron 在固定时间发送不含敏感详情的“今晚复盘已准备好”卡片；用户点击进入一个受认证的极简移动页，或直接在企业微信应用会话回复。PC 离线时云端可记录“稍后/跳过/已复盘”，但不在云端生成或保存完整复盘内容。

## 后续普通微信路线的准入门槛

当以下条件都满足时，再建设小程序通道：

1. 已确定可注册、上线的小程序主体与“效率/时间管理”服务类目，并在公共模板库中找到合规模板；官方发送接口会拒绝无订阅关系或已耗尽一次性额度的请求（错误码 `43101`）。[发送订阅消息](https://developers.weixin.qq.com/miniprogram/dev/server/API/mp-message-management/subscribe-message/api_sendmessage)
2. 每次用户开始一段监督会话时，由明确的用户点击触发 `wx.requestSubscribeMessage`，为该会话换取一条通知；不要在页面加载时强行索取订阅。[订阅 API](https://developers.weixin.qq.com/miniprogram/dev/api/open-api/subscribe-message/wx.requestSubscribeMessage.html)
3. 通知只写“监督时间已到”等必要内容，点击后进入小程序完成按钮或自然语言交互。
4. 夜间每日提醒继续由企微/ntfy 承担，除非微信后台确实向该主体和类目开放了长期或限频订阅模板；不能把指定公共服务行业能力当作普遍能力。[订阅消息分类](https://developers.weixin.qq.com/miniprogram/dev/framework/open-ability/subscribe-message-overview.html)

## 不建议采用的捷径

- **个人微信 PC Hook、协议机器人、UI 自动化发消息：** 无官方服务端身份，且未经授权接入与自动化控制受协议禁止。[微信软件协议](https://weixin.qq.com/cgi-bin/readtemplate?head=true&lang=zh_CN&s=default&t=weixin_agreement)
- **把企微群 Webhook 当双向机器人：** 文档没有入站消息/事件接口；用户在群里说话不会回到该 Webhook。这是根据其纯发送接口作出的推论。[企微消息推送](https://developer.work.weixin.qq.com/document/path/91770)
- **用服务号模板消息做任意每日督促：** 模板通知必须与用户已接受的服务及行为/同意相关，并禁止营销骚扰；长期订阅还有限定行业。应先获得合规模板和账号权限，而不是先写发送器。[模板消息运营规范](https://developers.weixin.qq.com/doc/service/guide/product/template_message/Template_Message_Operation_Specifications.html)；[服务号订阅通知](https://developers.weixin.qq.com/doc/service/guide/product/subscription_messages/intro.html)
- **把 Secret、Webhook 或 access token 打包进桌面前端日志/小程序：** 企微要求 token 只保存在后台；Webhook 泄漏可被任意发消息。[access_token 安全要求](https://developer.work.weixin.qq.com/document/path/91039)；[Webhook 安全提示](https://developer.work.weixin.qq.com/document/path/91770)

## 实施前的五项实测

1. 用目标个人账号创建/加入一个单人企业微信组织，确认自建应用和目标成员的“基础接口许可”状态。
2. 在真机确认交互模板卡片按钮回调、自然语言回调、锁屏通知预览和企业微信免打扰设置。
3. 断开 PC 8 小时，验证云端 Cron 仍能发夜间提醒，并验证按钮命令在 PC 恢复后只执行一次。
4. 轮换应用 Secret、Webhook（若保留兜底群）和回调 AESKey，验证旧凭据立即失效且日志不含明文。
5. 以“普通、私密、仅询问时”三类记忆分别测试通知内容降级：私密内容只显示通用标题，详情必须解锁后查看。
