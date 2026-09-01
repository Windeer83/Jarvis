# V2 正式骨架：安装前交接

## 当前完成边界

- Windows 正式代码已包含 `Jarvis Core`、`Jarvis Desktop`、对话优先入口和手机配对窗口。
- Core 通过局域网 HTTPS 发布当前手机策略；协议版本、证书固定、一次性配对、令牌哈希、撤销、事件幂等和旧版本拒绝均已进入正式代码。
- `com.jarvis.mobile` 只包含最近状态、快速文字记录、四个目标应用阻断、原因必填的五分钟临时开放和权限入口；没有无障碍服务、云账号、通知读取或应用内容采集。
- 二维码生成采用 QRCoder 1.8.0，扫码采用 ZXing Android Embedded 4.3.0；归属记录见根目录 `THIRD-PARTY-NOTICES.md`。
- Mate 70 Pro+ 已通过的执行机制为 `UsageStatsManager + TYPE_APPLICATION_OVERLAY + 可见前台服务 + 持久策略 + 到期闹钟 + BOOT_COMPLETED`。省电模式、八小时运行和真实通知入口仍是用户豁免的未测风险，不得写成通过。

## 一键重建安装前产物

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-v2-preinstall.ps1
```

脚本执行 .NET Release 构建和完整测试、Android 本地单元测试和 lint、debug/unsigned-release APK 打包以及 Windows 自包含 MSI 打包，并把校验和写到 `artifacts\v2-preinstall\SHA256SUMS.txt`。由于当前工作区路径含中文，而 Gradle Windows 测试运行器存在 classpath 问题，脚本只在构建期间创建一个 ASCII 临时盘符；源文件不会被复制或移动。

## 正式安装前唯一必须由所有者完成的选择

Android 覆盖升级永久依赖同一发布私钥。不要把 debug key 当作正式身份。首次正式安装前，在一个会离线备份的位置生成 keystore 并签名：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\sign-mobile-release.ps1 `
  -KeystorePath "E:\你的离线备份位置\jarvis-mobile-release.jks"
```

脚本交互读取密码，不把密码写入仓库或命令行参数。keystore 与密码任一丢失后，都无法覆盖升级已经安装的 `com.jarvis.mobile`。这项身份决定不能由自动构建代替。

## 回到电脑旁后的正式安装流程

1. 核对 `SHA256SUMS.txt`，安装 `Jarvis-0.2.0-win-x64.msi`；Windows 防火墙询问时只允许“专用网络”。
2. 完成上面的长期签名并核对 `apksigner verify` 输出。
3. 手机连接 USB，运行 `scripts\install-mobile.ps1` 安装并打开已签名 APK；脚本会核对签名和目标机型，且不会用 adb 绕过权限页面。
4. 在手机界面依次打开“使用情况访问”“悬浮窗”“通知”“精确闹钟”，再在华为应用详情中完成后台运行设置并点“我已完成”。
5. 让电脑和手机处于同一可信局域网。Desktop 点击“手机监督”并生成二维码，手机扫描配对。
6. 创建一个短时电脑型承诺，依次打开抖音、B站、小红书和手机微信，验证立即覆盖；填写原因临时开放五分钟，验证到期恢复。
7. 断开 Wi-Fi 后确认原策略继续到原结束时间；结束后确认覆盖层释放。最后检查 Desktop 状态和事件记录。

正式验收前不要安装 debug APK；它仅是无长期签名时的构建/临时诊断候选。
