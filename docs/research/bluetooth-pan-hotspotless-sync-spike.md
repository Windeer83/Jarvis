# 蓝牙 PAN 免切热点同步：最小可行性与真机门禁

> 调研日期：2026-09-02  
> 目标设备：Windows 11 PC + HUAWEI Mate 70 Pro+（HarmonyOS 4.3）  
> 约束：保留“手机主动轮询 + 证书指纹固定 HTTPS + 单机 Core 权威”，不增加 Jarvis 云后端，不先改业务协议。

## 结论

**先做一次不改代码的 Bluetooth PAN（BTPAN）真机 spike；通过后，把它作为同处一地时的常驻本地同步链路。Tailscale 暂不安装，保留为 BTPAN 不稳定时的第二选择；Windows Wi-Fi 热点继续作为配对和故障兜底。**

## 2026-09-02 目标真机结果

最小可用门禁已经通过，BTPAN 可作为同处一地时的默认同步链路：

- Mate 70 Pro+ 开启“通过蓝牙共享网络”后，Windows 11 以 Access Point 角色加入成功；Windows BTHPAN 获得 `192.168.44.83/24`，手机 PAN 地址为 `192.168.44.1`。
- 手机到电脑 BTPAN 地址 Ping 5 次全部成功；Core 在 `192.168.44.83:42731` 收到了来自 `192.168.44.1` 的真实 HTTPS/TCP 连接。
- 电脑同时保留校园 WLAN `ncepu-wifi`；WLAN interface metric 为 20，BTPAN 为 40，默认互联网路由没有被蓝牙链路抢走。
- 在只暴露 BTPAN endpoint 时重新配对一次，手机进入 `Ready`。正式 30 分钟测试承诺成功下发，手机登记了精确到期闹钟。
- 人工断开并重新加入 PAN 后，手机侧下游接口从 49 变为 50，证明发生了真实重连；PC 地址仍为 `192.168.44.83`，30 秒内恢复 `Ready` 和真实 TCP 连接。
- 重连后打开 B站仍出现 Jarvis 不透明全屏覆盖层，证明已缓存策略在链路中断时继续执行；提前结束承诺后，手机在下一轮同步清除策略，再开 B站不再覆盖。

这次结果证明日常创建、修改和结束监督不再需要切手机热点。尚未把“Windows/手机重启后自动加入 PAN”写成已通过；如果开机后 Windows 没有自动恢复，只需在系统蓝牙设备页重新加入一次 PAN，不需要重新扫码，也不影响手机已经缓存的监督。手机本次测试时 WLAN 处于关闭状态，因此“手机同时连接校园 WLAN”仍是未测的兼容性观察项；电脑校园 WLAN 与 BTPAN 共存已经通过。

原计划中的五次重连、30 分钟锁屏、PC 睡眠和双端重启不再作为第一阶段功能阻断项。它们属于日常可靠性观察；当前产品范围是一台私人电脑和一部就在身边的手机，而且缓存策略、离线执行与重连补传已经分别验收。若后续实际使用出现频繁掉线或地址变化，再按回退表进入 Tailscale spike，不预先建设多传输层。

这是当前最小充分方案。目标手机已经与电脑完成蓝牙配对；Windows 本机枚举到了手机的 Personal Area Network NAP 服务和 Microsoft BTHPAN 网络适配器，只差实际加入 PAN。华为官方支持页明确把 HUAWEI Mate 70 Pro+ 和 HarmonyOS 4.3 列入适用范围，并说明设备会识别当前移动网络或 WLAN，且可开启“通过蓝牙共享网络”。[华为：共享网络给其他设备](https://consumer.huawei.com/cn/support/content/zh-cn15801195/) Microsoft 的 Windows 11 官方步骤则是：配对后，在手机侧开启蓝牙共享，在 Windows 设备页对手机的 Personal Area Network 选择“加入”，角色选择 Access Point。[Microsoft：Connect to a Bluetooth network in Windows](https://support.microsoft.com/en-US/Windows/Hardware/Bluetooth/connect-to-a-bluetooth-network-in-windows)

若目标机能维持 BTPAN，手机可继续连接校园 WLAN；电脑也继续连接校园 WLAN，同时两者之间多出一条不经过校园 AP 的本地 IP 链路。Jarvis 的手机客户端仍主动访问电脑 HTTPS 端口，不需要 Tailscale、账号、VPN 权限或新协议。

官方文档**没有承诺**以下三件事；前两项的最小路径已经由上述真机证据回答，睡眠/重启可靠性继续作为日常观察项：

1. 手机作为 NAP 时，手机本机上的 Jarvis 是否能主动访问作为 PANU 的 Windows 客户端；
2. Windows 从手机获得的 BTPAN IPv4 是否跨断连、睡眠和重启保持不变；
3. Windows 是否会在开机/唤醒后自动重新加入 PAN，或至少一次加入后能稳定保持整日。

若后续发现地址跨重启变化，当前“只保存一个 endpoint”的手机实现会失联；若只需要每次开机点一次，但不需要每次布置监督再操作，则仍满足当前“不要每次开热点”的需求。

## 推荐拓扑

```text
校园 WLAN ─────────────── Windows 11 PC
    │                         │
    └──── Mate 70 Pro+        │ Core HTTPS :42731
             │                │
             └── Bluetooth PAN┘
                 手机 = NAP；Windows = PANU
                 Jarvis Mobile ──HTTPS──> Windows BTHPAN IPv4
```

选择“手机为 NAP、Windows 为 PANU”，原因是本机已经实际发现手机的 NAP 服务，且 Microsoft 官方正好支持 Windows 加入手机的 Access Point。Bluetooth SIG 的 PAN Profile 定义了 NAP、PANU 和 GN 三种角色；NAP 可为远端网络提供接入。[Bluetooth SIG：Personal Area Networking Profile](https://www.bluetooth.com/specifications/specs/personal-area-networking-profile-1-0/)

不优先反过来让 Windows 充当 NAP。那会要求 Windows 启动自己的 Bluetooth tethering、再让华为手机选择电脑作为网络接入点；当前已有硬件证据支持的是手机 NAP 路线，而且反向方案更可能改变手机默认上网路径。

## 为什么 BTPAN 值得先试

### 用户操作最少

- 手机保持校园 WLAN，不用切到电脑 Wi-Fi 热点；
- 不安装第三方客户端，不注册账号，不占 Android 的 VPN 槽；
- 手机和电脑已经配对，理论上只需手机开启一次“通过蓝牙共享网络”，Windows 加入一次 PAN；
- 一旦链路常驻，之后每次新建、修改、结束监督都继续由现有 5 秒主动轮询完成。

现有手机前台服务已经每 5 秒尝试同步，网络变化不要求新增 Android 权限；BTPAN 对应用表现为普通 IP 网络。对应实现见 `mobile/app/src/main/java/com/jarvis/mobile/MobileRuntimeService.java:66`。

### 不依赖校园 AP 允许终端互访

手机和电脑之间的数据走蓝牙 PAN，而不是从手机经校园 AP 访问电脑的校园 WLAN 地址。校园网仍可分别作为两台设备的互联网出口；对 Jarvis 本地同步而言，即使某些需认证 WLAN 不允许被完整共享，只要 BTPAN 本地子网能够双向通信即可。后一句是网络拓扑推论，必须用目标机验证，不能由华为文档单独保证。

华为官方同一篇说明同时覆盖 HarmonyOS 4.3 的 WLAN 共享和蓝牙共享，但也明确提醒设置项因机型而异、部分需认证 WLAN 无法分享。[华为：共享网络给其他设备](https://consumer.huawei.com/cn/support/content/zh-cn15801195/)

### 现有 HTTPS 模型可以保留

BTPAN 只是新增一个本地 IP 接口。Kestrel 目前监听任意 IP 的固定端口，手机继续发起 HTTPS，请求仍使用现有证书指纹和 bearer token；配对、策略版本、幂等事件、离线缓存均无需改变。

当前客户端只接受固定叶证书的 SHA-256 指纹，见 `mobile/app/src/main/java/com/jarvis/mobile/PinnedHttpClient.java:35`。因此在 spike 阶段，用同一 Core 证书改成 BTPAN IPv4 不会改变证书身份。正式版本仍应处理现有 `HostnameVerifier` 跳过主机名校验的问题；本次研究不借 BTPAN 扩大这个例外，也不修改证书或网络代码。

## 当前实现的两个实际接缝

### 1. 二维码未必会选择 BTPAN 地址

Core 启动时只选一个 IPv4 endpoint，并优先挑选“Up、有 IPv4 网关、类型为 WLAN/Ethernet”的第一个私网地址，见 `src/Jarvis.Core/MobileLanHost.cs:85`。当校园 WLAN 与 BTPAN 同时在线时，枚举顺序没有被业务规则固定，二维码可能仍给出校园 WLAN 地址。

因此 spike 的首次 BTPAN 配对不要先写地址选择器。可在确认 BTPAN 本地可达后，临时断开 PC 的校园 WLAN、保持 BTPAN 在线，重启 Core 并生成一次新二维码；手机仍可保持自己的校园 WLAN。随后恢复 PC 校园 WLAN。只有 BTPAN 全部门禁通过后，才决定是否增加一个显式“手机同步接口”设置。

### 2. 手机只保存一个 endpoint

手机配对数据目前只有单一 endpoint，见 `mobile/app/src/main/java/com/jarvis/mobile/ConnectionStore.java:10`。因此 BTPAN 分配给 PC 的地址必须稳定；不能假设 DHCP 会永久复用同一地址，也不应为了猜测中的变化预先建设多 endpoint 路由器或 mDNS。

如果地址在五次断连/重连、PC 重启或手机重启后变化，则“不改代码的 BTPAN”判定失败。此时优先改用 Tailscale 的稳定私网地址，而不是让用户频繁重新配对；只有用户拒绝 Tailscale且 BTPAN 其余可靠性足够时，才评估最小 endpoint 更新机制。

## 与其他方案的比较

| 方案 | 日常动作 | 对现有协议 | 主要风险 | 当前判断 |
|---|---|---|---|---|
| **手机 NAP + Windows BTPAN** | 理想为一次开启/加入；最差可能每次开机加入一次 | 原样保留；只换本地 endpoint | 地址可能变化、PAN 可能不自动重连、手机到 PANU 的主动访问需实测 | **先做最小 spike** |
| **Tailscale 私网** | 一次安装、登录和 VPN 授权后常驻 | 原样保留；需显式选择稳定 `100.x` endpoint | 第三方控制面、HarmonyOS 后台保活、占用唯一 VPN 槽 | **BTPAN 失败时的首选** |
| **Windows Wi-Fi 移动热点** | 手机需切离校园 WLAN，或依赖不确定的自动切网 | 已经实机通过 | 当前用户体验问题正是反复切热点 | **配对/故障兜底** |
| **USB 共享网络** | 每次插线，可能还要开 USB 共享 | 原样保留；只换 endpoint | 离开电脑或无 USB 时不可用 | **诊断兜底，不作为日常方案** |
| **自写 Bluetooth RFCOMM / BLE 协议** | 可做成后台自动连接 | 必须重写 HTTPS 传输与双端代码 | 新协议、安全、重连、兼容和权限成本最大 | **不做** |

Windows 官方支持把 Wi-Fi、以太网或蜂窝网络通过 Wi-Fi 或 Bluetooth 共享，但移动热点仍要求一端作为热点并显式连接。[Microsoft：Use your Windows device as a mobile hotspot](https://support.microsoft.com/en-US/Windows/Experience/Connectivity-Networking/use-your-windows-device-as-a-mobile-hotspot) 华为也支持 USB 共享当前移动网络或 WLAN，但线缆依赖不符合日常监督场景。[华为：共享网络给其他设备](https://consumer.huawei.com/cn/support/content/zh-cn15801195/)

Tailscale 的优势仍然明确：节点获得跨物理网络稳定的 `100.x` 地址，直连失败时可退回 DERP 中继。[Tailscale：IP addresses](https://tailscale.com/docs/concepts/ip-and-dns-addresses)；[Tailscale：Connection types](https://tailscale.com/docs/reference/connection-types) 但 Android 安装需要 VPN 授权，而 Android 同时只能运行一个 VPN。[Tailscale：Install on Android](https://tailscale.com/docs/install/android)；[Android：VpnService](https://developer.android.com/reference/android/net/VpnService) 对当前“一台电脑、一部就在身边的手机”，在验证系统自带 BTPAN 前直接引入它仍偏重。

## 不改代码的真机 spike（已执行）

### 前置动作

1. 手机继续连接校园 WLAN并保持蓝牙开启。
2. 手机进入“设置 > 移动网络 > 个人热点 > 更多共享设置”，开启“通过蓝牙共享网络”；若实际菜单为“移动网络共享”，按华为说明中的另一入口操作。
3. Windows 进入“设置 > 蓝牙和设备 > 设备”，展开已经配对的 Mate 70 Pro+，在 Personal Area Network (PAN) 旁选择“加入”，再选“Access Point > Connect”。
4. 不撤销当前 Jarvis 配对，不改防火墙，不改 route；先只验证网络层。

### 第一关：链路和方向

连接后立即记录：

```powershell
Get-NetAdapter -IncludeHidden
Get-NetIPConfiguration
Get-NetRoute -AddressFamily IPv4 | Sort-Object RouteMetric,InterfaceMetric
Get-NetConnectionProfile
```

验收条件：

- Microsoft BTHPAN 适配器为 `Up` 并获得 IPv4；
- 手机仍显示校园 WLAN 已连接；
- 手机能访问 `https://<PC-BTPAN-IPv4>:42731/v1/health`；
- 电脑普通互联网访问仍走校园 WLAN，不被手机 PAN 抢走默认路由。

Windows 默认用基于链路速度的 Automatic Metric 在多个默认网关间选择优先路径，较快接口通常获得较低 metric；但目标机仍要检查实际路由，不能只依赖理论。[Microsoft：Automatic Metric for IPv4 routes](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/automatic-metric-for-ipv4-routes)

若 BTPAN 抢走默认互联网路由，只提高 BTPAN 的 interface metric，再复测；直接相连的 PAN 子网因前缀更具体，仍应优先于默认路由。不要删除校园路由或全局关闭 Automatic Metric。

### 第二关：endpoint 与真实同步

只有第一关通过后才操作：

1. 记录 PC-BTPAN IPv4；
2. 临时断开 PC 校园 WLAN，保持 BTPAN；
3. 重启 Core，确认配对窗口显示的是 BTPAN IPv4；
4. 撤销旧手机配对并重新扫码一次；
5. 恢复 PC 校园 WLAN；
6. 创建一条 5–10 分钟测试监督，验证策略下发、手机事件回传和到期解除；
7. 确认 Core/手机状态持续在线至少 30 分钟，期间不启用 Wi-Fi 热点。

这一步会更换设备令牌，但不应更换 Core 证书。若配对窗口仍选择校园地址，停止 spike，记录适配器类型和枚举结果，不手工篡改数据库或手机 SharedPreferences。

### 第三关：日常可靠性观察

依次执行并记录地址、在线恢复时间和是否需要用户点击：

1. BTPAN 断开/重连五次；
2. PC 睡眠 5 分钟后唤醒；
3. 手机锁屏 30 分钟，期间从电脑创建一条测试监督；
4. 关闭再开启手机蓝牙；
5. 重启 Windows；
6. 重启手机；
7. 进行一次正常校园工作时段的续航/发热观察。

第一阶段的最小硬门槛：

- PC-BTPAN IPv4 不变化；
- 所有已连接时段均能在 30 秒内恢复 Jarvis 同步；
- Windows 默认互联网路由保持校园 WLAN；
- Windows 防火墙能够把 Jarvis 端口限制在 BTPAN 本地地址/手机来源，而不是继续对所有 Public 网络开放；
- 已缓存策略在 BTPAN 断开时继续执行，到期本地解除，恢复后事件只补传一次。

PC 睡眠、双端重启、手机校园 WLAN 共存和长时间续航是日常可靠性观察项。若 Windows 或手机重启后只需重新加入一次 PAN，但一整天内不再为每次监督开热点，视为满足当前需求；若每次睡眠、锁屏或短暂离开都要重新加入，则判定失败。

## 安全边界

- 保留 HTTPS、固定证书指纹、设备 token 和协议版本；蓝牙配对不能替代应用层认证。
- BTPAN 网络按不受信任的本地网络处理。Windows 网络类别保持 Public，仅为 Core 的 TCP 42731 创建最窄入站规则；不要把整个 BTPAN 或校园网络设为 Private 来换取方便。
- Core 当前监听任意 IP，因此防火墙是暴露面边界。正式规则应限制本地地址为 PC-BTPAN IPv4，并尽可能限制远端为手机 PAN 地址/子网。
- 不开启文件共享、网络发现或其他入站服务；Jarvis 不需要这些能力。
- 不自动控制手机蓝牙、热点或网络设置。当前阶段只使用系统设置和已有前台服务。

## 回退规则

| 真机结果 | 决策 |
|---|---|
| BTPAN 双向可达、地址稳定、锁屏/睡眠可靠；最多每次开机加入一次 | 采用 **BTPAN 常驻 + Wi-Fi 热点配对/故障兜底** |
| 本地可达，但地址跨重启变化 | 不正式采用无代码 BTPAN；优先测试 Tailscale 稳定 endpoint |
| 地址稳定，但 Windows 睡眠后频繁掉线或手机无法主动访问 PC | 放弃 BTPAN，测试 Tailscale |
| 用户拒绝第三方 VPN，且 BTPAN 失败 | 保持当前“需要下发时开 Wi-Fi 热点 + 手机离线执行”边界 |
| 未来出现不在同一地点也要即时下发的真实需求 | 再评估 Tailscale；BTPAN 只覆盖近距离场景 |

## 对第一阶段的建议

第一阶段不应立刻关闭网络问题，也不应实现 Tailscale。正确顺序是：

1. 以最小 ADR/手册更新确认“BTPAN 为同地默认、热点为配对/兜底”；
2. 把 Windows 防火墙限制为 BTPAN 接口上的 TCP 42731；
3. 只有 endpoint 选择确实影响重新配对时，才增加显式手机同步接口选项，不建设自动多传输层；
4. 若日常观察出现地址变化或频繁掉线，直接回到已经完成调研的 Tailscale 连通性 spike，不自建 Jarvis 云中继。

## 一手来源

- [Huawei：共享网络给其他设备（含 Mate 70 Pro+ / HarmonyOS 4.3）](https://consumer.huawei.com/cn/support/content/zh-cn15801195/)
- [Huawei：HUAWEI Mate 70 Pro+ 规格参数](https://consumer.huawei.com/cn/phones/mate70-pro-plus/specs/)
- [Microsoft：Connect to a Bluetooth network in Windows](https://support.microsoft.com/en-US/Windows/Hardware/Bluetooth/connect-to-a-bluetooth-network-in-windows)
- [Microsoft：Use your Windows device as a mobile hotspot](https://support.microsoft.com/en-US/Windows/Experience/Connectivity-Networking/use-your-windows-device-as-a-mobile-hotspot)
- [Microsoft：Automatic Metric for IPv4 routes](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/automatic-metric-for-ipv4-routes)
- [Bluetooth SIG：Personal Area Networking Profile](https://www.bluetooth.com/specifications/specs/personal-area-networking-profile-1-0/)
- [Tailscale：How Tailscale assigns IP addresses](https://tailscale.com/docs/concepts/ip-and-dns-addresses)
- [Tailscale：Connection types](https://tailscale.com/docs/reference/connection-types)
- [Tailscale：Install on Android](https://tailscale.com/docs/install/android)
- [Android：VpnService](https://developer.android.com/reference/android/net/VpnService)
