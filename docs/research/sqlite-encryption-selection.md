# Jarvis MVP：SQLite 静态加密实现选型

> 决策状态：本报告保留为技术调研资料；当前第一版决定已改为 ADR 0017 的“本机 SQLite 不做应用层加密，只对便携数据使用备份密码”。只有后置触发条件成立时才重新采用本报告选型。

> 结论基准日：2026-08-10。资料只采用 SQLite、Microsoft、Zetetic/SQLCipher、SQLitePCLRaw 与候选项目自己的文档、源码仓库和许可证。本文区分官方事实与 Jarvis 的工程建议。

## 结论

MVP 推荐 **SQLCipher Community Edition 4.17.0 自构建的 Windows x64 原生库**，上层使用 **`Microsoft.Data.Sqlite.Core` 10.0.x + 与其兼容的 SQLitePCLRaw provider**。把经过测试的原生库固定命名为 `e_sqlite3.dll`，随 Jarvis 的 `win-x64` self-contained 安装包发布；不要引用任何会额外带入 SQLite 原生库的 bundle。

具体边界如下：

- SQLCipher 源码固定到已审核 tag/commit；4.17.0 基于 SQLite 3.53.3。构建产物、编译参数、源 commit、编译器版本和 SHA-256 写入 SBOM，并随安装包签名。[SQLCipher releases](https://github.com/sqlcipher/sqlcipher/releases)
- 使用 MSVC x64 构建，至少启用 `SQLITE_HAS_CODEC`、`SQLITE_EXTRA_INIT=sqlcipher_extra_init`、`SQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown`、`SQLITE_THREADSAFE=1` 和 **`SQLITE_TEMP_STORE=3`**；选择并固定 SQLCipher 官方支持的密码学 provider。MVP 优先验证内置 LibTomCrypt provider，以避免再配送一个 `libcrypto` DLL；若性能或官方测试不通过，再改为受补丁维护的 OpenSSL 构建。SQLCipher 官方要求 `SQLITE_TEMP_STORE=2/3`，并提供独立的 SQLCipher 测试套件。[SQLCipher build instructions](https://github.com/sqlcipher/sqlcipher#compiling)
- NuGet 只引用 `Microsoft.Data.Sqlite.Core`，再显式配置与 10.0.x 兼容的 `SQLitePCLRaw.provider.e_sqlite3`；不引用 `Microsoft.Data.Sqlite` 元包、`bundle_e_sqlite3`、`bundle_e_sqlcipher` 或第二个 provider。SQLitePCLRaw 3.0 已停止免费维护/分发加密构建，旧 `bundle_e_sqlcipher` 已弃用，不能把旧二进制当生产依赖。[SQLitePCLRaw encryption options](https://github.com/ericsink/SQLitePCL.raw/wiki/SQLite-encryption-options-for-use-with-SQLitePCLRaw)、[SQLitePCLRaw architecture](https://github.com/ericsink/SQLitePCL.raw)
- 每次启动都断言 `PRAGMA cipher_version`、`sqlite_version()` 和关键 `PRAGMA compile_options`；版本或 `TEMP_STORE` 不符就拒绝打开正式库，而不是静默降级成普通 SQLite。
- 数据库使用 SQLCipher 4 默认格式：AES-256-CBC、每页随机 IV、HMAC-SHA512、数据库随机 salt；口令模式默认以 PBKDF2-HMAC-SHA512 迭代 256,000 次派生。Jarvis 不使用人类口令，而使用 32 字节随机原始数据库密钥，避免低熵口令风险。[SQLCipher design](https://www.zetetic.net/sqlcipher/design/)

这项推荐满足“复制走数据库文件后不能读取”和“没有日常解锁口令”的需求，但**不**防御已经登录当前 Windows 账户的恶意软件、进程内存读取、键盘记录或以 Jarvis 身份调用 DPAPI；这与已确认的威胁模型一致。

## 为什么不是现成免费 NuGet

`Microsoft.Data.Sqlite` 自身不实现加密。它的 `Password` 连接字符串项只是在连接打开后立即发送 `PRAGMA key`；如果加载的原生 SQLite 没有 codec，该项没有任何作用。因此“连接字符串里写了 Password”不能作为加密验收证据。[Microsoft.Data.Sqlite Password](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnectionstringbuilder.password?view=msdata-sqlite-10.0.0)、[connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)

Microsoft 的文档仍说明可用 `Microsoft.Data.Sqlite.Core` 配合自定义 SQLite provider；普通 `Microsoft.Data.Sqlite` 元包会自动带入标准 SQLite bundle。Jarvis 必须控制唯一的原生库来源。[Custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)

SQLCipher Community 是 BSD-3-Clause 风格许可，可用于闭源和商业软件且无运行时费用，但必须在用户可访问的“关于/第三方许可”页面或材料中保留版权与许可文本。免费版本提供源码，不提供 Zetetic 官方的免费 Windows/.NET 预编译包；Zetetic 官方 .NET 包属于 Commercial/Enterprise 版本，Commercial 当前从每应用每年 999 美元起。[Community license](https://www.zetetic.net/sqlcipher/community/)、[license overview](https://www.zetetic.net/sqlcipher/license/)、[SQLCipher for .NET](https://www.zetetic.net/sqlcipher/sqlcipher-for-dotnet/)、[commercial pricing](https://www.zetetic.net/sqlcipher/commercial/)

因此，自构建不是一次性的“下载 DLL”：Jarvis 必须拥有可重复构建、补丁跟踪、原生测试和升级回滚能力。这是本方案最大的长期成本。

## 密钥与打开流程

1. 首次创建时用 `.NET RandomNumberGenerator.Fill` 生成 32 字节数据库密钥；绝不从用户名、设备信息或固定口令派生。[RandomNumberGenerator.Fill](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator.fill?view=net-10.0)
2. 用 Windows DPAPI 的 `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` 包装该密钥，将密文保存在只允许当前用户访问的 Jarvis 数据目录。DPAPI 明确只能由相同当前用户解开，正好实现自动解锁；DPAPI 密文和数据库分开保存。[ProtectedData / DPAPI](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata?view=windowsdesktop-10.0)
3. `Jarvis Core` 是唯一打开数据库的进程。正常启动用 `Mode=ReadWrite`，不使用 `ReadWriteCreate`，防止路径错误或密钥错误时旁边静默生成一个空库。
4. 从 DPAPI 解开密钥后，用 64 个十六进制字符的 SQLCipher raw-key 形式作为 `Password`，使 `Microsoft.Data.Sqlite` 在任何数据库读取前发送 `PRAGMA key`。禁止记录连接字符串，建议 `Pooling=False`，连接关闭后用 `CryptographicOperations.ZeroMemory` 清理可控的字节缓冲；托管 `string` 无法可靠原地清零，这是 MVP 的已知内存边界。[SQLCipher keying](https://github.com/sqlcipher/sqlcipher#encrypting-a-database)、[ZeroMemory](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.cryptographicoperations.zeromemory?view=net-10.0)
5. `PRAGMA key` 成功只表示“密钥已装载”，不表示密钥正确。随后必须读取一个固定的 `jarvis_meta` 哨兵行和 schema 版本；错密钥应产生 `SQLITE_NOTADB`，绝不能据此初始化新库。再执行 `PRAGMA cipher_integrity_check`；恢复演练、升级前和疑似损坏时同时执行完整 `PRAGMA integrity_check`。[SQLCipher API: integrity and keying](https://www.zetetic.net/sqlcipher/sqlcipher-api/)

原始随机密钥绕过口令 KDF 不会降低熵；SQLCipher 的随机 salt、每页 IV 和独立 HMAC 密钥仍然适用。若以后改为用户输入的恢复口令，必须另行采用经过批准的口令 KDF，不能直接把短口令当 32 字节密钥。

## WAL、journal、TEMP 与崩溃安全

SQLCipher 加密主数据库、rollback journal 和 WAL 中的数据页；statement journal 也受保护。rollback/WAL 的结构性 header、master journal 中的路径以及 `-shm` 等协调信息不应被当作秘密载荷。SQLCipher 官方明确指出，其他磁盘临时文件未必加密，因此必须从编译时阻止 FILE temp。[SQLCipher temporary-files design](https://www.zetetic.net/sqlcipher/design/#database-encryption-and-temporary-files)

Jarvis 的运行设置为：

- `PRAGMA temp_store=MEMORY`，同时在启动时断言编译选项确为 `TEMP_STORE=3`；不能只依赖运行时 PRAGMA。
- `PRAGMA journal_mode=WAL`、`PRAGMA synchronous=FULL`；Core 单写者定期 checkpoint。WAL 模式不等于备份，不能在运行中只复制 `.db` 而忽略 `-wal`。
- 数据目录使用当前用户 ACL；禁止把数据库放进可被其他账户读取的公共目录。
- 每次发布的原生库都用强制产生排序 spill、TEMP 表、rollback journal、WAL、checkpoint 和异常退出的测试检查磁盘；任何可搜索到的测试明文都判定失败。

SQLCipher 保留 SQLite 的事务/恢复模型，但 codec、provider 和编译选项是额外风险。MVP 的自动备份采用“Core 短暂停写 → `wal_checkpoint(TRUNCATE)` → 关闭全部连接 → 复制单一加密数据库文件”的保守路径。写入临时文件、刷新、验证后再原子改名；不把普通文件复制当作在线备份。SQLite 官方说明在线备份可得到一致快照，但 SQLCipher 的跨密钥/跨格式备份有限制；转换应使用 `sqlcipher_export()`。[SQLite Online Backup API](https://www.sqlite.org/backup.html)、[SQLCipher export](https://www.zetetic.net/sqlcipher/sqlcipher-api/#sqlcipher_export)

## 迁移、换钥与错误处理

- **普通 SQLite → SQLCipher：** 新建带新密钥的目标库，通过 `ATTACH ... KEY` 和 `sqlcipher_export()` 做逻辑导出，验证 `cipher_integrity_check`、`integrity_check`、schema 版本和业务行数后原子替换。`PRAGMA rekey` 不能用于把标准明文 SQLite 库直接加密。[SQLCipher export and rekey](https://www.zetetic.net/sqlcipher/sqlcipher-api/#sqlcipher_export)
- **SQLCipher 大版本升级：** 默认设置的旧 1/2/3 格式可用 `PRAGMA cipher_migrate`；非默认设置或希望最稳妥回滚时用新文件 `sqlcipher_export()`。数据库元数据须记录 SQLCipher 格式版本，不凭“当前 DLL 版本”猜测。[SQLCipher migration](https://www.zetetic.net/sqlcipher/sqlcipher-api/#migration-and-compatibility)
- **数据库密钥轮换：** SQLCipher 支持 `PRAGMA rekey`，但它会重写每一页。Jarvis 优先采用“导出到新密钥的新文件 → 完整验证 → 原子切换”的可回退方式；只有原型证明强杀各阶段均可恢复时才允许原地 rekey。切换期间 DPAPI key record 同时保留 current/pending 状态，成功后才删除旧密钥。
- **错密钥与损坏：** 两者都可能表现为 `NOTADB`/HMAC 错误；用户界面应报告“无法解锁或数据库损坏”，保留原文件并引导恢复，不能自动创建、覆盖或尝试弱化 cipher 参数。

## 独立恢复密钥与便携备份

恢复密钥必须独立于 DPAPI：首次设置时再生成一把 32 字节随机 recovery key，只交给用户保存，不写入数据库、日志、普通配置或百度网盘备份。

推荐采用**标准算法的版本化 envelope**，而不是再设计一种数据库密码：

- 备份主体是已经 checkpoint、关闭并验证的 SQLCipher 密文数据库快照；无需先落地明文库。
- 使用 `.NET AesGcm`（AES-256-GCM，12 字节每备份随机 nonce，16 字节 tag）以 recovery key 包装那把 32 字节数据库密钥。AAD 至少绑定固定 magic/格式版本、backup id、schema 版本、SQLCipher 格式版本和数据库快照的 SHA-256；这样恢复时可验证包的身份、完整性与快照匹配关系。AES-GCM 官方要求同一密钥下 nonce 绝不复用。[AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.-ctor?view=net-10.0)、[AesGcm.Encrypt](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0)
- 只使用平台密码学 API，不实现 AES、GCM、随机数或 MAC。封装格式必须版本化、长度定界、拒绝未知字段/超大长度，并提交固定测试向量；先验 tag 和 SHA-256，再让 SQLCipher 打开快照并做完整 integrity check。
- 新电脑恢复时，recovery key 解包数据库密钥；验证快照后，用新电脑当前用户的 DPAPI 重新包装数据库密钥。首次设置恢复演练和每六个月演练必须走这条真实路径，而不是只测试“文件能复制”。

这是一种 envelope encryption 的工程组合，不是自创密码算法；它也避免把整个数据库再次解密后重新加密。若原型无法把格式解析器做到明确、可测试和 fail-closed，则 MVP 应退回“用 recovery key 创建第二个 SQLCipher 加密导出库”的方案，不得自行改用 ZIP 密码或 AES-CBC。

## 候选比较

| 方案 | 加密/完整性 | .NET 10 / Windows x64 交付 | 许可与成本 | 结论 |
|---|---|---|---|---|
| **SQLCipher Community 4.17.0，自构建** | AES-256-CBC；每页随机 IV 与 HMAC-SHA512；WAL/rollback 数据页加密；TEMP 必须强制内存 | 源码可用；自建 `e_sqlite3.dll` 并通过 SQLitePCLRaw provider 加载；需要维护原生 CI | BSD-3，无费用；必须归属声明；无官方免费 .NET 二进制和私有支持 | **推荐**。安全设计成熟、格式/迁移/完整性工具明确；代价是自建供应链 |
| SQLCipher Commercial | 与 Community 同一核心，另有优化、官方 NuGet 与支持 | 官方 Windows/.NET 包最省集成风险 | 当前从 999 美元/应用/年起 | MVP 不采用；若未来商业化且不愿维护 C 构建再购买 |
| SQLite SEE | SQLite 核心团队的扩展；主库、rollback/WAL 加密；TEMP table 不加密；算法/认证能力取决于所选 SEE variant | 购买后获得源码，自行编译；也可另购 .NET 构建服务 | 2,000 美元一次性、永久、无按份版税；只能按许可配送编译产物，通常要求与产品绑定 | 技术上可行且无年费，但个人 MVP 成本高；若要 SQLite 团队官方实现可重评。[SEE overview/license](https://sqlite.org/com/see.html)、[SEE technical docs](https://sqlite.org/see/doc/release/www/readme.wiki) |
| SQLite3 Multiple Ciphers / wxSQLite3 | 当前项目提供带认证的 ChaCha20-Poly1305、SQLCipher 兼容模式等；同样要求 TEMP 内存 | 2026 年有维护中的 MIT NuGet 和 win-x64 原生包；集成显著简单 | MIT、无费用 | **严肃备选**，不是首选：供应链更集中、数据库格式和 SQLCipher 并非默认互通；若自构建 SQLCipher 原型无法在时限内通过，可做对照 spike。[SQLite3MC](https://utelle.github.io/SQLite3MultipleCiphers/)、[official NuGet repository](https://github.com/utelle/SQLite3MultipleCiphers-NuGet) |
| 普通 SQLite + 外层加密文件/容器 | 只有关闭时加密容易；运行时随机访问会迫使明文 DB、WAL/TEMP 落盘，除非实现加密 VFS | 需要挂载容器、完整自定义 VFS 或每次全量解密/加密 | 平台工具可能免费，但操作和崩溃恢复复杂 | 拒绝。BitLocker/EFS 解决的是不同威胁；自写 VFS 等于重新实现已成熟的页级加密 |

SQLite SEE 的当前文档明确：TEMP table 不加密，主数据库、rollback journal 和 WAL 的数据会加密；其永久源码许可为 2,000 美元。SEE 因而不是“不安全”，只是 Jarvis MVP 的成本/构建优势不足以抵消价格。[SEE limitations and WAL](https://sqlite.org/see/doc/release/www/readme.wiki#limitations)

## 必须通过的原型门槛

在正式 schema 和业务功能开发前，用与发布完全相同的 `win-x64` 自包含安装包完成以下测试；任一安全项失败就不进入全面开发：

1. **唯一原生库：** 干净 Windows 11 x64 VM 安装后能加载固定 `e_sqlite3.dll`；扫描输出目录确认没有第二份 `sqlite3/e_sqlite3`，启动断言 SQLCipher/SQLite 版本与编译选项。
2. **静态明文检查：** 在 DB、`-wal`、`-journal`、TEMP 目录、备份和崩溃残留中写入独特长字符串并做二进制搜索；主库与所有可能携带页内容的文件不得出现明文。
3. **TEMP 强制测试：** 用大排序、索引构建、TEMP table 和低 cache 强迫 spill；确认 `TEMP_STORE=3` 生效，且没有包含业务数据的临时文件。
4. **密钥测试：** 正确 key 可读；单 bit 错 key、随机 key、缺失 DPAPI blob、不同 Windows 用户和不同电脑均 fail-closed，绝不创建空库；密钥不出现在日志、异常、遥测、命令行和备份 manifest。
5. **篡改测试：** 分别翻转数据库页、WAL 页、header，截断文件并调换旧 WAL；必须被 HMAC/`cipher_integrity_check`/`integrity_check` 或恢复流程拒绝。
6. **崩溃测试：** 在事务提交、WAL checkpoint、备份复制、安装升级、导出换钥和 DPAPI key 切换的多个注入点强杀 Core；重启后只能得到旧的完整状态或新的完整状态，不能静默丢库。
7. **迁移测试：** 完成 plaintext→SQLCipher、同格式备份、SQLCipher 旧格式迁移、导出换钥和失败回滚；逐表比对 schema、索引、trigger、FTS、行数与关键 hash。
8. **真实异机恢复：** 只拿百度网盘中的备份文件和用户持有的 recovery key，在隔离的新 Windows 用户/新 VM 恢复，完整 integrity check 后再用新账户 DPAPI 自动解锁。
9. **性能门槛：** 在目标电脑测首次/后续打开、常用事务、搜索、daily backup、完整校验、导出换钥和 10 分钟 idle；Core+Desktop 仍须满足既定 `<=300 MB`、idle CPU `<1%`，且监督写入不能因 checkpoint/备份出现不可接受停顿。SQLCipher 官方只声称许多操作约有 5–15% 加密开销，不能替代 Jarvis 实测。[SQLCipher README](https://github.com/sqlcipher/sqlcipher#features)
10. **更新供应链：** 用下一版 SQLCipher/SQLite 重建并运行完整回归；模拟新 DLL 不兼容，安装器必须恢复旧程序、旧 DLL 和升级前数据库。每次发布检查上游安全更新、许可证、源码 tag/commit、编译器和密码学 provider。

## 主要残余风险

- 免费 SQLCipher 没有官方 Windows/.NET 二进制和私有支持；Jarvis 团队承担编译正确性、补丁速度、DLL 搜索路径和 native ABI 风险。
- SQLCipher 是跟随特定 SQLite baseline 的 fork，上游 SQLite 更新不会自动进入 Jarvis；必须主动监控并重建。
- `TEMP_STORE=3` 会把大排序/索引临时数据放入内存，可能与资源预算冲突；只能通过限制查询规模和原型测量解决，不能为了省内存静默允许 FILE temp。
- DPAPI 自动解锁意味着同一已登录用户上下文中的恶意代码也可能取到密钥；本方案不声称解决该威胁。
- 托管连接字符串/异常转储可能短暂包含 key；MVP 必须禁用敏感 dump、禁止日志化连接字符串并缩短密钥存活，但不能承诺进程内零明文。
- 自定义备份 envelope 的风险主要在格式解析、nonce 管理和密钥/快照绑定，不在 AES-GCM 算法本身；必须依靠版本化、固定测试向量、破坏性测试和真实恢复演练控制。

最终落地条件是：**先让上述原型门槛通过，再把 SQLCipher CE 自构建方案写入正式 ADR。** 如果 native 构建、TEMP 内存或异机恢复任一项在可接受时间内无法达标，再用 SQLite3MC 官方 NuGet 做同一测试集的对照，而不是恢复使用已经弃用的 `bundle_e_sqlcipher`。
