# 程序与规则发布

客户端仅查询 `https://api.github.com/repos/FungCoder/DevUp/releases/latest`。草稿、预发布与不可解析版本不进入自动更新。程序版本取正式 Release 标签；规则版本取附件内部元数据，两者独立。

## 首次发布或程序更新

1. 更新 `DevUp.csproj` 的版本，完成回归和单文件构建。
2. 运行 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-rules.ps1`。
3. 运行 `dotnet run --project tests/DevUp.PortableTests -c Release -- --validate-rules-pack build/release-assets/DevUp.rules.json.gz`。
4. 在同一正式 GitHub Release 上传 `DevUp.exe` 与 `DevUp.rules.json.gz`。客户端从 GitHub API 获取每个附件的大小、SHA256 和固定仓库下载地址，分别校验。缺少程序附件哈希时只提供发布页入口。

CI 分别生成 exe 与规则附件，保留原有单 exe 发布目录；规则包不需要用户手动安装，也不增加新的运行时依赖。

## 仅更新规则

1. 从上游固定提交获取 `Non-CCleaner/FluentCleaner/Winapp2.ini` 到构建临时目录，记录提交及内容 SHA256。
2. 执行 `dotnet run --project tools/DevUp.RuleBuilder -c Release -- <上游文件> src/DevUp/Cleaning/Winapp2.safe.ini`。此维护工具与客户端复用 `CleanupRulePolicy`，不随程序分发。
3. 检查生成 INI 的差异、排除条件和删除范围，尤其是聊天、凭据、未保存资料与新版软件路径。策略只允许指定的缓存/日志子目录，不代表任何名称含 Cache 的目录都安全；新软件仍需核实路径语义。
4. 修改 `Cleaning/RulePack.metadata.json` 的来源提交及内部规则版本，格式为 `年.月.日.修订号`。需要新执行器能力时提高 `MinimumAppVersion`，同时发布新程序。
5. 运行默认回归、`--cleanup-ui` 与规则附件校验；重新生成 `DevUp.rules.json.gz`。
6. 将通过审核的附件替换到**当前最新正式 Release**，保持文件名不变，规则版本必须递增。不要以规则版本标签创建一个覆盖程序 latest 的新正式 Release，也不需要重新上传 exe。更换附件的短暂间隙内客户端继续使用旧规则。

维护者可以通过 GitHub 网页或现有发布流程上传附件；客户端不需要 GitHub 账号或 token。

## 存储及恢复

- 规则：`DevUpData/rules/current.gz` 与 `previous.gz`；损坏或不兼容时回退可用版本，最后回退内嵌版本。
- 程序：`DevUpData/updates/<随机任务>/` 暂存已校验程序与 helper。helper 等待原进程退出，核对路径、产品、版本及哈希，替换失败时尝试还原。
- 旧程序只保留一份：`DevUpData/updates/DevUp.previous.exe`。如果新版进程启动后出现问题，关闭 DevUp，将这份文件复制回原程序位置并改回原文件名。
- helper 不检查程序启动后的全部健康状态；不能声称所有启动故障都可自动回滚。
- 不在扫描和清理过程中安装规则；更新后清空待清理快照，需重新扫描。
- 默认仅在启动时至多每天检查一次；可在统一更新卡片关闭。检查失败只记录提示，不阻止使用，无计划任务或常驻服务。

安装包为完整单文件下载，不实现增量补丁；体积较小且恢复流程更直接。规则数据使用 gzip，限制压缩大小 512 KiB、解压大小 1.5 MB，拒绝越界目录、未知执行语法和不兼容的包。
