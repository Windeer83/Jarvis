# T26 密码保护备份、校验与恢复

## 交付边界

- 用户先选择由百度网盘客户端同步的专用本地子目录，并设置至少 12 字符的备份密码；可选择把密码保存到 Windows 凭据管理器。
- Core 使用 SQLite 一致性快照生成 WinZip AES-256 便携备份。压缩包严格只含 `manifest.json` 与 `jarvis.sqlite3`，生成后必须通过摘要、SQLite `integrity_check`、数据库版本和可打开性校验才记为成功。
- 自动任务每天生成一份每日备份，每月生成一份月度备份；T27 升级/迁移前和用户手动操作使用对应类型。默认保留每日 30 份、每月 12 份、升级前 3 份。
- 自动备份失败会保存在本地状态中，并按至少一小时节流重试，不会让 Core 每秒重复执行重型备份。新备份成功但旧文件清理失败时会明确提示磁盘空间或文件占用问题。
- “测试恢复”只在隔离目录解密和校验，不触碰正式数据库。正式恢复先完整校验并排队，完全退出并重新打开后才交换数据库；交换前保留 `restore-rollback.db` 本地回滚快照。
- 错误密码、损坏文件、摘要不符或当前版本不支持的数据库都不能进入正式恢复。
- 备份不包含 Windows 凭据管理器里的飞书或模型凭据。新电脑恢复后需要重新输入备份密码，并重新配置外部供应商凭据。
- Jarvis 只写用户选择的本地同步目录，不调用百度网盘 API、不保存网盘凭据，也不宣称云端已经上传。网盘客户端连续 24 小时未运行时，只在桌宠和托盘中每日最多提示一次“本地备份等待处理；云端状态未知”，不发送飞书提醒。

## 自动验证

```powershell
& ..\..\Jarvis\.tools\dotnet\dotnet.exe build Jarvis.slnx -c Release
& ..\..\Jarvis\.tools\dotnet\dotnet.exe test Jarvis.slnx -c Release
& ..\..\Jarvis\.tools\dotnet\dotnet.exe format Jarvis.slnx --verify-no-changes
& ..\..\Jarvis\.tools\dotnet\dotnet.exe list Jarvis.slnx package --vulnerable --include-transitive
```

`BackupScenarios` 覆盖手动备份、错误密码/损坏拒绝、隔离测试恢复、下次启动恢复与回滚副本、每日/月度自动备份、30/12/3 保留、自动失败节流，以及网盘客户端 24 小时本地提示。`SqliteMigrationScenarios` 覆盖 v8→v9 正常迁移、重启幂等与失败整体回滚。

## 人工验收保留项

1. 在“备份与恢复（T26）”选择百度网盘客户端同步的专用子目录，设置并确认备份密码；选择保存密码。
2. 点“立即生成并校验”，确认生成 `.jarvis-backup`，界面只显示本地校验成功和“云端状态未知”。
3. 选择该文件，先输入错误密码执行“隔离测试恢复”，确认拒绝；再输入正确密码，确认隔离校验通过且当前数据不变。
4. 使用一份可丢弃测试数据的备份执行“排队正式恢复”，完全退出并重新打开，确认数据回到备份时状态且数据目录保留 `restore-rollback.db`。
5. 在另一台机器或新的 Windows 用户环境恢复时，确认不会带入飞书/模型凭据，需要重新配置。
