# Jarvis 第一版云端 AI 选型

> 结论基准日：2026-08-10。只采用厂商官方 API 文档、价格页和隐私条款。厂商自述的模型能力不能证明它在 Jarvis 真实提示词上的主观质量，因此本文不把营销文案或厂商榜单当作最终胜负依据。

## 结论

**可以先用 DeepSeek V4，但应准确选择 `deepseek-v4-flash`，并把它定义为“试运行默认”，而不是永久锁定。** 第一版建议如下：

- 日常闲聊、口语意图理解、记忆候选提炼和普通复盘：`deepseek-v4-flash`；普通任务默认关闭深度思考，复杂整理再开启。
- 两周复盘：先让 Flash 参与实测；只有真实样例证明 `deepseek-v4-pro` 明显更好时，才按已公开规则升级到 Pro。不要仅因“Pro”名称就多维护一条路由。
- 隐私优先或 DeepSeek 不可用时的**人工配置备选**：阿里云百炼 `qwen3.7-flash`；需要更高档时再测 `qwen3.7-plus`。跨厂商切换必须由用户主动选择，界面显示将发送给哪一家，绝不能静默转发个人资料。
- `Doubao-Seed-2.0-lite` 只进入同场测试，不作为第一备选；其价格和工具调用有竞争力，但本次找到的官方材料没有给出像百炼那样直接的“客户数据绝不用于模型训练”承诺。
- 无论使用哪个模型，模型只能生成“候选命令”。Jarvis Core 必须做 JSON Schema 校验、权限检查和必要的用户确认，模型输出不能直接修改监督承诺、删除记录或执行系统动作。

这不是在断言 DeepSeek 客观最强，而是一个可逆的工程选择：它当前价格低、接口简单、上下文充足，适合先验证产品；真正决定长期默认模型的应是 Jarvis 自己的中文对话与工具调用测试集。

## “DeepSeek V4”是否真实存在

已经正式存在。官方 API 模型 ID 是 `deepseek-v4-flash` 和 `deepseek-v4-pro`，不是笼统的“DeepSeek V4”。官方价格页列出的 Flash 版本为 `DeepSeek-V4-Flash-0731`；旧 ID `deepseek-chat`、`deepseek-reasoner` 已于 2026-07-24 弃用兼容映射，不应写进新项目。[DeepSeek 模型与价格](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)

两者均支持 100 万 token 上下文、最大 38.4 万 token 输出、思考/非思考模式、JSON Output 和 Tool Calls。Flash 支持 Responses API，Pro 暂不支持；两者都提供 OpenAI 和 Anthropic 格式端点。因此 MVP 若希望两个档位共用一套调用代码，应先采用 Chat Completions 的共同子集，而不是把业务层绑定到 Responses API。[DeepSeek 模型与价格](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/)

## 候选对比

| 候选 | 公开能力与价格 | 对 Jarvis 的意义 | 主要风险 |
|---|---|---|---|
| **DeepSeek V4 Flash / Pro** | 1M 上下文；JSON、工具调用和自动上下文缓存。Flash 每百万 token：缓存命中输入 ¥0.02、未命中输入 ¥1、输出 ¥2；Pro 为 ¥0.025、¥3、¥6。账号并发上限分别为 2500 和 500，远超个人应用需求。[价格](https://api-docs.deepseek.com/zh-cn/quick_start/pricing/) · [限速](https://api-docs.deepseek.com/zh-cn/quick_start/rate_limit) | 价格低，OpenAI 格式便于 C# 通过可配置 Base URL 接入；同厂商 Flash/Pro 分层不需要跨境或跨厂商传递资料。 | 官方 JSON 文档明确提示偶尔可能返回空内容，必须校验和重试。[JSON Output](https://api-docs.deepseek.com/guides/json_mode/) 更重要的是隐私条款允许使用用户输入改进和训练技术，且留存期限不是固定天数。 |
| **百炼 Qwen3.7 Flash / Plus** | 两者均为 1M 上下文，支持思考模式、Function Calling、结构化输出和 OpenAI 兼容 API。[模型能力](https://help.aliyun.com/zh/model-studio/text-generation-model) 中国内地 `qwen3.7-flash` 在单次输入不超过 32K 时为每百万输入/输出 ¥0.2/¥0.8；32K–256K 为 ¥0.6/¥2.4；256K–1M 为 ¥1.2/¥4.8。`qwen3.7-plus` 标准价在 256K 内为 ¥2/¥8，以上为 ¥6/¥24；促销价不应写入长期预算。[百炼价格](https://help.aliyun.com/zh/model-studio/model-pricing) | Flash 在常见短请求中甚至比 DeepSeek V4 Flash 更便宜；Qwen 还支持图像输入，适合以后经用户授权分析截图。官方明确建议办公类任务从 Plus 开始，但这只是厂商建议，仍需本项目实测。 | 接口参数与 DeepSeek 并非完全一致，需独立适配器。百炼明确“绝不会将您的数据用于模型训练”并使用 AES-256 加密，但同时说明依法存储模型和应用调用数据，不能把它等同于零留存。[百炼隐私说明](https://help.aliyun.com/zh/model-studio/privacy-notice) |
| **Doubao-Seed-2.0 Lite / Pro** | 官方产品页给出的起步价：Lite 每百万输入/输出 ¥0.6/¥3.6，Pro 为 ¥3.2/¥16，并提供上下文缓存；Responses API 支持 Function Calling，示例可直接使用 OpenAI SDK 和北京端点。[产品与价格](https://www.volcengine.com/product/doubao/) · [工具调用](https://www.volcengine.com/docs/82379/1958524?lang=zh) | 国内访问、人民币计费和 API 接入都适合个人 Windows 应用，可作为中文陪伴风格的第三个实测样本。 | “起步价”会随输入长度变化，不能只按最低档估算长复盘；本次一手资料没有确认与百炼同等明确的推理数据不训练承诺，因此不先作为隐私备选。 |

## 隐私是 DeepSeek 的实际门槛

DeepSeek 官方隐私政策写明会收集文本输入、提示词、上传文件和聊天历史，并可能用于改进服务以及训练/改进模型；输入信息可在账号存在期间保留，具体期限取决于数据类型、目的和法律要求，数据存储在中国境内。[DeepSeek 隐私政策](https://cdn.deepseek.com/policies/en-US/deepseek-privacy-policy-2025-02-14.html) 官方模型说明称用户可以选择退出训练，但公开说明主要引导通过隐私政策或 `privacy@deepseek.com` 行使权利，没有在 API 文档中给出可由 Jarvis 自动验证的“零数据留存”开关。[训练方法说明](https://cdn.deepseek.com/policies/en-US/model-algorithm-disclosure.html)

因此，启用 DeepSeek 前应执行三项产品约束：

1. 设置页明确展示上述事实，并提供“仅本机”资料标签；监督原始证据、完整聊天截图、密钥、账户信息默认不得上传。
2. 只发送完成当前任务必需的摘录，记录每次请求的厂商、模型、资料来源和大致 token/费用；请求日志可查看和删除。
3. 如果用户无法确认退出训练是否已经生效，敏感成长资料要么留在本机，要么由用户主动改选百炼。切换供应商不能作为错误重试的隐藏动作。

百炼的“不用于训练”承诺更明确，但它仍会依法存储调用数据；所以“换成 Qwen”也不能替代最小化上传、脱敏和本机资料分级。

## 接入方式

业务层定义统一的 `AiProvider` 接口，至少分开 `Chat`、`StructuredCandidate`、`ReviewSynthesis` 三种用途；每个厂商适配器自行处理 Base URL、思考参数、工具格式、流式事件和错误码。C# 可从 OpenAI 兼容 HTTP 结构起步，但不得假设三家的 Responses API 完全相同。数据库为每次调用保存 `provider/model/model_version/purpose/input_source/token_usage/cost/status`，不长期重复保存已存在于本地资料库的完整明文请求。

正常调用失败时，先在同一厂商按受控次数重试；仍失败就退回本地模板和表单。只有用户已在设置中明确启用某个备选厂商，并在当次界面确认发送目标时，才允许跨厂商重试。

## 锁定前的 Jarvis 小型实测

用 40 条脱敏但真实风格的中文样例，同时测试 `deepseek-v4-flash`、`deepseek-v4-pro`、`qwen3.7-flash`、`qwen3.7-plus`，Doubao Lite 作为可选第五组：

- 8 条闲聊与陪伴：口语、情绪、边界、人设一致性；
- 12 条监督命令：新建/修改/取消模板、休息确认、含糊表达和拒绝执行；
- 8 条每日复盘：从口语中保留事实与感受，不杜撰完成情况；
- 6 条记忆提炼：区分长期偏好、临时状态和不应保存的敏感信息；
- 6 条两周复盘：从带噪记录中找证据、更新重心并提出可执行改进。

每条至少重复三次，并统一上下文和参数。记录：JSON/Schema 一次通过率、工具选择准确率、未经授权动作数、事实可追溯率、人工盲评偏好、首 token 与总耗时的 P50/P95、失败/429 比例和实际人民币成本。涉及动作的硬门槛是**零未经授权执行**；JSON 失败必须在一次受控修复后仍能安全降级。只有真实结果证明 Pro/Plus 在两周复盘上稳定胜出，才启用“日常便宜模型 + 周期复盘强模型”的固定路由。

最终建议保持不变：**先用 `deepseek-v4-flash` 做 MVP 默认，暂把 `deepseek-v4-pro` 作为两周复盘候选，把 `qwen3.7-flash` 作为用户手动选择的隐私优先备选；完成上述测试后再锁定。**
