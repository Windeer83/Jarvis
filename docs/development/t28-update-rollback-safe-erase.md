# T28 手动更新、回滚与安全清除

## 手动更新

Jarvis 不检查、下载或静默安装更新。用户先从可信发布位置取得新版 MSI，再在 Desktop 的“更新与安全清除（T28）”页选择这个本机文件。

更新按以下顺序执行：

1. Core 核对文件存在、扩展名和 SHA-256。当前存在电脑型监督时默认返回 `update_active_supervision`，不做备份、不退出、不启动安装；用户可等待监督结束，或明确勾选“停止当前监督后更新”。
2. Core 使用 T26 的同一密码保护格式创建 `UpgradeOrMigration` 备份并完成解密、摘要、数据库版本和可打开性验证；未配置备份目录/密码、磁盘失败或校验失败都会中止更新。
3. Core 另在 `%LOCALAPPDATA%\Jarvis-Maintenance\update-*` 创建一致的数据库回滚快照，候选卡显示安装包摘要、两类恢复材料及二次确认短语。
4. 用户二次确认后，Core 把不含密码、聊天、活动标题或私人正文的维护请求交给独立 PowerShell 维护进程；Desktop 与 Core 完全退出后才运行 MSI。
5. 安装完成后，新 Core 以 `--health-check` 打开同一 SQLite、运行数据库迁移并启动真实 Core pipe；它再启动真实 Desktop 健康探针，由 Desktop 通过命名管道读取 Core 快照。只有程序文件、数据库和 Core↔Desktop 往返全部通过，才记录 `completed` 并重新启动 Core。
6. MSI 失败或健康检查失败时，维护进程恢复旧程序树、升级前数据库以及 Windows Installer 中缓存的旧版 MSI 登记；恢复数据库前后都会移除失败版本留下的 WAL、SHM 和 journal。三者均恢复才记录 `rolled_back`，否则记录 `rollback_incomplete` 并保留维护目录供手动恢复。诊断只含操作种类、状态、退出码、是否回滚和恢复目录，不含凭据或业务内容。

更新不会在后台自动触发，也不会因发现新版而中断监督。安装包在候选预览后若发生字节变化，确认会拒绝并要求重新预览。

## 安全清除

普通卸载继续遵循 T27：保留数据。永久清除是独立的双重确认流程：

1. 用户选择清除范围之外的目录并输入两次最终备份密码；第一次预览先生成并验证一份密码保护备份，让用户看到真实路径和范围。第二次确认时再生成并验证一份更新的最终备份，避免用户停留在预览页期间数据继续变化。目录若位于正式数据目录、维护目录或当前配置的 Jarvis 备份目录内，返回 `safe_erase_backup_inside_scope`。
2. 第一张预览卡逐项列出将删除的本机范围：Jarvis 数据目录（数据库、设置、费用记录等）、`%LOCALAPPDATA%\Jarvis-Maintenance` 中的维护请求/状态/诊断日志、已配置目录中名称匹配 `jarvis-*.jarvis-backup` 的本机备份、Windows 登录启动项，以及 `Jarvis/AI/siliconflow`、`Jarvis/AI/deepseek`、`Jarvis/Backup/password` 三个 Jarvis 凭据。
3. 用户输入完整短语进行第二次确认；Core 完全退出后，维护进程只在数据目录存在 `.jarvis-data-root` 根标记且路径不是磁盘根、用户目录或 LocalAppData 根时才递归删除。
4. 配置的备份目录只删除 Jarvis 命名的备份，保留无关文件；范围外最终备份必须仍存在。维护进程不调用百度 API、不删除百度云端文件、不读取或查找 Jarvis 之外保存的密码。

## 自动验证

```powershell
dotnet build Jarvis.slnx -c Release
dotnet test Jarvis.slnx -c Release --no-build
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-t28-maintenance.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-t28-update-success.ps1
```

本次自动证据：

- Release build 0 warning / 0 error；185/185 测试通过；
- 伪造损坏 MSI 真实触发维护进程失败路径，旧程序树、升级前数据库和失败版本留下的 SQLite WAL/SHM 均正确回滚，状态为 `rolled_back`；
- 安全清除真实删除隔离的 Jarvis 数据根、维护日志和命名备份，同时保留同目录无关文件及范围外最新最终备份；
- 构建并安装 0.1.0 自包含 MSI，再由维护进程更新到 0.1.1；新版 Core 启动真实 Desktop，并通过命名管道读取同一数据库快照，状态为 `completed`；
- 故意破坏升级后数据库使真实健康检查失败，程序文件、数据库和 Windows Installer 登记均恢复到 0.1.0，且旧 MSI 可正常卸载；
- 维护请求序列化回归确认不包含最终备份密码、监督目标文字或其他业务正文。

## 目标机人工验收

1. 保留一条正在监督的可丢弃承诺，选择新版 MSI，先不勾“停止当前监督”；确认明确被阻止且监督继续。
2. 再勾选停止选项，核对承诺进入待回顾、升级备份与回滚快照路径均显示；输入卡片短语后观察 Jarvis 退出、安装和重新启动。
3. 打开同一承诺、设置、复盘和备份状态，确认升级后可读；检查维护状态只显示退出码/状态/恢复目录，不含正文或密钥。
4. 在可丢弃测试账户中准备一个包含无关文件的备份目录，选择另一个目录保存最终备份；预览安全清除范围，错误短语应拒绝。
5. 第二次确认后，确认本机数据和 Jarvis 命名备份消失、无关文件及最终备份保留；百度客户端/云端文件不应被 Jarvis 操作。
