# ADHD 执行监督、专注与 AI 陪伴竞品初筛

> 初调日期：2026-08-19；两篇小红书笔记于 2026-08-20 通过用户已打开的原页面补充核验。目标是先形成可供选择的候选池，不做完整竞品拆解。关键功能优先引用官网、应用商店、官方 GitHub/文档；商店评分数、下载档位与 GitHub Star 均为调研时点快照，会随时间变化。产品方自报用户数只作为“厂商声称”，不当作已独立核验事实。

## 结论先行

若下一步只深调 6 个，建议选择：

1. **PlanCoach**：国内 ADHD/拖延场景最直接的任务拆解与逐步执行产品。
2. **Focus Bear**：现有候选里与 Jarvis“识别当前应用/网站是否相关并即时干预”最接近。
3. **Forfeit**：最值得研究承诺、证据、申诉和自动后果如何形成闭环。
4. **Focusmate**：真人 body doubling 的成熟基准，适合验证“被陪着做”本身的价值。
5. **Tiimo**：视觉日程、AI 共创计划和温和提示的成熟 ADHD 产品基准。
6. **Project AIRI**：桌面角色、语音和可替换 AI 能力的开源基准；它不是执行监督产品，但很适合研究陪伴表现层。

如果更想研究中国用户和移动端普及产品，可把 **Focusmate** 换成 **番茄 ToDo**；如果更想研究“微信里的主动伙伴”，应加看 **Cyberboss** 和 **CATO 提醒猫**，但后者公开证据仍很弱。

两篇小红书笔记核验后，建议再加两个**机制样本**，但不要把它们误当成“用户量大的成熟产品”：一是 Physical AI 黑客松的 **“小黑脸”赛博监工原型**（“小黑脸”是评论区出现的称呼，正式项目名未确认），它把电脑进程、屏幕图像与摄像头视线检测组合起来；二是作者“福圓童子”的**未上线个人 AI 伴侣/专注频道原型**，它用实时视频、语音与角色对话制造高强度在场监督。

## 用户给出的 4 条线索：目前能确认什么

| 线索 | 当前判断 | 能确认的亮点 | 证据强度 |
|---|---|---|---|
| 小红书笔记一：“48h 黑客松，我们给 ADHD 们做了个赛博监工” | 已确认是作者“凯文丘比”发布的 2026 Hong Kong Physical AI Hackathon 原型。相关评论称其为“**小黑脸**”，但正式产品名仍未见独立项目页。作者明确说明系统由两部分组成：PC Agent 读取电脑后台任务进程并理解屏幕图像，判断活动是否与当前专注任务相关；电脑摄像头通过视觉算法判断视线是否在屏幕、是否发呆/涣散/移开。演示使用松灵 PiperX 机械臂。 | 多信号交叉判断，而不是仅靠计时或单一摄像头；把数字活动、人的注意状态和实体角色/机械臂组合成 Physical AI 监督。 | **中—强**：架构和硬件由作者在原笔记评论中直接回答；“小黑脸”只是相关评论中的称呼，正式命名与上线状态仍不明确。[原笔记](https://www.xiaohongshu.com/explore/6a73421d00000000290330af) |
| 小红书笔记二：“ADHD 克星｜做了一个实时动态监督但尝试糊弄……” | 已确认是作者“福圓童子”的**个人开发、尚未上线**的软件原型，不是 PlanCoach、Focus Bear 等现有产品。作者已实现让多模态 AI 从语音中感知情绪、语调、状态与哼歌，支持实时语音通话和视频电话；视频电话分为聊天频道和专注频道。演示界面中的角色名为 Victor，但作者没有给软件公布正式产品名。 | 以“像真的有人在视频通话里看着你”为核心：摄像头画面、实时语音、角色人设与专注频道结合；标题和演示强调会识别用户糊弄，但帖子未披露具体判定规则、采样频率或误判处理。 | **强（原型事实）/弱（效果）**：功能、未上线状态与拟开源架构教程均来自作者原笔记和评论；没有公开产品页、实测账号或效果数据。[原笔记](https://www.xiaohongshu.com/explore/6a83b0440000000027020f06) |
| 截图一：CATO 提醒猫 | 可确认是“微信里的提醒猫/AI 猫”方向，但没有找到官网、官方商店页或官方文档。公开转载描述称可用自然语言/语音建立任务、拆解任务，到点提醒，未完成会继续追问，并可生成看板与日报；这些仍需产品方一手资料或实测确认。 | 微信原生触达；角色化对话降低建任务摩擦；“没完成就再来问”比一次性通知更接近提醒升级。 | **弱—中**：用户截图是一手界面证据；功能主要来自转载，规模数字不采信。[公开转载](https://post.smzdm.com/p/agolo0xd/) · [公开使用描述](https://www.toutiao.com/w/1865319513054212/?source=m_redirect) |
| 截图二：可听、可说的 Neuro-sama 风格开源桌面 AI | **高置信识别为 Project AIRI**。官方仓库明确写明受 Neuro-sama 启发，提供实时语音对话、语音识别、Live2D/VRM 等方向；GitHub API 当日为 **48,089 Stars**。但截图所称“六个平台都能装”不能照搬：当前官方仓库首页明确列出的成熟入口是 Web、macOS、Windows，移动端/其他形态应在深调时逐项验证。 | 可自托管与自选模型；语音输入输出；可换角色身体；桌面常驻；后续可扩展游戏和屏幕上下文。 | **强**：身份、能力方向和热度有官方仓库/API 支持；“六平台”仅有截图，证据不足。[GitHub](https://github.com/moeru-ai/airi) · [GitHub API](https://api.github.com/repos/moeru-ai/airi) |

## 候选池总表

证据等级：**强**＝官方商店、GitHub/API 或平台方奖项；**中**＝产品官网自述；**弱**＝二手转载或由标题推断。

| 产品 | 定位与平台 | 亮点功能 | 使用量/代表性信号（调研时点） | 对 Jarvis 的启发 | 证据 |
|---|---|---|---|---|---|
| **PlanCoach** | ADHD 友好的抗拖延 App；iPhone/iPad/Apple Watch，Apple Silicon Mac 可运行 | AI 把模糊任务拆到第一步；多种教练人格；导航模式只显示当前步骤；渐进披露、偏航修正、语音输入和持续“贴脸提醒” | 中国区 App Store **826 个评分、4.8**；官网声称 10 万+用户，后者仅作厂商口径 | Jarvis 不只要判断“偏离”，还要在启动困难时给出极小、可立即执行的下一步；监督角色可按阻力类型换方法，而非只换语气 | **强**：平台、功能和评分有 [App Store](https://apps.apple.com/cn/app/plancoach-%E8%AE%A1%E5%88%92%E6%95%99%E7%BB%83-%E5%B0%8F%E7%BA%A2%E4%B9%A6%E6%8A%96%E9%9F%B3%E7%88%86%E6%AC%BE%E6%8A%97%E6%8B%96%E5%BB%B6app/id6748287561)；用户数与贴脸提醒来自[官网](https://plancoach.freemindworkshop.com/) |
| **Focus Bear** | 为 AuDHD 用户设计的跨端例程与分心拦截器；Windows/macOS/iOS/Android | 识别当前应用或网站；工作时阻断不相关内容；AI 判断网页与工作是否相关；晨晚例程逐步引导；“Late No More”可把视觉提醒升级为语音 | Google Play **10K+ 下载、89 个评分**；不是大体量产品，但机制与 Jarvis 最接近 | 深调重点应是相关性判断、误判纠正、低多巴胺替代内容、跨电脑和手机的升级节奏，以及用户如何绕过拦截 | **强**：[Google Play](https://play.google.com/store/apps/details?id=com.focusbear) · [官网](https://www.focusbear.io/) |
| **“小黑脸”赛博监工原型** | 2026 Hong Kong Physical AI Hackathon 项目；PC Agent + 摄像头视觉 + 松灵 PiperX 机械臂 | 后台任务进程与屏幕图像共同判断数字活动是否相关；视线/发呆/精神涣散检测；实体机器人在场 | 原笔记调研时约 **61 赞、20 收藏、14 评论**；这是黑客松传播信号，不是用户量，未见可下载产品 | 与 Jarvis 的核心监督机制高度重合，尤其适合对比“活动证据 + 人体状态”融合的收益、隐私成本和误判；机械臂更适合作为概念对照而非首版方案 | **中—强**：作者原笔记与答复；正式项目页、代码、数据策略和效果测试尚缺。[原笔记](https://www.xiaohongshu.com/explore/6a73421d00000000290330af) |
| **福圓童子实时视频专注原型（未命名）** | 未上线的个人 AI 伴侣软件；演示为桌面端视频通话/专注频道 | 多模态语音情绪与状态理解；实时语音/视频通话；聊天频道与专注频道分流；可配置角色式高强度监督 | 原笔记调研时约 **5,465 赞、1,734 收藏、462 评论**；作者明确说尚未上线，因此不能把互动量当用户量 | 证明“角色 + 视频通话 + 持续语音回合”能制造很强的被陪伴/被监督感；Jarvis 应重点研究低打扰触发、摄像头授权、人格边界和防糊弄判定透明度 | **强（演示与状态）/弱（效果）**：[原笔记](https://www.xiaohongshu.com/explore/6a83b0440000000027020f06) |
| **Forfeit** | 以承诺合约和损失后果推动执行；iOS/Android，另有 Mac 侧约束能力 | 先定义任务、截止时间和失败后果；照片/延时摄影/GPS/健康数据/屏幕使用等证据；AI 或人工复核；申诉；可罚款、联系监督人、封 App；Overlord AI 会追问和采取动作 | Google Play **10K+ 下载、约 335 个评分**；美国 App Store **330 个评分、4.8**。官网更大的累计数字是厂商自报，不作独立事实 | Jarvis 可以借鉴“承诺版本锁定—证据—判断—申诉—结果”链路，但应避免金钱惩罚和羞辱；尤其值得研究证据类型与申诉治理 | **强**：[官网](https://www.forfeit.app/) · [Google Play](https://play.google.com/store/apps/details?id=app.forfeit.forfeit) · [App Store](https://apps.apple.com/us/app/forfeit-habit-contracts/id1633125787) |
| **Focusmate** | 真人一对一视频 body doubling；Web | 预约 25/50/75 分钟；系统匹配搭档；开场互报目标，保持摄像头开启并各自工作，结束汇报进展 | 官网声称已完成 **900 万+场**、每天有来自 150+国家的数千用户使用；这是产品方披露，未独立审计 | 最小但强力的监督有时只是“另一个人稳定在场 + 开始和结束汇报”。Jarvis 可模拟轻量社会在场感，但不要假装真人 | **中**：[官方工作流程](https://www.focusmate.com/how-it-works/) · [官方业务页的场次口径](https://www.focusmate.com/business/) |
| **Tiimo** | 面向神经多样性用户的视觉日程和 AI 规划器；iPhone/iPad/Apple Watch/Web，Android 能力在持续补齐 | 彩色时间线、视觉计时器、AI Co-Planner、任务拆解和优先级、灵活重排、温和的声音/动画/触觉提示 | 2025 Apple **iPhone App of the Year**；美国 App Store **14K 个评分、4.6**。App Store 文案中的 300 万下载为开发者自述 | ADHD 产品不必都“严厉”：把时间变得可见、减少选择、允许柔性重排，也能降低执行负担；可作为 Jarvis 承诺卡和日视图的体验基准 | **强**：[Apple 2025 获奖公告](https://www.apple.com/newsroom/2025/12/apple-unveils-the-winners-of-the-2025-app-store-awards/) · [App Store](https://apps.apple.com/us/app/tiimo-planning-focus-to-do/id1480220328) · [官网](https://www.tiimoapp.com/) |
| **Focus Friend** | 可爱 Bean 伙伴 + 专注计时/手机 App 拦截；iOS/Android | 用户专注时 Bean 织袜子；完成获得装饰奖励；中断会呈现角色失落；Deep Focus 阻断分心 App；休息时装饰房间 | Google Play **1M+ 下载、约 7.5K 评分**，并获 2025 Google Play 年度最佳 App | 角色并不需要复杂对话即可产生社会在场感；但“角色因用户失败而伤心”与 Jarvis 的无负担陪伴原则冲突，值得作为反例测试 | **强**：[Google Play 年度榜](https://play.google.com/store/apps/editorial?hl=zh&id=mc_apps_cmp_bestof2025_fcp) · [App Store](https://apps.apple.com/us/app/focus-friend-by-hank-green/id6742278016) |
| **Project AIRI** | 自托管开源 AI VTuber/数字伙伴；当前官方重点为 Web、Windows、macOS | 实时语音、STT/TTS、Live2D/VRM、可接云或本地模型，试验游戏与上下文能力 | GitHub API **48,089 Stars**；MIT | 将“监督核心”和“角色身体/语音/模型供应商”拆成可替换层；Jarvis 第一版不必照搬其重型 Live2D/3D 栈 | **强**：[GitHub](https://github.com/moeru-ai/airi) · [GitHub API](https://api.github.com/repos/moeru-ai/airi) |
| **Cyberboss** | 把本地 Codex/Claude Code 接入微信的开源生活 Agent Bridge；本机 + 微信 | 时间戳与时间感、随机/自主唤醒、主动追问、自动日记和生活时间线，可调用 MCP/本地工具并发送文件和表情 | GitHub 约 **1.3K Stars**（当日量级） | 比 CATO 更可核验的“微信主动伙伴”参考：研究主动消息节流、时间上下文、记忆与工具调用边界；但它依赖编码 Agent，不是成熟消费 App | **强**：[GitHub](https://github.com/WenXiaoWendy/cyberboss) · [GitHub API](https://api.github.com/repos/WenXiaoWendy/cyberboss) |
| **MineContext** | 开源、主动式上下文感知 AI 伙伴；Windows/macOS 桌面 | 定时截屏与视觉理解，自动生成活动、总结、洞察和待办；本地优先，可接 OpenAI 兼容的本地模型 | GitHub API **5,468 Stars**；Apache-2.0 | 它展示了“看见数字工作上下文”的能力上限，也提醒 Jarvis 坚持非内容活动证据的重要性：全屏截图虽强，但隐私与成本完全不同 | **强**：[GitHub](https://github.com/volcengine/MineContext) · [GitHub API](https://api.github.com/repos/volcengine/MineContext) |
| **Structured** | 视觉日计划和时间线；iPhone/iPad/Mac/Apple Watch | 拖拽时间线、Inbox、子任务、番茄钟、AI 自动排日程；Replan 会重排漏做任务 | 美国 App Store **164K 个评分、4.8**；开发者在商店文案中声称 150 万活跃用户，后者不独立采信 | 研究“漏做后如何优雅重排”，避免 Jarvis 把计划变更等同于失败；也可对照承诺修订功能 | **强**：[App Store](https://apps.apple.com/us/app/structured-daily-planner-todo/id1499198946) · [官网](https://structured.app/) |
| **Goblin Tools** | 为神经多样性用户设计的单点 AI 小工具；Web/iOS/Android | Magic ToDo 拆任务；Compiler 把脑内倾倒变行动项；Estimator 估时；Taskmaster 一次只带用户做一个任务 | Google Play **100K+ 下载、约 1.5K 评分**；美国 App Store **3K 评分、4.8** | “AI 做很窄的一件事”可能比全能助手更低摩擦；Jarvis 可把拆解、估时和逐项引导作为独立能力，而不是都塞进聊天 | **强**：[官网](https://goblin.tools/) · [Google Play](https://play.google.com/store/apps/details?id=com.goblintools) · [App Store](https://apps.apple.com/us/app/goblin-tools/id6449003064) |
| **Llama Life** | 以“做完清单”而非“维护清单”为核心的 ADHD 任务计时器；Web/iOS/Android | 每项任务独立倒计时、可视化饼图、结束时间、超时正计时、周期小铃声、随机选任务、AI 拆解、一次只看一项 | Google Play **10K+ 下载、约 140 评分**；体量不大但 ADHD 时间盲设计很有代表性 | Jarvis 可以区分“任务截止提醒”和“执行中周期性注意力召回”；估时与实际超时数据也可服务复盘 | **强**：[官网功能页](https://llamalife.co/features) · [Google Play](https://play.google.com/store/apps/details?id=com.llamaapp) |
| **Finch** | 自我照护宠物 + 目标/习惯/情绪记录；iOS/Android | 完成自我照护目标为宠物提供能量和奖励；反思、呼吸、情绪日志和轻量日检视 | Google Play **10M+ 下载、约 590K 评分**；美国 App Store **738K 评分、4.9** | 证明角色化正反馈有大规模吸引力；但“照顾宠物才能成长”与 Jarvis 无负担陪伴冲突，适合作为边界对照 | **强**：[Google Play](https://play.google.com/store/apps/details?id=com.finch.finch) · [App Store](https://apps.apple.com/us/app/finch-self-care-pet/id1528595748) |
| **Forest** | 游戏化番茄钟和手机戒断；iOS/Android/浏览器扩展 | 专注时种树，离开导致树枯萎；App 白名单/拦截；定时阻断；好友共同种树；专注统计 | Google Play **10M+ 下载、约 810K 评分**；商店文案中的 6000 万用户为产品方自述 | 最成熟的“损失厌恶 + 可视化积累”基准；可研究轻后果如何有效，但不宜把失败转为道德压力 | **强**：[Google Play](https://play.google.com/store/apps/details?id=cc.forestapp) |
| **番茄 ToDo** | 国内番茄钟、学霸模式与自习室；iPhone/iPad/Apple Watch | 学霸模式、白名单与 App 拦截、专注悬浮窗、白噪音、统计、自习室、习惯计时 | 中国区 App Store **94 万评分、4.9** | 国内用户的强基准；对照 Jarvis 的差异应是“承诺 + 电脑活动证据 + 升级提醒 + 回顾”，而非再做一个番茄钟 | **强**：[App Store](https://apps.apple.com/cn/app/%E7%95%AA%E8%8C%84todo-%E6%9E%81%E7%AE%80%E9%AB%98%E6%95%88%E8%87%AA%E5%BE%8B%E7%95%AA%E8%8C%84%E9%92%9F/id1242689729) |
| **滴答清单 / TickTick** | 通用全平台任务、日历、习惯和番茄钟；Web/Windows/macOS/iOS/Android/Wear OS | 自然语言建任务、多提醒、日历时间线、番茄钟、习惯、统计、跨设备同步 | Google Play **10M+ 下载、约 161K 评分**；中国区 App Store **14 万评分、4.9** | 它是“功能齐全”的基线。Jarvis 不应在清单广度上竞争，而应聚焦承诺确认、过程监督、误判治理和事后复盘 | **强**：[Google Play](https://play.google.com/store/apps/details?id=com.ticktick.task) · [中国区 App Store](https://apps.apple.com/cn/app/di-da-qing-dan-dai-ban-shi/id626144601) · [功能页](https://ticktick.com/features?language=en_US) |

## 按产品机制分组

| 机制 | 最值得看的产品 | Jarvis 应关注的问题 |
|---|---|---|
| 任务太模糊、无法启动 | PlanCoach、Goblin Tools、Llama Life | 拆到多细才不会增加阅读负担？是先问用户，还是生成候选步骤后让用户确认？ |
| 做着做着跑偏 | Focus Bear、Forfeit、“小黑脸”赛博监工 | 用什么证据判断；是否需要叠加进程、屏幕图像与视线；多久干预；如何允许“这是相关工作”；如何避免误判和过度惩罚？ |
| 需要有人在场 | Focusmate、福圓童子视频专注原型、Flow Club（备选） | 纯社会在场感能否由桌面角色替代；实时视频是否真的增加效果；何时必须真人；摄像头带来什么隐私成本？ |
| 时间盲和日程过载 | Tiimo、Structured、Llama Life | 时间如何可视化；错过后怎样重排；计划变更与违约如何区分？ |
| 角色化正反馈 | Focus Friend、Finch、Forest | 角色存在感如何帮助坚持；哪些“宠物伤心/枯萎”机制会制造照顾义务或愧疚？ |
| 主动消息与跨端追问 | CATO、Cyberboss | 什么时候主动；多快追问；怎样止损；隐私摘要显示到什么程度；如何防止消息轰炸？ |
| 桌面数字伙伴 | Project AIRI、MineContext | 角色渲染、语音、模型、记忆和监督规则应怎样解耦；要不要读取屏幕内容？ |

## 建议的深调顺序

### 第一组：直接验证 Jarvis 的核心差异

- **“小黑脸”赛博监工原型**：向作者/团队补访任务相关性判断、屏幕与摄像头采样、机械臂实际反馈、误判和隐私；它是与 Jarvis 当前设想最接近的本土机制样本。
- **Focus Bear**：实测它如何识别/拦截不相关网站、误判时能做什么、语音升级是否扰人。
- **Forfeit**：走完一次从承诺创建、证据提交、判断到申诉的全链路。
- **PlanCoach**：观察从自然语言任务到第一步、导航执行、偏航修正和完成反馈的摩擦。
- **Focusmate**：至少完成 2—3 场，验证“开场承诺—持续在场—结束汇报”的真实心理效果。

### 第二组：塑造 Jarvis 的体验层

- **Tiimo**：重点看视觉时间线、AI 共创计划、遗漏后重排和温和提示。
- **Project AIRI**：重点看角色常驻、语音回合、模型配置、资源占用和屏幕上下文授权。
- **福圓童子视频专注原型**：待作者发布入口或架构后，重点验证它如何识别“糊弄”、何时开口、怎样维持角色连续性，以及长时视频的成本与隐私。
- **Focus Friend / Finch 二选一**：前者研究专注角色，后者研究大规模陪伴和奖励循环。

### 第三组：国内渠道和市场基线

- **CATO + Cyberboss**：共同研究“微信里的主动伙伴”，但 CATO 必须先找到真实入口并实测。
- **番茄 ToDo + 滴答清单**：只做基线，不需要像直接竞品一样深挖；重点回答 Jarvis 为什么不是另一个番茄钟/清单。

## 深调前还需补证的事项

1. “小黑脸”仍需补项目正式名称/团队页、代码或演示入口、机械臂具体干预、观察数据的保存范围与效果测试。
2. 福圓童子原型需等待产品入口或作者承诺的架构教程；“能识别糊弄”仍只是演示主张，需核验判定信号、误判纠正、资源/API 成本和隐私。
3. CATO 需要获取真实产品入口、运营主体、隐私政策和可测试账号；目前功能主要来自截图与转载。
4. Project AIRI 需按当前 release 逐个平台实装，不能用截图里的“六平台”代替当前可用性测试。
5. 对所有声称“AI 实时监督”的产品，都应实际测试：观察源、采样频率、误判、绕过方式、离线行为、数据保存与申诉机制。
