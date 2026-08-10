# Issue tracker: GitHub

本仓库的需求、PRD 和开发任务使用 GitHub Issues 管理。所有操作通过仓库内的 `gh` CLI 执行。

## 常用操作

- 创建：`gh issue create --title "..." --body "..."`
- 阅读：`gh issue view <number> --comments`
- 列表：`gh issue list --state open --json number,title,body,labels,comments`
- 评论：`gh issue comment <number> --body "..."`
- 添加标签：`gh issue edit <number> --add-label "..."`
- 删除标签：`gh issue edit <number> --remove-label "..."`
- 关闭：`gh issue close <number> --comment "..."`

仓库地址由当前目录的 `git remote` 推断。

## Pull requests as a triage surface

**PRs as a request surface: no.**

外部 Pull Request 默认不进入需求分诊队列。如需改变，可将上述值改为 `yes`。

## Skill 约定

- “publish to the issue tracker”表示创建 GitHub Issue。
- “fetch the relevant ticket”表示执行 `gh issue view <number> --comments`。
- `to-spec` 产生的 PRD 发布为 GitHub Issue。
- `to-tickets` 产生的任务发布为相互关联的 GitHub Issues。

## Wayfinding

`wayfinder` 使用一个主 Issue 保存决策地图，并创建子 Issue 记录研究、原型、讨论和任务。

- 主地图标签：`wayfinder:map`
- 子任务标签：`wayfinder:research`、`wayfinder:prototype`、`wayfinder:grilling` 或 `wayfinder:task`
- 优先使用 GitHub 原生子 Issue 和依赖关系。
- 如果原生依赖不可用，在 Issue 顶部使用 `Blocked by: #<number>`。
- 领取任务时将 Issue 分配给自己。
- 完成后记录结论并关闭 Issue。
