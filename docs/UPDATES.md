# 程序与内嵌规则发布

只分发一个 `DevUp.exe`。基础规则、扩展清理规则、排除规则和版本元数据全部作为程序集资源嵌入，离线即可使用。客户端不生成、不下载独立规则附件，也不加载旧版本下载的 `DevUpData/rules/*.gz`；旧数据不会被自动删除。

## 正式发布

1. 修改 `src/DevUp/DevUp.csproj` 的程序版本并完成回归。
2. 运行 `scripts/build.ps1`，或执行 `dotnet publish src/DevUp/DevUp.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o build/single-file`。
3. 检查发布目录仅有 `DevUp.exe`，在正式 GitHub Release 上传该文件。CI 同样只生成一个 exe 产物。
4. 客户端仅查询固定仓库 `FungCoder/DevUp` 的最新正式版本，按附件大小与 SHA256 校验；草稿、预发布及无校验信息的附件不自动安装。

旧 Release 中仍存在的规则附件会被新版客户端忽略。本地改动不会自动删除已发布的 GitHub 附件；维护者可在发布新版时单独处理历史附件。

## 规则维护

1. 从上游固定提交获取 `Non-CCleaner/FluentCleaner/Winapp2.ini` 到构建临时目录，记录来源提交与 SHA256。
2. 执行 `dotnet run --project tools/DevUp.RuleBuilder -c Release -- <上游文件> src/DevUp/Cleaning/Winapp2.safe.ini`。维护工具与客户端复用 `CleanupRulePolicy`，不随程序分发。
3. 审查生成 INI 的差异、排除条件及清理范围，尤其是聊天、凭据、未保存资料和软件新版目录。
4. 更新 `Cleaning/RulePack.metadata.json` 的来源提交与规则版本（年.月.日.修订号）。需要新执行器时同步提高 `MinimumAppVersion`。
5. 运行默认回归、`--updates-only` 和 `--cleanup-ui`；重新构建并递增程序版本，发布新的 `DevUp.exe`。仅调整规则时同样发布完整程序。

## 更新及恢复

- 1.5.1 修复目录末尾分隔符导致的「更新文件目录不匹配」。遇到该错误的旧版需关闭程序后手动覆盖一次新版 DevUp.exe，保留 DevUpData；旧版的替换前检查无法通过重新下载新版来修复。
- 程序在 `DevUpData/updates/<随机任务>/` 暂存校验过的 exe 与 helper。helper 等待原进程退出，核对路径、产品、版本及哈希，替换失败时尝试还原。
- 旧程序保留一份 `DevUpData/updates/DevUp.previous.exe`。新版发生启动问题时，关闭 DevUp，将旧文件复制回原程序位置并改为原文件名。
- helper 不验证启动后的全部健康状态，不能保证所有问题自动回滚。
- 新程序重新加载内嵌规则，扫描前不展示默认清理项，扫描完成后仅展示有候选文件的项目。
- 更新回归使用独立无界面测试宿主，在含中文与空格的隔离目录中调用真实 UpdateInstaller，验证准备就绪、父进程退出、备份、替换和重启，并检查目标文件及备份哈希。非 DevUp 测试宿主必须被产品校验拒绝，不能将目录误判视为通过。
- 自动更新只在运行 DevUp 时、每天至多检查一次，可关闭；没有计划任务或常驻服务。失败不影响现有程序与内嵌规则。

单文件分发不改变运行时的数据存储：设置、日志、备份和更新暂存仍在 exe 旁的 `DevUpData`。运行需要官方 .NET 8 Desktop Runtime（x64）。
