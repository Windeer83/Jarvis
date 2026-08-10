# Domain Docs

工程 Skill 在探索代码库前，应读取与当前工作相关的领域文档。

## 阅读顺序

- 根目录的 `CONTEXT.md`。
- `docs/adr/` 中与当前工作相关的 ADR。

如果这些文件还不存在，直接继续，不要报告错误，也不要为了填充目录而提前创建空文档。

`domain-modeling`、`grill-with-docs` 和 `improve-codebase-architecture` 会在实际澄清术语或作出重要决策时创建和维护这些文件。

## 文件结构

本仓库采用 single-context 布局：

```text
/
├── CONTEXT.md
├── docs/
│   └── adr/
└── src/
```

## 使用统一术语

Issue、需求、测试、代码和文档应使用 `CONTEXT.md` 中定义的领域术语。

如果所需概念尚未记录，应判断这是错误的新叫法，还是值得通过 `domain-modeling` 补充的领域概念。

## ADR 冲突

如果新方案与现有 ADR 冲突，必须明确指出冲突，而不是静默覆盖已有决定。
