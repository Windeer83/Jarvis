# 桌宠开源项目调研：Jarvis 应该重做还是基于现有项目改造

> 调研日期：2026-08-06。Star 数取自 GitHub API 当日快照，会随时间变化。只采用项目仓库、README、LICENSE 和 GitHub API 等一手资料。

## 结论先行

对目前还是空仓库的 Jarvis，默认建议是**新建自己的产品代码，但复用成熟项目验证过的设计**，而不是直接把某个完整项目 fork 过来改名：用 BongoCat 的 Tauri 跨平台窗口方案作为桌面外壳参考，用 OpenPets 的事件、宠物包和插件边界作为架构参考，并从一开始使用自己的角色与动画素材。

只有目标高度匹配时才值得直接基于现有项目：

- 想最快做出“轻量、跨平台、响应键鼠输入”的桌宠：优先 fork `ayangweb/BongoCat`。
- 想最快做出“插件化、可接 Codex/Claude/MCP”的桌面伴侣，并能接受 Electron：优先评估 fork `alvinunreal/openpets`。
- 只做 Windows，核心是投喂、状态、养成、MOD：优先嵌入 `VPet-Simulator.Core`，不要搬走整个 VPet 应用和它的美术资源。
- 产品本质就是“显示编码代理状态”，并愿意让衍生产品遵守 AGPL：才考虑 fork `clawd-on-desk`。

## 筛选结果

| 项目 | Star 快照 | 技术栈 / 平台 | 授权 | 最值得借鉴 | 作为 Jarvis 基座 |
|---|---:|---|---|---|---|
| [ayangweb/BongoCat](https://github.com/ayangweb/BongoCat) | [22,415](https://api.github.com/repos/ayangweb/BongoCat) | Tauri、Vue、TypeScript、Rust；Windows/macOS/Linux(X11) | MIT | 透明置顶窗口、键鼠/手柄动作、自定义模型、跨平台打包 | **推荐参考；功能一致时可 fork** |
| [Open-LLM-VTuber/Open-LLM-VTuber](https://github.com/Open-LLM-VTuber/Open-LLM-VTuber) | [13,120](https://api.github.com/repos/Open-LLM-VTuber/Open-LLM-VTuber) | Python + Web/桌面前端、Live2D；跨平台 | 代码 MIT；Live2D 示例素材另行授权 | 本地/云 LLM、ASR、TTS、视觉、Live2D 情绪映射 | 不建议整仓 fork；适合拆模块参考 |
| [LorisYounger/VPet](https://github.com/LorisYounger/VPet) | [6,584](https://api.github.com/repos/LorisYounger/VPet) | C#、WPF；Windows | 代码 Apache-2.0；内置动画另有条款 | 完整养成状态、动画系统、创意工坊、MOD/插件、可嵌入 Core | **Windows 养成路线首选** |
| [rullerzhou-afk/clawd-on-desk](https://github.com/rullerzhou-afk/clawd-on-desk) | [5,848](https://api.github.com/repos/rullerzhou-afk/clawd-on-desk) | Electron、JavaScript；Windows/macOS/Linux | AGPL-3.0；内置角色美术不在 AGPL 授权内 | 多编码代理 hooks、权限气泡、事件到动画的映射、多屏与主题包 | 功能最贴近编码伴侣，但授权约束强 |
| [Adrianotiger/desktopPet](https://github.com/Adrianotiger/desktopPet) | [1,129](https://api.github.com/repos/Adrianotiger/desktopPet) | C#；Windows | GitHub 未识别到许可证 | XML 动画定义、窗口边缘/任务栏碰撞、多屏行为 | 只适合阅读思路；没有明确许可不要复制代码 |
| [isHarryh/Ark-Pets](https://github.com/isHarryh/Ark-Pets) | [1,034](https://api.github.com/repos/isHarryh/Ark-Pets) | Java；当前主要为 Windows | GPL-3.0 | 模型库、角色筛选、物理/重力、窗口边缘站立、多宠物托盘管理 | 可参考引擎行为；不宜继承第三方角色/IP 素材 |
| [alvinunreal/openpets](https://github.com/alvinunreal/openpets) | [1,025](https://api.github.com/repos/alvinunreal/openpets) | Electron、TypeScript；Windows/macOS/Linux | MIT | 沙箱化插件 SDK、宠物格式、MCP、编码代理事件、本地隐私边界 | **AI/插件型 Jarvis 最接近的宽松授权基座** |
| [SeakMengs/WindowPet](https://github.com/SeakMengs/WindowPet) | [643](https://api.github.com/repos/SeakMengs/WindowPet) | Tauri、React、TypeScript；跨平台 | MIT | 多宠物、点击穿透、拖拽、自启动、自动更新 | 小而直观，但最后推送为 2025-04-11，适合读实现而非押注生态 |
| [zebangeth/PawPal](https://github.com/zebangeth/PawPal) | [565](https://api.github.com/repos/zebangeth/PawPal) | Electron、React、TypeScript；Windows/macOS | 代码 MIT；动画素材独立授权 | 休息/喝水提醒、专注模式、应用检测、本地统计 | 若 Jarvis 偏时间管理，这是功能定义的重要参考 |

## 重点项目解读

### 1. BongoCat：最成熟的轻量跨平台外壳

BongoCat 的定位很纯粹：宠物根据键盘、鼠标或手柄输入做动作，支持导入自定义模型，离线运行。Tauri 让它比典型 Electron 桌宠更适合“常驻但尽量轻”的目标。它的优点是社区体量最大、MIT 授权宽松、跨平台路径已经走通；短板是产品模型仍接近输入可视化器，没有养成、复杂插件或 AI 记忆。

如果 Jarvis 的 MVP 只是“透明置顶角色 + 拖拽 + 若干状态 + 自定义皮肤”，直接基于它改造能够减少大量操作系统窗口和打包问题。若 Jarvis 最终是 AI 助手或时间管理产品，则更适合只借鉴外壳与资源格式，避免把输入可视化的领域模型带进新系统。[README](https://github.com/ayangweb/BongoCat#readme) · [LICENSE](https://github.com/ayangweb/BongoCat/blob/master/LICENSE)

### 2. VPet：Windows 养成与 MOD 能力最完整

VPet 已经具备投喂、状态、工作、存档、大量动画、主题、物品以及代码插件，并把核心拆为可嵌入 WPF 应用的 `VPet-Simulator.Core`。如果 Jarvis 是 Windows 优先的“真正虚拟宠物”，它比从零重做状态机和内容系统更有价值。

风险有两个：WPF 基本锁定 Windows；代码授权和美术授权并不相同。仓库明确说明自带动画与图片有额外使用、署名和商业条款。因此更稳妥的做法是使用其 Core/NuGet 或学习接口设计，同时完全自制 Jarvis 的动画与角色资产。[README 与软件结构](https://github.com/LorisYounger/VPet#readme) · [LICENSE](https://github.com/LorisYounger/VPet/blob/main/LICENSE)

### 3. Clawd on Desk：编码代理联动做得最深

Clawd 能把 Codex、Claude Code、Cursor 等代理的思考、执行、权限请求、子代理和完成状态映射成宠物动画，还处理了多会话、多显示器、点击穿透、主题包、自动更新和权限气泡。若 Jarvis 的目标是“让 AI 工作状态可见”，这里几乎是一份现成的需求清单。

但它不是一个适合随意换皮的宽松基座：代码为 AGPL-3.0，衍生分发需要履行相应开源义务；内置角色和主题美术还明确保留权利，不能因为代码开源就直接复用。可以学习事件协议与状态优先级，但除非接受 AGPL，不建议直接 fork。[README](https://github.com/rullerzhou-afk/clawd-on-desk#readme) · [授权说明](https://github.com/rullerzhou-afk/clawd-on-desk#license)

### 4. OpenPets：最接近“通用 Jarvis 平台”的宽松基座

OpenPets 把桌宠、插件和编码代理集成分开：Electron 桌面宿主负责窗口和控制中心；宠物包有单独格式；插件 SDK 有权限、配额、存储、定时任务、命令和面板；MCP/CLI/代理事件通过独立包接入。这种边界很适合 Jarvis 后续增加番茄钟、提醒、日程、小游戏或 AI 工具。

它的优势是 MIT 和架构贴近，缺点是项目较新、社区与长期稳定性还未经充分验证，而且 Electron 的内存占用通常高于 Tauri。若目标是尽快验证插件型 AI 桌宠，可以 fork；若重视轻量常驻，则更建议在 Tauri 上重建同类契约。[README](https://github.com/alvinunreal/openpets#readme) · [架构文档](https://github.com/alvinunreal/openpets/tree/main/docs) · [LICENSE](https://github.com/alvinunreal/openpets/blob/main/LICENSE)

### 5. Open-LLM-VTuber：AI 语音与 Live2D 能力库，不是理想桌宠底座

该项目支持本地或云端 LLM、多种 ASR/TTS、语音打断、视觉输入、Live2D 表情和透明桌宠模式。它适合回答“Jarvis 是否应该能看、听、说”的技术问题，却已经是一个相当重的 AI 伴侣系统，而且项目正在讨论 v2 重写。直接 fork 会把大量模型部署、音频链路和前后端复杂度一起带入。

更合理的方式是等 Jarvis 的桌面内核稳定后，再按接口接入它验证过的 ASR/TTS/LLM 适配思路。代码虽按 MIT 描述，仓库内 Live2D 示例模型受单独许可约束，商用尤其需要重新准备资产。[README](https://github.com/Open-LLM-VTuber/Open-LLM-VTuber#readme) · [LICENSE](https://github.com/Open-LLM-VTuber/Open-LLM-VTuber/blob/main/LICENSE) · [Live2D 授权说明](https://github.com/Open-LLM-VTuber/Open-LLM-VTuber/blob/main/LICENSE-Live2D.md)

### 6. PawPal：时间管理方向的直接参考

PawPal 的角色会提醒休息、喝水，并根据当前应用提示用户结束分心。它的功能面不大，但把“宠物行为”和“生产力反馈”绑在一起，比单纯换动作更容易形成长期价值。当前 Windows 上的分心检测仍有缺口，因此它更像产品需求参考，而不是无需修改即可复用的完成品。[README](https://github.com/zebangeth/PawPal#readme) · [素材授权](https://github.com/zebangeth/PawPal/blob/main/ASSET_LICENSE.md)

## 对 Jarvis 的推荐架构

在尚未确定最终角色与功能前，可把可逆性最高的第一版定为：

1. **Tauri 2 + TypeScript 桌面宿主**：透明、无边框、置顶、可拖拽、可点击穿透、托盘、多屏、开机启动。
2. **独立宠物渲染器**：使用明确的宠物包 manifest，第一版只支持 PNG/WebP/GIF/APNG；把 Live2D/Spine 留成后续适配器。
3. **小型状态机**：`idle / moving / focused / reminding / thinking / success / error / sleeping`，外部事件只发送语义状态，不直接控制动画帧。
4. **事件总线与权限边界**：计时器、系统活动、Codex hooks、日历或 LLM 都作为适配器接入；插件只能声明并获得所需权限。
5. **素材与代码分离授权**：从第一天给代码、角色设定、动画、声音分别记录来源与许可证，避免将来商业化时重做全部资产。

这条“新建内核 + 有选择地借鉴”的路线保留 Jarvis 自己的产品身份，同时避开 WPF 的平台锁定、Electron 的常驻成本、AGPL 的分发限制，以及第三方角色资产的版权风险。

## 最终选择规则

- 你描述的第一版功能与某项目已有功能重合 **70% 以上**，且许可证能接受：fork。
- 重合低于 70%，只是窗口、托盘、动画或事件处理相似：新建项目，按许可证复用少量模块或设计。
- 需要商业闭源：优先 MIT/Apache-2.0 代码，逐项审查素材；避开 AGPL/GPL 基座和无许可证代码。
- 先做 Windows 单平台养成：VPet Core。
- 先做跨平台轻量桌宠：BongoCat/Tauri 路线。
- 先做可扩展 AI/生产力伴侣：OpenPets 的领域边界 + Tauri 宿主，是对 Jarvis 最平衡的组合。
