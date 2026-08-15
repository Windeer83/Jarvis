# T27 Windows 11 x64 安装、启动与普通卸载

## 交付边界

- `scripts/build-t27-installer.ps1` 分别发布自包含 `win-x64` 的 Jarvis Core 与 Desktop，再生成单个 MSI。目标电脑不需要预装 .NET 10。
- 安装范围是当前普通 Windows 用户，安装位置为 `%LOCALAPPDATA%\Programs\Jarvis`，不安装系统服务、不开放网络端口，也不要求管理员权限。
- 安装程序拒绝 Windows 11 x64 桌面版以外的环境。首次安装明确说明：Jarvis 本机优先、没有项目云端后台；电脑关机、休眠或 Core 未运行时不会监督；本机 SQLite 未做应用内加密，建议开启设备加密或 BitLocker。
- “Windows 登录后启动”是默认勾选但可取消的独立安装功能。它只启动 Core，随后由 Core 启动 Desktop。安装后可从 Core 托盘或 Desktop 的“安装与启动（T27）”页随时关闭或重新开启。
- 关闭 Desktop 窗口只隐藏界面；“完全退出”仍由 Core 明确确认并说明监督会停止。
- 普通卸载只删除程序文件、开始菜单项和登录启动项。它保留本机数据库、设置、密码保护备份以及 Windows 凭据管理器中的 Jarvis 凭据；永久删除属于 T28 的独立双重确认操作。

WiX 固定使用 `WixToolset.Sdk` / `WixToolset.UI.wixext` 4.0.6。没有接受 WiX 7 的额外 OSMF EULA。构建脚本为自包含发布中的每个文件和目录生成稳定 WiX 标识，不提交生成清单或构建产物。

## 自动验证

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-t27-installer.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate-t27-package.ps1
```

本次证据：

- MSI：`Jarvis-0.1.0-win-x64.msi`，60,998,551 bytes；
- SHA-256：`8C3147C494ADF6E2E65E46FF239242DAE29FE9CF5AEADE71AE64A3F355C6FE4D`；
- WiX 构建 0 warning / 0 error；
- MSI 在隔离目录真实安装，已安装 Core 与 Desktop 均可启动；Core 只启动一个 Desktop；
- 普通卸载删除程序文件，同时保留安装目录之外的用户数据哨兵；
- Release solution 构建 0 warning / 0 error；182/182 自动测试通过，含 Core 所有的登录启动设置 IPC 往返。

验证脚本用 `ADDLOCAL=MainProgram` 跳过实际写入当前用户的登录启动项，避免自动测试覆盖目标机现有设置；安装器默认选择与注册表作者通过静态包边界检查，开关行为由 Core 定向测试覆盖。

## 人工验收

1. 双击 MSI，核对首次说明与默认勾选的“登录时启动（推荐）”；取消一次再返回，确认选项可改。
2. 安装后确认先出现 Core 托盘，再出现 Desktop；关闭 Desktop 窗口，确认托盘仍在且监督不退出。
3. 在托盘和 Desktop“安装与启动（T27）”页分别关闭/开启登录启动，确认状态同步。
4. 在有可丢弃测试数据时普通卸载，确认程序和登录启动项消失；重新安装后确认原数据库、设置、备份和 Jarvis 凭据仍保留。
