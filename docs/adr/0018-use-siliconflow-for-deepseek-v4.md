# 通过硅基流动调用 DeepSeek V4 Flash 与 Pro

> 2026-08-15：固定按用途分层的模型路由已由 [ADR 0019](0019-make-siliconflow-model-user-selectable.md) 取代；硅基流动传输路径、凭据边界和费用记录仍以本文为准。

Jarvis 第一版的 DeepSeek V4 请求统一通过用户自己的硅基流动账号调用，不再把硅基流动密钥发送到 DeepSeek 官方端点。普通对话固定使用 `deepseek-ai/DeepSeek-V4-Flash` 并关闭思考模式；自然语言候选操作、每日复盘辅助和周期复盘辅助固定使用 `deepseek-ai/DeepSeek-V4-Pro`。路由只由 Core 根据已确认的请求用途决定，模型不能自行升级，故障时也不自动切换供应商。

请求使用硅基流动的 OpenAI 兼容端点 `https://api.siliconflow.cn/v1/chat/completions`。2026-08-15 核对的人民币单价为：Flash 缓存命中输入 0.02 元、普通输入 1 元、输出 2 元/百万 tokens；Pro 分别为 1 元、12 元和 24 元/百万 tokens。价格版本随每次调用记录，变更后必须更新预算估算，不能把当前价格当作永久事实。

凭据继续只保存在 Windows 凭据管理器。新凭据使用 `siliconflow` 槽；为兼容已经在旧界面上传的硅基流动密钥，Core 会只读回退到原 `deepseek` 槽，用户无需重新粘贴。删除本机凭据时同时清理新旧槽，但不会声称供应商后台的密钥已经撤销。

本决策取代 ADR 0012 中“直连 DeepSeek 官方 API”的传输路径；分层路由、用户自带凭据、确定性 Core 校验和费用硬上限仍保持不变。
