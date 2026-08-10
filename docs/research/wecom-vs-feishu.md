# Jarvis 手机通知通道：企业微信与飞书对比

> 结论基准日：2026-08-08。本文只引用平台官方文档、官方帮助中心和官方 SDK；官方没有明确说明的账号权益、价格和个人租户准入项均标为“需实测”，不把未写明等同于免费或可用。

## 结论

**Jarvis 的 MVP 推荐飞书企业自建应用。** 决定性原因不是卡片样式，而是它把两种运行形态拆得更干净：

- PC 在线时，Jarvis 可用官方 SDK 建立 WebSocket 长连接，只需能访问公网，不需要公网 IP、域名或内网穿透；自然语言消息和新版卡片按钮回调都可走同一长连接。[飞书：使用长连接接收事件](https://open.feishu.cn/document/server-docs/event-subscription-guide/event-subscription-configure-/request-url-configuration-case?lang=zh-CN)；[飞书：使用长连接接收回调](https://open.feishu.cn/document/event-subscription-guide/callback-subscription/step-1-choose-a-subscription-mode/configure-callback-request-address?lang=zh-CN)
- PC 离线时，小型云函数可凭 `App ID + App Secret` 换取 `tenant_access_token`，再通过无状态 HTTPS API 向唯一用户发晚间提醒；仅“发消息”不要求维持 WebSocket。[飞书：调用 API](https://open.feishu.cn/document/server-docs/api-call-guide/calling-process/get-?lang=zh-CN)；[飞书：发送消息](https://open.feishu.cn/document/server-docs/im-v1/message/create?lang=zh-CN)
- 单人开通路径更明确：官方允许创建企业、邀请成员可跳过，创建后默认基础免费版；发送消息教程还明确建议自行创建新企业，并说明应用默认仅创建者可见。[飞书：创建企业](https://www.feishu.cn/hc/zh-CN/articles/360043741453-%E5%88%9B%E5%BB%BA%E4%BC%81%E4%B8%9A)；[飞书：创建企业与邀请成员](https://www.feishu.cn/hc/zh-CN/articles/985882202335-%E7%AC%AC%E4%B8%80%E6%AD%A5-%E5%88%9B%E5%BB%BA%E4%BC%81%E4%B8%9A%E4%B8%8E%E9%82%80%E8%AF%B7%E6%88%90%E5%91%98)；[飞书：快速调用发送消息 API](https://open.feishu.cn/document/introduction?lang=zh-CN)

**企业微信仍是可行备选，而且 2026 年的新能力改变了旧结论。** 企业微信智能机器人现已正式支持 WebSocket 长连接、自然语言收发、流式回复、模板卡片按钮事件和主动推送，无需公网回调；旧调研中“企业微信双向交互只能用公网 URL”的判断仅适用于传统自建应用，不再适用于智能机器人。[企业微信：智能机器人长连接](https://developer.work.weixin.qq.com/document/path/101463)；[企业微信官方 Node.js SDK](https://github.com/WecomTeam/aibot-node-sdk)

但它对本项目有三个结构性代价：

1. 每个智能机器人同一时间只能有一个有效长连接，新连接会踢掉旧连接。因此不能让 PC 和云端同时直连同一个机器人做主备或分工。[企业微信：智能机器人长连接](https://developer.work.weixin.qq.com/document/path/101463)
2. `aibot_send_msg` 虽支持定时提醒和告警，但官方要求用户先在该会话给机器人发过消息，之后机器人才可主动推送；主动发送本身也经由现存 WebSocket 连接。因此 PC 关机后若仍要发晚间提醒，必须把唯一连接长期放在云端，或另用传统自建应用的 HTTPS 发送 API。[企业微信：智能机器人长连接](https://developer.work.weixin.qq.com/document/path/101463)
3. 若改用传统自建应用，主动发送虽可由无状态云函数完成，但自然语言和按钮回调又回到公网 URL、Token、EncodingAESKey 的模式，并且发送返回值存在 `unlicenseduser`（成员无有效基础接口许可）的账号权益风险。[企业微信：发送应用消息](https://developer.work.weixin.qq.com/document/path/90236)；[企业微信：回调配置](https://developer.work.weixin.qq.com/document/path/90930)

若用户已经高频使用企业微信，且愿意部署一个始终在线的云端 WebSocket 网关，企业微信智能机器人可以反超：它的交互足够、通知入口更熟悉，也没有飞书基础免费版的月度 OpenAPI 配额问题已被官方文档明确列出。但在“单人、先本地、尽量少运维、PC 离线仍能定时发送”的当前约束下，飞书更稳妥。

## 能力对比

| 维度 | 企业微信智能机器人 | 企业微信传统自建应用 | 飞书企业自建应用 | 对 Jarvis 的影响 |
|---|---|---|---|---|
| 单人组织与安装 | 需企业微信账号、智能机器人/API 模式、BotID/Secret；官方长连接文档未明确单成员组织和当前账号是否都有创建权限，**需真机实测** | 需企业、CorpID、AgentId、Secret、可见范围；基础接口许可状态需实测 | 可创建新企业且可跳过邀请，默认基础免费版；应用默认对创建者可见 | 飞书的单人路径证据最完整 |
| 主动发给一人 | 支持 `aibot_send_msg`；单聊用用户 `userid`，但须由用户先发过消息；需要有效长连接 | `touser` 可只填一个 UserID，HTTPS API 无需入站回调 | `receive_id` 可用 `open_id/user_id/email` 指向一人，HTTPS API 无需入站回调 | 离线定时“只发送”时，飞书和企微传统应用适合无状态云函数；企微智能机器人需要常驻连接 |
| 自然语言消息 | 单聊或群聊 @ 机器人后通过 WebSocket 收到文本等消息 | 用户给应用发消息后，企微加密 POST 到开发者 URL | 订阅 `im.message.receive_v1`，可用 WebSocket 或 HTTP 收到用户给机器人的单聊消息 | 三者都能满足；飞书/企微智能机器人均可免公网入口 |
| 按钮与卡片 | 模板卡片支持按钮、投票、多选等，按钮产生 `template_card_event`；5 秒内可更新 | 模板卡片支持按钮/投票/多选并回调 URL | 新版飞书卡片支持按钮、表单、选择器等，`card.action.trigger` 可走长连接；3 秒内响应 | MVP 的“完成/稍后/停止”三者均足够；飞书扩展交互更丰富 |
| 回调网络 | WebSocket；无固定公网 IP；同一机器人仅一个有效连接 | 公网 HTTP(S) URL | 企业自建应用支持 WebSocket；也可选公网 HTTP | 两个平台都已支持长连接；飞书允许最多 50 个连接且是集群消费，企微智能机器人只能 1 个 |
| PC 在线监督 | PC 自身维持唯一 WebSocket，直接收按钮和文本 | PC 需经公网中继接收入站回调 | PC 自身维持 WebSocket，直接收按钮和文本 | 企微智能机器人和飞书同级 |
| PC 离线夜间提醒 | 唯一 WebSocket 必须转移/常驻云端；同一机器人不能由 PC、云端同时连接 | 云 Cron 调 HTTPS 发送 API | 云 Cron 调 HTTPS 发送 API | 仅考虑“到点发一条”，飞书最简单 |
| PC 离线时处理回复 | 需要常驻云端 WebSocket | 公网云回调 | 需要常驻云端 WebSocket，或把订阅切为云端 HTTP 回调 | 若离线时也必须即时处理按钮/文本，两者都需要持续在线的云端接收器 |
| 发送频率/配额 | 每会话回复与主动推送合计 30 条/分钟、1000 条/小时；回复窗口为收到消息后 24 小时 | 同一成员 30 次/分钟、1000 次/小时，另有企业账号上限相关规则和许可风险 | 同一用户 5 QPS；基础免费版自建应用受租户月度 OpenAPI 总量控制 | 个人提醒不会碰瞬时频率，飞书月配额与企微许可更值得关注 |
| 中国大陆可用性 | 腾讯企业微信大陆正式产品 | 同左 | 官方企业创建流程使用 `+86` 手机号，基础免费版可用 | 均可在中国大陆使用；无需跨境推送链路 |

飞书接收单聊消息需开通 `im:message.p2p_msg:readonly`（或相应旧权限）并订阅事件；发送只需开通 `im:message:send_as_bot` 等三个权限之一，目标用户还必须在机器人可用范围内。[飞书：接收消息](https://open.feishu.cn/document/server-docs/im-v1/message/events/receive?lang=zh-CN)；[飞书：发送消息](https://open.feishu.cn/document/server-docs/im-v1/message/create?lang=zh-CN)

## 两种关键运行场景

### 1. PC 在线：监督提醒和手机遥控

飞书与企业微信智能机器人都可以让 Windows Jarvis 只建立出站 WebSocket：

```text
手机 IM  ⇄  平台  ⇄  PC 上的长连接客户端  ⇄  Jarvis 本地命令总线
```

两者都能实现“提醒卡片 → 完成/再来 10 分钟/停止 → PC 执行”，也能把用户发给机器人的自然语言交给本地模型处理。

飞书事件处理要在 3 秒内返回；失败会按 15 秒、5 分钟、1 小时、6 小时最多重试四次，并且可能重复，应用应按 `event_id` 幂等。[飞书：事件概述](https://open.feishu.cn/document/server-docs/event-subscription-guide/overview?lang=zh-CN) 企业微信智能机器人卡片更新要求 5 秒内完成；官方 SDK已封装心跳、断线重连和消息分发。[企业微信官方 Node.js SDK](https://github.com/WecomTeam/aibot-node-sdk)

### 2. PC 离线：晚间复盘仍须送达

若需求只是“到点发出一条提醒”，飞书只需一个云 Cron 加无状态发送函数：

```text
云 Cron → 获取 tenant_access_token → 飞书发送消息 API → 手机
```

若用户在 PC 离线期间点击按钮或回复文字也必须立即生效，则两者都需要一个持续在线的云端接收器。飞书长连接断开后的事件重试最长约 7.1 小时，不应被当成离线消息队列；卡片回调本身是同步操作且不提供补推。[飞书：事件概述](https://open.feishu.cn/document/server-docs/event-subscription-guide/overview?lang=zh-CN)；[飞书：接收并处理回调](https://open.feishu.cn/document/event-subscription-guide/callback-subscription/receive-and-handle-callbacks?lang=zh-CN)

企业微信智能机器人的主动推送只能由当前唯一长连接发出。因此，如果选择它，推荐从一开始就把 WebSocket 网关放在云端，PC 与网关通过一个最小命令队列通讯；不要让 PC 与云端争抢同一 Bot 连接。[企业微信：智能机器人长连接](https://developer.work.weixin.qq.com/document/path/101463)

## 成本、许可与需要真机确认的项目

### 飞书

官方配额说明写明：基础免费版单租户下所有企业自建应用原基线为 **10,000 次/月**，超过后会返回 `99991403`；同一页又写了“2026 年 6 月限时 100 万次”。该限时已经结束，但页面没有给出 2026 年 8 月的新基线，因此实施前必须在管理后台“费用中心 → 权益数据”确认当前额度，不能把 100 万次视为长期权益。[飞书：自建应用 API 调用量上限](https://open.feishu.cn/document/uAjLw4CM/ugTN1YjL4UTN24CO1UjN/platform-updates-/custom-app-api-call-limit)

即使按 10,000 次/月计算，单人 Jarvis 若每天发送几十条以内仍有空间，但需要把 token 获取缓存、卡片刷新和不必要的轮询计入预算。企业创建后默认基础免费版；升级商业版通常按企业内总人数购买，单人租户成本仍应以后台实际报价为准。[飞书：版本介绍](https://www.feishu.cn/hc/zh-CN/articles/360049067600-%E9%A3%9E%E4%B9%A6%E5%AE%9A%E4%BB%B7%E7%89%88%E6%9C%AC%E4%BB%8B%E7%BB%8D)

### 企业微信

企业微信智能机器人长连接官方页面列出了连接数和发送频率，但未在该能力页给出价格、单人成员资格或创建数量权益；这些必须用目标账号验证。传统自建应用发送接口可能返回 `unlicenseduser`，所以不能根据“能创建应用”推断“目标成员永久免费拥有发送许可”。[企业微信：智能机器人长连接](https://developer.work.weixin.qq.com/document/path/101463)；[企业微信：发送应用消息](https://developer.work.weixin.qq.com/document/path/90236)

## 通知体验与隐私

两者最终都依赖手机系统通知权限、应用免打扰设置和厂商推送，不能把 API 返回成功等同于用户一定听到提示音。飞书官方特别说明：移动端若开启“关闭手机通知”，桌面端或 iPad 在线时手机将不再提示；因此 Jarvis 使用飞书时必须关闭该选项，并真机测试锁屏、后台、电池优化和勿扰模式。[飞书：设置消息通知](https://www.feishu.cn/hc/zh-CN/articles/111093338362-%E8%AE%BE%E7%BD%AE%E6%B6%88%E6%81%AF%E9%80%9A%E7%9F%A5) 企业微信也应逐项真机验证“PC 在线时手机始终通知”、锁屏预览和休息/免打扰状态，但本文未找到可稳定引用的企业微信官方帮助页，故不把具体默认值写成事实。

隐私上，两者都会让平台处理提醒正文、用户回复和卡片动作。MVP 应采用相同的最小化原则：

- 只申请“机器人发消息、读取用户发给机器人的单聊消息”及卡片回调所需权限；飞书不申请群内全部消息、通讯录、云文档等无关权限。[飞书：申请 API 权限](https://open.feishu.cn/document/server-docs/application-scope/introduction?lang=zh-CN)
- 通知只包含 `reminder_id`、时间和必要短文案；屏幕截图、完整成长记忆、模型密钥、对话长期上下文留在 PC。
- `App Secret`、Bot Secret、Corp Secret 和 access token 只放操作系统凭据库或云端密钥库，不进入前端包、日志或卡片 `value`。飞书官方要求 access token 由服务端保管并禁止传给客户端。[飞书：获取 access_token](https://open.feishu.cn/document/common-capabilities/sso/api/get-access_token)
- 按钮只回传短期、一次性动作令牌；入站事件做签名/连接鉴权、过期校验和幂等。
- 锁屏通知默认使用通用文案。飞书管理员可在基础版免费设置隐藏移动端通知详情。[飞书：移动端通知消息预览](https://www.feishu.cn/hc/zh-CN/articles/193923612413-%E7%AE%A1%E7%90%86%E5%91%98%E8%AE%BE%E7%BD%AE%E7%A7%BB%E5%8A%A8%E7%AB%AF%E9%80%9A%E7%9F%A5%E6%B6%88%E6%81%AF%E9%A2%84%E8%A7%88)

## 推荐 MVP

采用 **飞书企业自建应用 + 本地长连接 + 极小云定时发送器**：

1. 创建单人飞书企业和企业自建应用，启用机器人能力；可用范围只包含创建者。
2. 权限只开 `im:message:send_as_bot` 与 `im:message.p2p_msg:readonly`，订阅 `im.message.receive_v1` 和 `card.action.trigger`。
3. PC 在线时由本地官方 SDK 长连接消费消息和卡片动作；耗时模型调用先入本地队列，事件处理器立即返回。
4. 云端只保存加密的 `App ID/App Secret`、单个接收者 ID、提醒时间、最小模板变量和一次性动作令牌；Cron 在 PC 离线时调用发送 API。
5. MVP 的离线交互只记录“待处理动作”，不在云端保存完整对话。若产品要求 PC 关机数天仍能即时接收自然语言和按钮，再把事件/回调接收器迁到云端 HTTP 或常驻长连接。

上线前做两组 A/B 真机实验，而不是继续纸面推断：

- 飞书：创建单人企业、发布应用、锁屏收卡片、PC 客户端在线时手机仍通知、PC 关机 8 小时后云 Cron 发送、按钮/文字回调、查看当月 API 权益。
- 企业微信：确认目标账号能创建 API 模式智能机器人、单人可见范围、首次用户消息后的主动推送、模板卡片事件、唯一连接切换行为、PC 在线时手机通知和是否出现任何付费/许可提示。

若飞书任一关键实测失败，或用户明显不愿日常打开飞书，再切换到 **企业微信智能机器人 + 常驻云端 WebSocket 网关**；不建议退回传统企业微信自建应用作为首选，因为它同时引入公网回调和许可不确定性。
