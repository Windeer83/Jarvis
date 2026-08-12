# T14 承诺修订与可解释历史

## 已交付

- 已成立的工作承诺可以复用现有表单进入修订模式。用户必须填写自然语言原因，并在候选卡中核对字段差异和 `vN → vN+1` 后再确认。
- 工作承诺 ID 保持稳定。首次确认和每次修订分别形成不可变版本快照；当前投影只指向最新版本，旧目标、时段、分类规则、提醒和休息设置仍可查看。
- 修订从 Core 确认时刻向后生效。已经开始的承诺不能倒改开始时间；已结束或已跳过的记录不能修订；冲突、过期候选和旧版本操作均零写入拒绝。
- 监督历史按版本保留活动区段、提醒、分类纠正与用户回应。明确误判只更新所绑定区段的有效分类，原分类事实和纠正说明不被覆盖。
- 修订后，旧版本提醒的气泡、声音、持续标记和待回应入口立即失效；偏离累计等已经发生的监督事实继续保留。
- 重复安排的时间调整也走候选确认：原因必填，每个受影响发生项分别追加新版本，并以预览时捕获的版本整体校验；任一项变化或冲突时整批不写。跳过仍是保留历史的独立状态事实。
- 点击“当前活动相关/分心”时绑定提醒触发时捕获的外部软件或网站、活动状态起点和承诺版本。打开 Jarvis 面板不会把分类目标替换成 Jarvis 自身；目标或版本变化后旧操作失效。

本纵切不实现取消、推迟、完成结论、承诺回顾、飞书卡片处理或 AI 自然语言解析；这些由后续纵切复用同一版本契约。

## 自动验证

```powershell
dotnet build Jarvis.slnx --configuration Release --no-restore
dotnet test Jarvis.slnx --configuration Release --no-build --no-restore
dotnet format Jarvis.slnx --verify-no-changes --no-restore
dotnet list Jarvis.slnx package --vulnerable --include-transitive
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-t08.ps1
```

高层场景使用可控时钟、可控活动输入和隔离的真实 SQLite 文件，覆盖前向生效、原因必填、禁止倒改、候选与旧操作失效、活动区段纠正、提醒与回应版本归属、重复调整的批量版本追加，以及数据库迁移和重启恢复。双进程 smoke 继续覆盖 Core/Desktop、命名管道、SQLite 重启恢复、bundled runtime 转发、Desktop 单实例和精确清理。

## 剩余人工观察

- 对一条尚未结束的承诺完成一次目标或结束时间修订，确认候选卡只显示变化且原因必填。
- 查看历史，确认同时出现 v1 与 v2，且 v1 内容和修改原因仍可阅读。
- 在任意外部软件中触发分类入口后打开 Jarvis，确认按钮仍显示并修改原外部软件，而不是 `Jarvis.Desktop.exe`。
