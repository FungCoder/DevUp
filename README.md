# DevUp · 驱动与软件更新助手

> 一个 Windows 桌面小工具：**看清设备健康状况、装对官方驱动、升级软件、管理自启动、清理系统与软件缓存**。
> 界面用 WPF 自绘，更新逻辑复用成熟的开源内核，以**单个 DevUp.exe** 分发，使用官方 **.NET 8 桌面运行时（x64）**，无需安装 DevUp。

- 作者：风之翼灵'Blog · <https://www.fungj.com>
- 版本：1.5.0（Windows 10 19041+ / Windows 11，x64）
- 许可证：MIT（第三方组件各自遵循其许可证，见 [docs/ATTRIBUTION.md](docs/ATTRIBUTION.md)）

![DevUp 概览（系统信息与个人路径已打码）](docs/images/ui-dashboard-redacted.png)

---

## 1. 它解决什么问题

| 痛点 | DevUp 的做法 |
| --- | --- |
| 设备管理器里全是黄叹号，不知道该装什么驱动 | 枚举全部设备并标出异常；一键扫描 **Windows Update 官方驱动**，按本机硬件精确匹配 |
| 三方"驱动大师"捆绑、装错版本 | 只用 WUA（Windows Update Agent）与 Microsoft Update Catalog，不下载来路不明的驱动包 |
| 软件更新要一个个点、版本还对不上 | 复用 `winget` 的官方清单，按 **名称 + 已装版本** 双重匹配，避免同名软件误升级 |
| 升级失败没法回滚 | 安装驱动前可自动备份驱动仓库、创建系统还原点 |
| 权限太吓人 | 主界面以普通权限运行，**只在真正需要时**弹一次 UAC |

## 2. 功能一览

### 概览
可点击的统计卡片（可更新软件 / 可更新驱动 / 需要处理的设备），点「查看 ›」直接跳转并自动套用筛选；系统信息磁贴与最近日志。

设备、驱动更新和软件更新分别记录检查状态及上次成功时间。尚未成功检查时显示「—」，成功检查后才显示零；失败、取消及需要复核时明确标注旧结果。JSON / HTML 报告包含各模块检查状态。

### 驱动与设备
- 设备列表：状态彩色标签、按状态筛选、点击行查看驱动版本 / 提供者 / INF / 硬件 ID / 服务名。
- 可用驱动更新：来自 Windows Update 官方源，勾选后「安装选中的 N 项」或「安装全部」。
- 安装前可选：备份相关驱动包（`pnputil /export-driver`）、创建系统还原点（`Checkpoint-Computer`）。
- 一键注册 **Microsoft Update 扩展源**（可搜到显卡 / 网卡等第三方官方驱动）。
- Windows Update 里没有的驱动，可用「查目录」按硬件 ID 检索 Microsoft Update Catalog。

### 软件更新
- 支持按软件名称、发布者或 winget 包 ID 搜索；复选框可选择当前筛选结果或清空全部勾选，显示隐藏在筛选之外的已勾选数量，支持升级选中项。「全部升级」包含筛选之外的全部可升级软件，排除已忽略项。
- 批量升级逐项结果保留在当前会话的软件页面，支持复制全部结果、只重试失败项以及继续取消或尚未执行的项目。重试和继续前重新扫描清单及更新，跳过已无可升级项的软件；成功项目不会重复执行。新批次会替换上一批结果，退出程序后不保留批次。
- `winget list --source winget` 精确比对，显示「已装版本 → 可用版本」；无更新时也保留包 ID，可查看清单信息。没有升级不代表已是厂商最新版，软件源可能落后于官网。
- 选中一行后展示 **官方更新说明、发布日期、安装包类型、官方下载地址**，以及 `winget show` 清单登记的 **SHA256**。
- 「下载并校验安装包」保存安装包到下载目录，并计算本地 **SHA256 + MD5** 与官方清单值比对：
  - 一致 → 显示「校验一致」，确认安装包与 winget 官方清单完全相同；
  - 不一致 → 红色警示，建议不要运行；
  - 清单未登记 SHA256 → 明确标注「仅本地计算」，**不伪造校验值**。
- 设备、驱动、软件和 npm 扫描显示真实步骤或已读取数量，并提供取消和超时后的失败提示；失败后可重新检查。
- 提示窗和进度窗显示期间，主窗口可拖动、缩放和最小化；页面操作暂时锁定，弹窗关闭后恢复，避免更新期间遮挡其他软件。
- 单条 / 批量升级；winget 流程显示可确认的下载、校验和安装阶段，并在长期无进展时提示重试，不显示推测的下载百分比。升级通过隐藏伪终端读取 winget 的实时下载计数，兼容 Windows 传递优化下载；百分比来自已下载字节与总量，速度来自计数增量。下载、校验和安装分别显示，校验后保留已下载总量；不再依赖缓存文件出现的时机。下载器尚未报告总量时不显示推测的百分比。速度、下载量和进度分栏显示，说明放在悬浮提示中。单独下载安装包时显示实际下载量、速度和剩余时间，网络中断会自动重试。升级结束后清理 `%TEMP%\WinGet` 临时安装包。
- 升级失败不会甩出 winget 原始错误，而是给出**原因 + 下一步**（重试安装 / 打开官方下载页 / 需要管理员权限等）。

### Node.js 包
管理 npm 全局安装的 AI 命令行工具（Codex、Claude Code 等）与 pnpm 等：检查更新、升级、卸载。
检测不到 Node.js 或 npm 损坏时，只给友好提示与官方下载入口，**不会自动安装**。

### 备份与工具
「网络与下载」提供默认连接、直接连接和自定义 HTTP/HTTPS 代理，统一应用于 winget 软件检查、清单查询、升级及安装包下载。代理需要用户已有可用服务，不修改系统代理，不影响驱动或 npm。旧版 winget 不支持代理参数时会提示更新「应用安装程序」。连接测试可填写具体软件的 HTTPS 官方下载地址，只检查响应，不下载完整安装包，也不保证其他地址或完整下载可用。下载失败会说明域名解析、连接超时、服务器拒绝访问等问题，并提示境外下载源可能受当前网络影响，以及配置代理、更换网络或打开官方下载页等处理方法；不会仅凭超时断定境外访问受限。

驱动备份目录、安装包下载目录可自由选择并持久化；驱动包备份 / 还原、系统还原点、报告导出（JSON / HTML）、运行日志。

### 垃圾清理
- 独立页面，先只读扫描，再按分类勾选并确认永久删除；可查看完整路径与文件大小。
- 内嵌 17 类自有规则：用户 / Windows 临时文件、错误报告、应用 / 系统转储、缩略图、DirectX / NVIDIA / AMD 着色器缓存，以及 Edge、Chrome、Firefox、Brave、VS Code、Discord、Slack、Spotify 缓存。
- 另内嵌 Winapp2 的 756 项适配规则，覆盖更多第三方软件及 Windows 应用的明确缓存、日志子目录；扫描时只展示实际命中的扩展项目。扩展项目默认不勾选，保留最近七天文件，支持上游的文件和目录排除规则。不会递归清空整个 UWP LocalCache，也不清理 OneNote 笔记缓存等可能包含未同步资料的目录。
- 临时文件保留最近七天；错误报告与转储保留最近一天，默认不勾选。已识别的常用浏览器及软件正在运行时跳过；其他扩展软件依靠保留期和占用检查，清理前仍应退出相应软件。
- 保留个人文件、密码、Cookie、收藏夹、历史记录，以及 DevUp 程序目录、驱动备份和安装包下载目录。跳过链接、只读及占用文件；清理前复核文件路径、创建时间、修改时间和大小，扫描后变化的文件需重扫。
- 扫描和清理均支持取消。清理取消后保留已删除数量与大小；已删除文件无法恢复，重新扫描后才可再次清理。大小为成功删除文件的逻辑大小，不等同于磁盘实际可用空间增量。
- 扫描类别高亮、旋转指示、柔和光带、数值和进度过渡沿用全局主题；切换页面停止装饰动画，并遵循 Windows 的动画开关，不显示估算文件进度。
- 普通权限无法访问的系统项目会跳过；可使用左下角「以管理员身份重新启动」，在清理页重启时保留页面并重新扫描。当前不清理 Windows Update 下载库、WinSxS、注册表、回收站、聊天附件或个人下载文件。

### 启动方式
首次启动会询问「以管理员权限运行 / 以普通权限运行」，可勾选「记住我的选择」，之后在「备份与工具 → 启动方式」随时改回每次询问。
普通权限下，只有驱动安装、驱动备份还原、创建还原点等系统级操作才会单独弹 UAC；**取消 UAC 会回落到普通权限继续运行**，不会中断使用。

### DevUp 与规则更新
「备份与工具」顶部检查本项目 GitHub 正式 Release 的 `DevUp.exe`。默认仅在启动时、每天至多检查一次，可关闭；没有后台常驻进程。发现更新时侧栏显示入口。只信任固定仓库 `FungCoder/DevUp`，按 GitHub 附件登记的 SHA256 与大小校验；缺少校验信息时不自动安装。

程序下载后验证产品和版本，通过短时 PowerShell helper 等待主进程退出再替换、重启。复制或启动调用失败时尝试还原；保留一份旧版在 `DevUpData/updates/DevUp.previous.exe`。进程启动后的应用内部故障不能由 helper 保证自动恢复，可关闭程序后用该备份替换原 exe。设置、备份及下载目录保留。helper 完成后清理本次临时文件。

清理规则和排除规则全部内嵌在 `DevUp.exe`，随程序一起更新；发布及下载只需要这一个执行文件。不生成或下载独立规则附件，旧版下载过的外部规则不再覆盖内嵌版本。离线也可使用全部内嵌规则。

规则调整审核后重新构建程序并递增程序版本，正式 Release 仅上传 `DevUp.exe`。规则维护与发布步骤见 [docs/UPDATES.md](docs/UPDATES.md)。

### 自启动管理

新增独立页面，扫描注册表 Run / RunOnce（含计算机 32 位视图）、用户和公共启动目录、所有子目录中的隐藏及普通开机/登录任务，以及自动或触发启动的 Windows 服务。支持搜索、多选禁用、恢复 DevUp 保存的原配置；同一确切可执行文件的入口可以一起禁用，共享命令解释器不自动关联。

禁用直接移走注册表启动值、移动启动文件、设置任务 Enabled 或通过服务管理器设置 Disabled；每项修改前保存配置，完成后重新扫描真实来源。恢复保留注册表原始类型、环境变量、文件内容和服务原启动类型，不覆盖已经改变的命令。恢复数据保存在对应用户/计算机权限范围的 `SOFTWARE\DevUp\StartupBackups`，启动文件保存在原启动目录的 `DevUp.DisabledStartup` 子目录。

Windows 维护任务、Windows 目录中的服务和受保护服务仅展示。系统范围的操作使用现有 UAC 提权机制；换用其他管理员账户时停止操作，避免修改错误用户。扫描失败及访问受限会明确列出。Store StartupTask、策略、驱动等其他启动机制不在当前管理范围；软件可能重新创建入口，刷新后会重新显示。系统配置复核通过不等于已经实测重启；本功能不终止正在运行的进程或服务，也不安装常驻拦截器。

垃圾清理页面在扫描前保持空列表，扫描完成后只显示实际存在候选文件的项目；空结果、扫描取消和失败均不能直接清理。

## 3. 架构

```
DevUp.exe（WPF 外壳，单文件，依赖官方 .NET 8 桌面运行时）
├─ Views/       概览 / 驱动与设备 / 软件更新 / Node.js 包 / 备份与工具 / 垃圾清理
│               + 自绘 MessageDialog、ProgressDialog、StartupDialog
├─ Services/    DeviceService、DriverStoreService、DriverUpdateService、
│               SoftwareService、NpmService、BackupService、ReportService、CleanupService
├─ Core/        Ctx（全局状态）、Scanner（扫描编排）、PsBridge（PowerShell 桥）、
│               Native（提权 / 签名校验）、Ui、CommandLine
└─ Scripts/devup.ps1   PowerShell 后端（WUA / winget / pnputil / CIM）
```

垃圾清理走独立的 C# 调用链：`CleanupView → CleanupService → CleanupFileAccess`，使用 FluentCleaner 的 MIT 解析器读取内嵌规则，以 Windows 文件句柄复核并删除文件。清理路径不从命令行或外部规则文件传入。

**调用链**：WPF → `PsBridge` → `powershell.exe -NoProfile -ExecutionPolicy Bypass -File devup.ps1 -Action <动作> -OutFile <临时文件>`
所有动作统一返回 JSON 契约：

```json
{ "ok": true, "action": "devices", "data": { }, "error": null }
```

**提权模型**：主进程 `asInvoker`。需要管理员的动作以 `--elevated-action <动作> --elevated-args <json> --elevated-out <文件>` 重新启动自身副本，由系统弹 UAC，结果经文件回传。

**便携数据目录**（exe 旁只生成一个 `DevUpData/` 总目录；以下路径均相对于该总目录，不使用启动时的工作目录）：

| 文件 | 位置 | 说明 |
| --- | --- | --- |
| 普通权限运行组件 | `runtime/user/` | 从内嵌资源释放 |
| 管理员运行组件 | `runtime/s-<随机>/` | 保留 ACL 保护，进程退出时清理 |
| 设置 | `data/settings.json` | 程序目录内的路径保存为相对路径，移动后仍有效 |
| 日志 | `logs/devup.log` | 随程序保存 |
| 通信临时文件、PowerShell 模块分析缓存 | `temp/` | 动作完成后删除通信文件 |
| 驱动备份、离线安装包 | 默认 `backups/`、`downloads/` | 可通过界面选择其他目录 |

应用自有目录或用户选择的目录不可写时会明确报错，不静默回退到用户目录或系统目录。
上一版 exe 旁 `data/settings.json` 中的配置会自动迁入 `DevUpData/data/settings.json`，保留用户选择的外部目录；旧默认下载和备份路径改到总目录下，旧文件不自动移动或删除。更早版本留在 AppData / ProgramData 中的数据不会自动迁移或删除；需要保留时，可自行复制 settings.json 到 DevUpData/data/。
便携范围限于 DevUp 自身。npm、winget、Windows Update 和软件安装器保留官方安装路径、缓存及系统服务行为；仅在用户执行相应操作时进行安装、更新、备份或导出。不修改 npm prefix、缓存配置、系统环境变量或第三方软件安装目录。

> 提权子进程收到的参数来自外部，一律按不可信输入处理：更新 ID 必须是合法 GUID，驱动包名必须匹配 `oemN.inf`。

## 4. 复用的开源项目

| 组件 | 版本 | 许可证 | 用途 | 复用方式 |
| --- | --- | --- | --- | --- |
| [Nefarius.Utilities.DeviceManagement](https://github.com/nefarius/Nefarius.Utilities.DeviceManagement) | 6.1.0 | MIT | SetupAPI / CfgMgr32 / DriverStore 封装，读取 DriverStore 驱动包 | NuGet 包引用 |
| [PSWindowsUpdate](https://github.com/mgajda83/PSWindowsUpdate) | 2.2.1.5 | MIT | 通过 WUA 搜索、下载、安装驱动更新 | 模块内嵌（`lib/PSWindowsUpdate`） |
| [MSCatalog](https://github.com/ryan-jan/MSCatalog) | 0.27.0 | MIT | 检索 Microsoft Update Catalog 上的离线更新 | 模块内嵌（`lib/MSCatalog`） |
| [winget](https://github.com/microsoft/winget-cli) | 系统自带 | MIT | 软件清单、版本比对、升级、下载 | 调用外部 CLI |
| [FluentCleaner](https://github.com/builtbybel/FluentCleaner) | 固定提交 `18977b7` | MIT | 解析清理规则 | 复用 5 个源码文件，见 `lib/FluentCleaner`；规则数据见 Winapp2 单独条目 |
| [Winapp2](https://github.com/MoscaDotTo/Winapp2) | 固定提交 `53ae419` 的适配子集 | CC BY-SA 4.0 | 第三方软件及 Windows 应用缓存规则 | 保留安全子目录、排除规则与署名，移除注册表删除和隐私数据清理；详见 `lib/Winapp2` |
| pnputil / Checkpoint-Computer / Get-PnpDevice | 系统自带 | — | 驱动导出还原、系统还原点、设备枚举 | 命令行 / CIM |

> 本项目**没有**复制任何 GPL / AGPL 项目的源码（例如 SDIO 一类工具），只借鉴其思路后自行实现。
> 各组件版权与完整许可证文本见 [`docs/ATTRIBUTION.md`](docs/ATTRIBUTION.md)。

## 5. 构建

要求：Windows 10 19041+ / Windows 11、.NET 8 SDK、PowerShell 5.1 或 7。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

产物：`build\single-file\DevUp.exe`，仅分发这一个文件。运行电脑需要官方 **.NET 8 Desktop Runtime（x64）**；普通 .NET Runtime 不包含 WPF，不能替代桌面运行时。程序不会自行安装运行时，也不携带需要自解压的原生运行时依赖。

> **编码提示**：`src\DevUp\Scripts\devup.ps1` 含中文，必须保存为 **UTF-8 with BOM**，
> 否则 PowerShell 5.1 会按 GBK 解析而报错。可用 `scripts\fix-encoding.ps1` 批量校正。

## 6. 使用

1. 将 `DevUp.exe` 放到可写目录，确认已安装官方 .NET 8 桌面运行时（x64），双击运行，按需选择管理员 / 普通权限（可记住）。
2. **概览**：看三张卡片，点「查看 ›」进入对应功能页。
3. **驱动与设备**：点「扫描驱动更新」查询官方驱动（需管理员时会弹 UAC）；左上下拉框可筛选「仅异常设备 / 缺失驱动 / 未连接的设备」；右下「可用驱动更新」勾选左侧方框 → 「安装选中的 N 项」。
4. **软件更新**：点「检查更新（winget）」→ 用「仅可更新」筛选 → 点一行查看官方更新说明与 SHA256 → 右侧「升级到此版本 / 下载安装包」，或用「全部升级」。
5. **备份与工具**：设置驱动备份目录、安装包下载目录；查看日志、导出报告。

## 7. 常见问题

**Q：扫描驱动更新没反应 / 提示权限不足？**
确认弹出的 UAC 已同意。未注册 Microsoft Update 时只能搜到微软自家驱动，可点「启用 Microsoft Update 扩展源」。

**Q：软件列表是空的？**
需要系统已安装 `winget`（App Installer）。在终端执行 `winget --version` 验证。

**Q：升级后磁盘里还留着安装包？**
升级流程（含失败与取消）都会清理 `%TEMP%\WinGet` 下的临时包；「下载安装包」按设计保留在你选择的目录，方便离线安装。

**Q：软件更新里的 SHA256 / MD5 从哪里来？能证明是官方包吗？**
SHA256 来自 `winget show` 读取的 winget 官方源清单（与 `winget` 安装前校验的值相同），界面会标明来源；MD5 只在本地下载后计算，用于留档核对。点「下载并校验安装包」后，DevUp 会把本地 SHA256 与清单值逐位比对：一致才显示绿色「校验一致」，不一致会红色警示。清单未提供哈希的软件会明确写明"仅本地计算"，不会编造。

**Q：为什么第一次点窗口时没反应？**
程序已对首次激活点击做兼容处理；若仍遇到，请再点一次并反馈 exe 旁的日志 `DevUpData\logs\devup.log`。

## 8. 目录结构

```
DevUp/
├─ src/DevUp/            主工程（WPF + 服务 + 内嵌脚本）
├─ lib/                  内嵌的开源 PowerShell 模块（PSWindowsUpdate、MSCatalog）
├─ scripts/              构建、编码修正、调试脚本
├─ docs/                 第三方署名与许可证
├─ build/single-file/    单文件发布产物 DevUp.exe（git 忽略）
└─ .github/workflows/    CI：构建校验
```

## 9. 许可证

本项目自身代码由 风之翼灵'Blog 维护，以 MIT 许可证发布；第三方代码组件保留各自 MIT 许可证，Winapp2 适配规则数据单独遵循 CC BY-SA 4.0（见 `docs/ATTRIBUTION.md` 与 `LICENSE`）。

便携路径回归检查：`dotnet run --project tests/DevUp.PortableTests -c Release`（含真实 PowerShell 调用；不安装软件或驱动）。

软件状态、搜索及批量执行回归：`dotnet run --project tests/DevUp.PortableTests -c Release -- --software-iteration-only`。安装过程使用模拟执行器，验证失败重试、取消和继续，不安装真实软件。软件页交互与宽窄窗口布局：`dotnet run --project tests/DevUp.PortableTests -c Release -- --software-ui`，使用模拟清单生成 `build/software-iteration-wide.png` 和 `build/software-iteration-narrow.png`。

窗口交互检查需在有交互桌面的 Windows 本机运行：`dotnet run --project tests/DevUp.PortableTests -c Release -- --dialogs-only`。手动拖动验证可使用 `--dialogs-preview`，测试窗口会在 45 秒后自动关闭；这两项不在托管 CI 上运行。

垃圾清理安全回归：`dotnet run --project tests/DevUp.PortableTests -c Release -- --cleanup-only`，只在专用测试临时目录创建、扫描和删除文件；也包含在默认回归中。

清理页交互与布局检查：`dotnet run --project tests/DevUp.PortableTests -c Release -- --cleanup-ui`，使用隔离测试文件验证扫描、取消、确认清理、按钮状态、动画结束与窄窗口布局，生成 `build/cleanup-ui*.png`；不会扫描或清理真实系统缓存。

程序及内嵌规则检查：`dotnet run --project tests/DevUp.PortableTests -c Release -- --updates-only`，使用内存 HTTP 响应与隔离临时目录验证单文件下载、校验、规则安全策略和真实 PowerShell 替换，不启动被替换程序，不修改真实 DevUp.exe。

自启配置检查：`dotnet run --project tests/DevUp.PortableTests -c Release -- --startup-only`。只创建唯一命名的无副作用测试入口，验证禁用、恢复、旧快照拒绝及配置冲突并清理。普通权限验证用户注册表和启动文件夹；任务修改需要管理员测试环境，服务及实际重启效果需另外验证。
