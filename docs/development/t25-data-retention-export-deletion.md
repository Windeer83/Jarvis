# T25 数据保留、加密导出与永久删除

## 交付边界

- 详细监督时间线默认保留 90 天，用户可设置 7–3650 天。Core 每日最多执行一次到期归档。
- 到期活动区段先按本地自然日切分并生成每日汇总，再删除区段、提醒、回应和分类纠正明细。跨午夜活动不会被整段计入前一天。
- 承诺、承诺版本、完成结果、承诺回顾、每日复盘和周期复盘不因时间线到期而删除。
- Desktop 的“数据保留与导出（T25）”页可按日期查看详细事实、已归档每日汇总和承诺/回顾摘要。单次最长 367 个自然日，时间线超过 5000 条时明确显示截断。
- 导出只序列化上述数据投影，使用 PBKDF2-SHA256（210,000 次）与 AES-256-GCM 密码保护。密码至少 12 字符，不保存。
- 导出不含飞书/模型凭据、API Key、聊天历史、截图、按键、文件正文或个人成长档案；也不允许导出路径覆盖当前 SQLite 数据库。
- 永久删除先生成 10 分钟有效的候选，列明日期、范围和估算记录数；只有逐字输入 Core 生成的确认短语才执行。
- 删除有三层范围：仅详细时间线；时间线+每日汇总；或所选过去日期的全部监督记录。最后一层不允许包含今天或未来。

## 自动验证

```powershell
& ..\..\Jarvis\.tools\dotnet\dotnet.exe build Jarvis.slnx -c Release
& ..\..\Jarvis\.tools\dotnet\dotnet.exe test Jarvis.slnx -c Release
& ..\..\Jarvis\.tools\dotnet\dotnet.exe format Jarvis.slnx --verify-no-changes
& ..\..\Jarvis\.tools\dotnet\dotnet.exe list Jarvis.slnx package --vulnerable --include-transitive
```

`DataGovernanceScenarios` 覆盖到期归档保留承诺/回顾、跨午夜切分、密码导出排除凭据/聊天、错密码拒绝、数据库覆盖拒绝、永久删除短语与范围限制。`SqliteMigrationScenarios` 覆盖 v7→v8 正常迁移、重启幂等与失败整体回滚。

## 人工验收保留项

- 在“数据保留与导出（T25）”查看一个含监督事实的日期，确认时间线与承诺/回顾数量可理解。
- 选择文件、输入至少 12 字符密码并导出，确认文件已生成，密码框在成功后清空。
- 只用可丢弃的测试日期验证删除：预览后故意输入错短语应被拒绝，输入完整短语后只有所选范围消失。
