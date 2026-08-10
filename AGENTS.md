## Agent skills

### Issue tracker

本仓库的需求、PRD 和开发任务使用 GitHub Issues 管理。详见 `docs/agents/issue-tracker.md`。

### Triage labels

使用五个默认分诊标签：`needs-triage`、`needs-info`、`ready-for-agent`、`ready-for-human`、`wontfix`。详见 `docs/agents/triage-labels.md`。

### Domain docs

本仓库采用 single-context 领域文档布局：根目录 `CONTEXT.md` 与 `docs/adr/`。详见 `docs/agents/domain.md`。

### 最小充分设计

- 以满足已确认当前需求的最小充分设计为默认，避免过度设计。新增服务、进程、抽象、安全层、依赖或工作流前，先指出它正在解决的当前用户问题；没有已确认问题时，将设想记录为后置需求及重新评估条件。
- 优先采用平台自带能力和成熟组件。只有现有方案无法满足已确认的功能、隐私、可靠性或恢复边界时，才引入自建基础设施或多层机制。
- 复杂度会实质改变体验、成本或风险时，先说明最简单方案的具体取舍并取得用户确认。新决定与既有 ADR 冲突时，创建明确的取代记录并同步当前规格，不静默叠加两套方案。
