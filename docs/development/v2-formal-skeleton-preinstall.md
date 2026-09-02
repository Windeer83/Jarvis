# V2 正式骨架：安装与真机验收交接

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

## 2026-09-01 正式真机验收结果

- Windows MSI 与手机 APK 均安装为 `0.2.0`；手机正式包使用长期私钥签名，APK v3 签名验证通过。
- 首次打开扫码页时发现 ZXing 运行时缺少 `androidx.core.content.ContextCompat`。正式工程已显式加入与 `compileSdk 36` 匹配的 `androidx.core:core:1.17.0`，正式构建会运行 `verifyScannerRuntimeClasspath` 防止该依赖再次漏包。修复后同一真机扫码不再崩溃并成功配对。
- 正式包的使用情况、悬浮窗、通知、精确闹钟与华为后台运行状态均显示可用；没有通过 adb 绕过权限页面。
- 抖音、B站、小红书和手机微信均在策略执行期间显示 Jarvis 不透明全屏覆盖层。
- 原因留空时五分钟临时开放被拒绝；填写原因后只开放当前抖音，五分钟后重新阻断。`TemporaryAccessStarted` 与 `TemporaryAccessEnded` 均进入 Core 事件账本。
- 完全停止 Windows Core 后，手机仍按缓存策略阻断 B站；Core 恢复后离线事件成功补传。
- 原策略在预定结束时间本地到期，覆盖层释放，抖音保持正常前台，并产生 `PolicyExpired` 事件。
- 用户此前豁免的省电模式、八小时运行和正式通知入口仍记为未测风险，不得写成通过。

## 正式日常使用前仍需解决

- 校园 Wi-Fi `ncepu-wifi` 开启客户端隔离：电脑与手机虽处同一 IPv4 子网，但双向 Ping 和手机到 Core TCP 均失败。手机热点下局域网配对与同步通过，证明应用链路有效。热点继续作为当前配对、策略下发和故障兜底；策略成功缓存后，执行与本地到期不依赖热点。
- 2026-09-02 决定先验证系统自带 Bluetooth PAN，不先安装 Tailscale。目标电脑已经发现 Mate 70 Pro+ 的 NAP 服务和 Microsoft BTHPAN 适配器；若真机证明本地双向可达、地址稳定，且睡眠/重启后最多只需重新加入一次 PAN，则把它作为同处一地时的默认同步链路。完整门禁见 `docs/research/bluetooth-pan-hotspotless-sync-spike.md`。
- 手机暂不在电脑旁，因此蓝牙 PAN 的地址稳定性、自动重连、校园 WLAN 共存和 30 分钟真实同步尚未验收；在通过前不得写成正式可用，也不为它预建多 endpoint、mDNS 或自定义蓝牙协议。
- Windows 当前由系统联网提示生成了 `Jarvis.Core.exe` 的 Public 入站允许规则。正式网络方案确定时必须把 Core 约束到实际同步接口和手机来源，不能依赖校园网恰好隔离客户端。
- Tailscale 保留为蓝牙 PAN 失败后的成熟备选，Windows Wi-Fi 热点继续作为配对与故障兜底；不建设 Jarvis 云中继。
