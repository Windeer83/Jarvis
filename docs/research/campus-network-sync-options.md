# 校园网客户端隔离下的手机同步方案

> 调研日期：2026-09-02  
> 适用范围：Jarvis V2-A、Windows 11 Core、HUAWEI Mate 70 Pro+（HarmonyOS 4.3 / Android 12 API 31）  
> 资料边界：只采用 Microsoft、Android、Huawei、Tailscale、WireGuard 与 Headscale 的官方文档或官方源码仓库。

## 结论

**当前 V2 最小正式方案应使用 Tailscale 作为“仅负责网络可达性”的私有传输层；Windows 移动热点作为故障降级和首次安装兜底；不建设 Jarvis 自有云中继。**

理由是：用户的核心问题不是热点能否临时配对，而是校园 Wi-Fi 开启客户端隔离后，每次下发新监督策略都要手动切网络。Tailscale 给电脑和手机分配跨物理网络稳定的私有地址，优先建立直接连接；直连受校园网隔离、NAT 或 UDP 限制时，可退回端到端加密的 DERP 中继。手机继续使用校园 Wi-Fi 或移动网络，无需改连热点。[Tailscale 地址与 DNS](https://tailscale.com/docs/concepts/ip-and-dns-addresses)；[连接类型](https://tailscale.com/docs/reference/connection-types)；[DERP](https://tailscale.com/docs/reference/derp-servers)

这不是把 Jarvis 业务数据迁到云端：工作承诺和事件仍由 Windows Core 权威保存，手机仍只缓存当前策略并离线执行；Tailscale 只提供虚拟私网。当前证书固定 HTTPS、设备令牌、幂等事件和离线到期机制全部保留。

推荐不是无条件的。若目标真机验证出现以下任一情况，应放弃 Tailscale 默认路线，改用 Windows 移动热点自动连接：

1. 校园网或所在地区无法稳定访问 Tailscale 协调服务和 DERP，导致锁屏后的策略同步持续失败；
2. HarmonyOS 在已经完成后台保护、休眠联网和始终开启 VPN 设置后，仍会停止 Tailscale；
3. 续航增量被用户判断为不可接受；
4. 用户必须同时运行另一款 Android VPN；Android 同一时间只允许一个活动 VPN；
5. 用户不接受 Tailscale 账号、控制平面及设备/连接元数据由第三方处理。

反过来，只有当 Windows 热点能在本机启动后自动开启、手机能可靠自动重连、校园网共享上网可用且符合校园网规则时，它才足以替代 Tailscale 成为默认路线。

## 当前实现基线

现有代码与 [ADR-0020](../adr/0020-one-computer-one-mobile-pinned-https-sync.md) 已经具备正确的业务边界：

- Windows Kestrel 在固定端口监听 HTTPS；
- 手机主动轮询 `/v1/sync`，电脑不主动连手机；
- 首次二维码保存一个 endpoint、电脑证书 SHA-256 指纹和设备令牌；
- 手机每 5 秒尝试同步，失败时继续执行最后一个已确认策略到原定结束时间；
- 电脑是唯一业务权威，手机事件使用 UUID 幂等补传。

真正造成校园网失败的是 endpoint 目前是普通局域网 IPv4。校园 AP 的客户端隔离阻止手机直接访问电脑；业务协议本身不需要重做。

当前 `MobileLanAddressResolver` 只识别 `10/8`、`172.16/12`、`192.168/16`，不会选取 Tailscale 使用的 `100.64.0.0/10` 地址；手机端又只保存一个 endpoint。这是采用 Tailscale 时需要改变的主要接缝。

另外，现有 Android 客户端固定叶证书指纹后直接跳过 hostname 校验。Android 官方将不安全的 `HostnameVerifier` 视为风险；正式修改 endpoint 时应一并恢复标准 hostname 校验，而不是继续扩大这一例外。[Android：Unsafe HostnameVerifier](https://developer.android.com/privacy-and-security/risks/unsafe-hostname)

## 方案比较

| 维度 | Windows 移动热点 | Tailscale 私有虚拟网络 | WireGuard / Headscale 自托管 | Jarvis 自建云中继 |
|---|---|---|---|---|
| 日常操作 | 手机保存热点后可能自动重连；但电脑重启、睡眠恢复和无人连接时热点是否保持，必须实测 | 一次安装、登录和 VPN 授权后可常驻；日常无需切 Wi-Fi | 一次配置后可常驻，但服务器、密钥、域名和升级由自己维护 | 用户可无感，但开发和运维成本最高 |
| 校园网隔离 / NAT | 手机直接连电脑建立的网络，绕开校园 AP 的终端互访限制；电脑再共享校园网 | 直连失败时可走 DERP；只要必要的出站路径可达，就不要求校园 AP 放行终端互访 | 裸 WireGuard 没有 Tailscale 的自动协调与 DERP；通常要一台公网 VPS/hub | 双端都向公网服务建立出站连接，可绕过隔离 |
| 手机网络 | 手机不再直连校园 Wi-Fi，而是经电脑热点上网 | 手机保持校园 Wi-Fi/移动网络；不使用 exit node 时只把 tailnet 路由送入 Tailscale | 与 Tailscale 一样占用 VPN，但自建 hub 可能让更多流量绕路 | 无需占 VPN；需维持轮询或长连接 |
| 后台与耗电 | 不增加手机 VPN；电脑 Wi-Fi 和供电压力更高 | 增加一个 Android `VpnService` 和常驻状态；耗电必须真机量化 | 同样占 VPN；persistent keepalive 会增加周期流量 | Jarvis 自己承担后台连接、重连和心跳 |
| 隐私 | 业务与连接都留在本机网络；无新增第三方 | 业务流量端到端加密，但 Tailscale 处理设备、IP、连接等元数据 | Headscale 可自持控制平面；VPS 服务商仍可看到网络元数据 | 若不额外做端到端加密，服务端可见策略；即使加密也会保存队列和元数据 |
| 第三方依赖 | 仅 Windows/Huawei 系统能力和校园网 | Tailscale SaaS、身份提供商、客户端和 DERP | VPS、域名、证书、Headscale/DERP 或 WireGuard 运维 | 云厂商、域名、数据库/队列、监控和自研协议 |
| 对现有 HTTPS | 选择热点适配器地址；证书/令牌协议可保留 | 选择稳定的 Tailscale 地址或 MagicDNS 名；证书/令牌协议可保留 | 类似 Tailscale，但地址、DNS 和路由自行维护 | 需要把 request/response 改成队列或隧道协议；现有端到端 HTTPS 不能直接假设穿透 |
| 故障降级 | 热点关闭后手机执行已缓存策略；新策略无法到达 | Tailscale 不可用后手机执行已缓存策略；可人工启用热点 | VPS/控制面/DERP 故障都要自处置 | 服务端故障、队列积压、鉴权和重放都要自处置 |
| V2 判断 | **保留为降级** | **推荐默认** | 后续隐私路线，不进入当前交付 | **不做** |

## 方案一：Windows 移动热点

### 能解决什么

Windows 10/11 官方支持把 Wi-Fi、以太网或蜂窝连接再次通过 Wi-Fi 或蓝牙共享；手机可用密码或二维码连接。因此“电脑连校园网，手机连电脑热点”在操作系统能力上成立。[Microsoft：Windows 移动热点](https://support.microsoft.com/en-US/Windows/Experience/Connectivity-Networking/use-your-windows-device-as-a-mobile-hotspot)

手机和电脑此时不再依赖校园 AP 允许终端互访。校园网只看到电脑的上游连接，Windows 负责共享上网。这是对官方共享能力和当前网络拓扑作出的工程推论，不能替代实际校园网测试，也不能证明学校允许网络共享。

### 为什么不作为当前默认

- 手机必须改连电脑热点，网络路径和可用性依赖电脑处于唤醒状态；
- 热点在重启、睡眠、断开校园网和长时间无客户端后的恢复行为取决于 Windows、驱动和电源状态；
- 同一块 Wi-Fi 网卡能否一边连接校园 Wi-Fi、一边稳定发射热点必须在当前 Intel AX211 上实测；
- 校园认证、门户或服务条款可能限制共享；
- 若由 Jarvis 自动开启热点，需要调用 Windows tethering API 并处理用户授权、能力声明和失败状态，而不是假设设置开关永远保持。

Microsoft 提供 `NetworkOperatorTetheringManager.StartTetheringAsync` 启动热点；带每会话配置的重载在 Windows 11 24H2 引入，并要求 `wiFiControl` capability。当前电脑系统版本满足 API 版本门槛，但 Jarvis MSI/桌面进程能否获得所需能力仍须做最小原型，不能只按文档推断。[Microsoft：StartTetheringAsync](https://learn.microsoft.com/en-us/uwp/api/windows.networking.networkoperators.networkoperatortetheringmanager.starttetheringasync)

华为 Wi‑Fi+ 可以对曾连接网络进行自动连接，但系统还会按网络质量切换网络，因此只能作为便利能力，不能作为可靠性保证。[Huawei：Wi‑Fi+](https://consumer.huawei.com/en/support/content/en-us15674126/)

### 若作为降级，最小实现

1. 用户在 Windows 设置中配置一个固定、非敏感 SSID 和强密码；
2. 手机保存该网络，只在 Tailscale 不可用时连接；
3. Core 明确枚举热点适配器地址，不能沿用“第一个有默认网关的私网地址”；
4. 防火墙只允许热点/私网接口访问 Jarvis 同步端口；
5. 热点关闭或连接失败时，电脑显示“手机离线，本地策略继续”，不自动宣称阻断已同步。

不为 V2 编写自制 Wi-Fi 管理器，也不尝试控制手机选择哪个网络。

## 方案二：Tailscale

### 为什么最符合“无感同步”

Tailscale 为每台设备分配稳定的 `100.x.y.z` 地址；只要设备仍注册在同一 tailnet，物理网络从校园 Wi-Fi 切到热点或移动网络，地址不变。[Tailscale：IP 地址](https://tailscale.com/docs/concepts/ip-and-dns-addresses)

连接按“直接 UDP → Peer Relay（若配置）→ DERP”降级；三种路径都使用 WireGuard 端到端加密。校园客户端隔离通常只会让同 AP 直连失败，并不要求 Jarvis 自己实现 NAT 穿透。DERP 在 hard NAT、严格防火墙或 UDP 不可用时转发密文，服务器拿不到 Jarvis HTTPS 明文。[Tailscale：连接类型](https://tailscale.com/docs/reference/connection-types)；[DERP](https://tailscale.com/docs/reference/derp-servers)；[防火墙端口](https://tailscale.com/docs/reference/faq/firewall-ports)

个人方案目前为免费档，足以覆盖一台电脑和一部手机，但价格和权益属于可变外部条件，实施时仍应记录依赖。[Tailscale pricing](https://tailscale.com/pricing)

### 手机代价

Tailscale Android 8 及以上受支持，安装时需要用户批准 VPN 配置。[Tailscale：Android 安装](https://tailscale.com/docs/install/android) 目标手机没有 Google Play 也可使用 Tailscale 官方发布的 APK；官方 Android 仓库明确给出稳定 APK 渠道。[Tailscale Android 官方仓库](https://github.com/tailscale/tailscale-android)

Android 同时只能有一个活动 VPN，启动第二个会停用第一个；这可能与校园 VPN、代理、防火墙或其他 VPN 冲突。[Android `VpnService`](https://developer.android.com/reference/android/net/VpnService) Tailscale 不应配置 exit node；Jarvis 只需要 tailnet 内地址，不应把抖音、B站、小红书、微信或普通互联网流量转发到电脑。

HarmonyOS 4.3 必须把 **Tailscale 本身** 单独加入后台保护；Jarvis 已有的后台权限不会自动授予另一个应用。华为官方建议对需要持续运行的应用关闭自动管理、允许后台活动、关闭电池优化，并在必要时保持休眠联网和锁定最近任务。[Huawei：应用无法后台运行](https://consumer.huawei.com/cn/support/content/zh-cn00428704/) 华为也在 HarmonyOS 4.3 文档中提供“始终开启 VPN”设置，但目标 APK 是否出现在该设置中仍须真机确认。[Huawei：始终开启 VPN](https://consumer.huawei.com/cn/support/content/zh-cn00430541/)

### 隐私与依赖

Tailscale 私钥留在设备端，数据平面使用 WireGuard 端到端加密；协调服务只参与身份、密钥分发、策略和连接协调。Tailscale 明确说明其看不到业务流量，但会处理设备名、系统、IP、节点公钥、连接关系等元数据。[Tailscale：控制面与数据面](https://tailscale.com/docs/concepts/control-data-planes)；[Security](https://tailscale.com/security)

因此 Jarvis UI 和开发手册应准确表述为：

> 监督正文与事件不上传 Jarvis 云端；启用远程私网连接时，Tailscale 会按其服务条款处理连接所需的设备和网络元数据。

不应表述为“完全无第三方”或“所有信息都不出设备”。

### 对现有证书固定 HTTPS 的最小改动

推荐不使用 `tailscale serve` 终止 Jarvis HTTPS，也不启用 Tailscale 公有 CA 证书。Tailscale 官方说明，启用 tailnet HTTPS 会把完整证书域名写入公开 Certificate Transparency 日志；当前私人单用户产品没有必要暴露机器名。[Tailscale：HTTPS certificates](https://tailscale.com/docs/how-to/set-up-https-certificates)

最小改动是：

1. Core 增加显式 `MobileSyncEndpoint` 配置，允许当前电脑的稳定 Tailscale IPv4 或完整 MagicDNS 名称；不要把 `100.64/10` 粗暴并入“普通局域网地址自动发现”；
2. 自签名 Jarvis 证书 SAN 包含实际使用的 Tailscale IP/完整名称；手机仍固定该证书指纹，同时恢复标准 hostname 校验；
3. Kestrel 继续监听现有端口，Windows 防火墙只允许 Tailscale 接口/来源；tailnet 策略只允许目标手机访问电脑的 Jarvis 端口；
4. 配对令牌、协议版本、同步 API、幂等事件和离线缓存全部不变；
5. V2 先只保存一个正式 endpoint。不要预建复杂的多传输路由器；Tailscale 失败时先依赖离线执行和人工热点降级，只有真实失败频率证明有必要时再增加 endpoint 列表。

Tailscale 地址在设备仍注册时保持稳定，因此首版可直接用稳定 IP，避免新增 MagicDNS 故障面。[Tailscale：稳定地址](https://tailscale.com/docs/concepts/ip-and-dns-addresses) 若设备被移除、重装或丢失节点密钥，IP 可能改变，此时按现有安全模型重新配对即可。

## WireGuard 与 Headscale：为什么暂不采用

裸 WireGuard 提供加密隧道，但不会自动替当前场景完成协调和 DERP 兜底。官方说明，位于 NAT/防火墙后的 peer 若希望持续接收入站包，需要用 `PersistentKeepalive` 维持映射，常见建议值是 25 秒；这仍没有解决“哪一端具有公网可达 endpoint”的问题。[WireGuard Quick Start](https://www.wireguard.com/quickstart/)

因此实际部署通常还要一台公网 VPS 作为 hub。这样会把 Tailscale 已经解决的密钥分发、NAT 穿透、网络迁移和中继运维重新交给 Jarvis 项目，违背最小充分设计。

Headscale 是可自托管的 Tailscale 控制服务器替代品，官方 Tailscale Android 客户端可以配置 alternate server。[Headscale：Android client](https://github.com/juanfont/headscale/blob/main/docs/usage/connect/android.md) 但它要求公网 HTTPS 服务；自建 DERP 还需开放 TCP 443 和 UDP 3478。只保留一个自建 DERP 会形成单点故障。[Headscale requirements](https://headscale.net/stable/setup/requirements/)；[Headscale DERP](https://github.com/juanfont/headscale/blob/main/docs/ref/derp.md)

Headscale 只在以下需求真实出现后再评估：用户明确拒绝 Tailscale 控制平面元数据、愿意支付 VPS/域名成本，并愿意承担证书、备份、升级和故障处置。它不是当前 V2 的“免费 Tailscale”。

## 自建云中继：当前不值得

Azure Relay 官方展示了这类中继的基本价值：内网服务只需建立出站连接，不必在防火墙开放入站端口，云服务通过双向 WebSocket 转发数据。[Microsoft：Azure Relay](https://learn.microsoft.com/en-us/azure/azure-relay/relay-what-is-it)

但 Jarvis 自建中继并不是“加一个 URL”。至少要引入：

- 电脑与手机的云端身份和密钥生命周期；
- 断线重连、消息确认、幂等、过期和离线队列；
- 端到端载荷加密，否则中继可见监督策略和原因；
- 数据保留、删除、监控、费用、告警和服务升级；
- 电脑关机、手机休眠、服务故障时的重试与冲突规则。

实时 WebSocket 也不等于可靠投递。Microsoft 的可靠 Web PubSub 文档仍要求客户端实现恢复、ack 和重复消息处理；服务或网络切换会断开连接。[Microsoft：Reliable WebSocket clients](https://learn.microsoft.com/en-us/azure/azure-web-pubsub/howto-develop-reliable-clients)

这会把当前“手机主动拉取一个电脑权威状态”的简单协议改造成分布式消息系统，同时直接冲突 [ADR-0006](../adr/0006-local-first-without-cloud-backend.md) 的本地优先边界。对一台电脑和一部手机，收益不足以覆盖风险，当前明确不做。

## 推荐落地顺序

### 第一步：只做 Tailscale 连通性 spike

不改业务协议，先在目标电脑和手机上安装官方客户端并加入一个仅包含这两台设备的个人 tailnet：

1. 手机保持连接校园 Wi-Fi，电脑保持连接同一校园 Wi-Fi；
2. 不使用 exit node；
3. 确认电脑允许 tailnet 入站连接；
4. 用 `tailscale ping` 和 `tailscale status` 记录 direct/DERP 路径；
5. 用手机访问 Core 的 `/v1/health`，再运行现有 `/v1/sync`；
6. 锁屏、切换移动数据、电脑睡眠恢复和手机重启后重复。

只有该 spike 通过，才修改 endpoint 选择和证书 SAN。不要先建设抽象层。

### 第二步：最小正式改动

- 增加显式 Tailscale endpoint 配置；
- 证书 SAN 覆盖实际 endpoint，并恢复 hostname 校验；
- 保留证书指纹固定和设备令牌；
- Core 状态区分“Jarvis 服务在线 / 私网传输离线 / 手机执行权限降级”；
- 安装说明增加 Tailscale VPN、华为后台保护和隐私告知；
- 热点只保留在“连接故障帮助”中，不成为日常操作。

### 第三步：只有失败证据出现才扩展

- Tailscale 在目标网络不稳定：测试 Windows 移动热点自动开启与手机自动重连；
- 用户拒绝 Tailscale 元数据：先用热点，不直接上 Headscale；
- 热点也不满足且确有异地实时同步需求：重新做 ADR，再比较 Headscale 与托管中继；
- 不因“未来可能多设备”提前建设账号、多 endpoint、云队列或消息总线。

## 必须真机验证的门禁

### Tailscale 默认路线

1. **校园网隔离：** 手机和电脑都连校园 Wi-Fi，连续同步 30 分钟；记录实际连接为 direct 还是 DERP，且不允许手动切热点。
2. **出站限制：** 记录 `tailscale netcheck`、`tailscale ping` 与 `tailscale status`；若 UDP 不通，验证 DERP 仍能同步，而不是只验证“登录成功”。
3. **锁屏后台：** 手机锁屏至少 30 分钟，期间电脑下发一条短期监督策略；手机须在可接受时间内收到并执行。
4. **网络迁移：** 手机在校园 Wi-Fi、移动数据、电脑热点之间切换，不重新配对，策略版本不能倒退或重复。
5. **重启：** 手机和电脑分别重启；Tailscale、Jarvis Core 和 Jarvis Mobile 恢复后能继续同步。恢复前必须显示降级。
6. **HarmonyOS 后台：** 给 Tailscale 单独开启自启动、关联启动、后台活动、休眠联网和必要的电池优化例外；从最近任务清理后复测。
7. **VPN 冲突：** 验证用户常用的其他 VPN/代理；若必须共存，Tailscale 路线判定失败，不尝试在 Android 上运行两个 VPN。
8. **续航：** 在相同网络和相近使用下做一轮开启/关闭 Tailscale 的 8–24 小时对照，记录电量差、发热和系统耗电排名，由用户决定是否接受。
9. **安全：** 手机只能访问被授权的 Jarvis 端口；错误 tailnet 设备、旧令牌、错误证书指纹和错误 hostname 均必须失败。
10. **服务故障：** 主动关闭 Tailscale，确认手机继续执行已缓存策略到原定结束，电脑明确显示离线；再启用后事件只补传一次。

### Windows 热点降级路线

1. 当前 Intel AX211 能否同时连接校园 Wi-Fi 并提供热点；`netsh` 的旧 Hosted Network 字段不能代替实际 Mobile Hotspot 测试；
2. 手机是否在无需用户点击的情况下连接已保存热点，并能通过电脑访问互联网；
3. 电脑开机、Core 启动、睡眠恢复和短暂断网后，热点是否自动恢复；
4. 校园认证/门户是否允许 ICS/NAT 转发，且不违反校园网络规则；
5. 手机连接热点时，Core 生成的是热点侧可达 endpoint，而不是校园 WLAN 地址；
6. 热点关闭时离线策略继续，到期解除；恢复后事件幂等补传。

## 最终决策表

| 真实结果 | 决策 |
|---|---|
| Tailscale 在校园网、锁屏和重启后稳定；续航、VPN 槽和元数据可接受 | **采用 Tailscale 默认 + 手动热点降级** |
| Tailscale 被网络封锁、被 HarmonyOS 杀死、VPN 冲突或隐私不可接受；Windows 热点六项门禁通过 | **采用 Windows 热点自动连接** |
| 两者都失败，但只要求执行已经同步的监督 | **维持局域网配对 + 离线执行，明确不承诺实时同步** |
| 两者都失败，且出现真实的异地/跨网即时同步硬需求 | **重新决策 Headscale 或托管中继；不直接自建云业务后端** |

## 一手来源索引

- [Microsoft：Use your Windows device as a mobile hotspot](https://support.microsoft.com/en-US/Windows/Experience/Connectivity-Networking/use-your-windows-device-as-a-mobile-hotspot)
- [Microsoft：NetworkOperatorTetheringManager.StartTetheringAsync](https://learn.microsoft.com/en-us/uwp/api/windows.networking.networkoperators.networkoperatortetheringmanager.starttetheringasync)
- [Microsoft：What is Azure Relay](https://learn.microsoft.com/en-us/azure/azure-relay/relay-what-is-it)
- [Android：VpnService](https://developer.android.com/reference/android/net/VpnService)
- [Android：Unsafe HostnameVerifier](https://developer.android.com/privacy-and-security/risks/unsafe-hostname)
- [Huawei：应用无法后台运行](https://consumer.huawei.com/cn/support/content/zh-cn00428704/)
- [Huawei：HarmonyOS 4.3 始终开启 VPN](https://consumer.huawei.com/cn/support/content/zh-cn00430541/)
- [Huawei：Wi-Fi+](https://consumer.huawei.com/en/support/content/en-us15674126/)
- [Tailscale：IP and DNS addresses](https://tailscale.com/docs/concepts/ip-and-dns-addresses)
- [Tailscale：Connection types](https://tailscale.com/docs/reference/connection-types)
- [Tailscale：DERP servers](https://tailscale.com/docs/reference/derp-servers)
- [Tailscale：Control and data planes](https://tailscale.com/docs/concepts/control-data-planes)
- [Tailscale：Install on Android](https://tailscale.com/docs/install/android)
- [Tailscale：Security](https://tailscale.com/security)
- [WireGuard：Quick Start](https://www.wireguard.com/quickstart/)
- [Headscale：Requirements](https://headscale.net/stable/setup/requirements/)
- [Headscale：Android client](https://github.com/juanfont/headscale/blob/main/docs/usage/connect/android.md)
- [Headscale：DERP](https://github.com/juanfont/headscale/blob/main/docs/ref/derp.md)
