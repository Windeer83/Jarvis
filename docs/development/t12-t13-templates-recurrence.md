# T12–T13 承诺模板与重复计划纵切

## 已交付

- 模板保存名称、承诺类型、常用时长、目标、软件/网站、监督模式、提醒、活动分类规则和休息设置，但不保存日期，也不会自行创建承诺。
- 模板支持新建、修改与归档。由模板生成的承诺会冻结当时的规则和休息设置；之后修改或归档模板不会改写既有承诺。
- 使用模板仍先生成候选卡片，并允许仅覆盖本次的开始时间、时长、目标与监督设置；只有确认后才正式写入。
- 重复计划支持每天、每周指定星期、有限起止范围及不连续指定日期。一次确认在同一 SQLite 事务内生成相互独立的发生项。
- 电脑型批次会同时检查既有承诺和批内跨午夜冲突。任一发生项冲突时整批零写入，并指出冲突日期；相邻时段允许，线下发生项可以重叠。
- 重复计划与发生项均可在重启后恢复。跳过只标记状态并保留身份；修改范围为仅本次、本次及以后、整个计划，三者有真实可观察差异。
- 已经开始、结束或跳过的发生项不可覆盖修改。整个计划范围只改变仍未开始的发生项。
- Core 仍是 SQLite 唯一写者；Desktop 只通过当前会话命名管道提交命令并渲染 Core 投影。

## 明确未做

本纵切不实现每月规则、节假日顺延、交易日历、RRULE、无限生成或版本事件框架。

## 验证

```powershell
$dotnet = "D:\Desktop\codex\20260806时间管理项目\Jarvis\.tools\dotnet\dotnet.exe"
& $dotnet build Jarvis.slnx --configuration Release
& $dotnet test Jarvis.slnx --configuration Release --no-restore
& $dotnet format Jarvis.slnx --verify-no-changes --no-restore
& $dotnet list Jarvis.slnx package --vulnerable --include-transitive
```

高层测试使用真实 SQLite 和可控时钟，覆盖模板冻结、三种日期集合、批量冲突零写入、重启恢复、跳过保留历史、三种修改范围、历史不可覆盖，以及被跳过发生项不再采样活动或发送开始提醒。
