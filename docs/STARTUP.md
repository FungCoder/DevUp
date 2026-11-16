# 自启动管理实现与验证边界

界面从 `StartupService` 调用 `StartupBackend` 扫描和修改。系统范围操作沿用 `PsBridge` 的 UAC 子进程和结果文件通信；`CommandLine` 将自启动作交给同一份 C# 后端，避免维护两套修改逻辑。Windows API 调用失败和扫描不完整都保留明确状态，界面不预先切换开关。

## 当前覆盖

| 来源 | 扫描范围 | 禁用 | 恢复 |
| --- | --- | --- | --- |
| 注册表 | 当前用户及计算机 Run/RunOnce，计算机 32/64 位视图 | 保存原值及类型后删除生效值 | 恢复原始 REG_SZ/REG_EXPAND_SZ 和原批准信息 |
| 启动目录 | Windows 配置的用户及公共目录中的文件 | 移入 `DevUp.DisabledStartup` 子目录 | 校验文件哈希后移回，拒绝覆盖同名文件 |
| 计划任务 | 递归遍历含隐藏任务的所有目录，筛选开机/登录触发器 | 设置整个任务 Enabled=false | Enabled=true，其他任务定义必须与快照一致 |
| 服务 | 自动、延迟自动、触发启动及禁用的 Win32 服务 | 使用 SCM 的 ChangeServiceConfig 将启动类型改为 Disabled | 恢复原启动类型；保留原延迟配置 |

StartupApproved 是 Explorer 的内部格式，仅保守识别已知状态并保留原始二进制数据；未知格式不猜测，但允许禁用已经确认存在的注册表或启动文件入口。恢复未知格式时写回原配置，并明确显示无法确认启用状态，不标记启用成功。禁用通过移出生效位置实现，不依赖只写内部批准标记。读取失败时不能将“未枚举到”解释为“已禁用”。

原始配置按权限范围写入 HKCU/HKLM 的 `SOFTWARE\DevUp\StartupBackups`，先 Flush 再修改。入口身份包括来源、作用域、视图、路径和名称。文件哈希和任务定义指纹用于拒绝过期快照与恢复冲突。恢复成功并重新扫描确认后才移除记录。应用目录不承载可被普通用户替换的系统级恢复配置。

## 限制

- 当前用户范围不包括其他未登录用户的注册表配置。换用其他管理员账户的 UAC 请求明确失败，避免错用 HKCU。
- Windows 维护任务、位于 Windows 目录的服务和受保护服务只展示；不修改驱动启动、系统策略、Winlogon、WMI 永久订阅或 Store StartupTask。
- 应用关联仅使用同一确切可执行文件路径，排除 cmd、PowerShell、rundll32 等共享宿主；不同辅助程序不会因名称相似而被自动关闭。
- 已运行的进程、服务和任务不会被终止。禁用任务涉及它的全部动作与触发器，用户确认列表会显示入口来源。
- 软件可主动重新创建启动配置。刷新会反映重新创建的项目；本功能没有后台拦截服务，无法承诺永久阻止重新注册。
- 单项“配置复核通过”是再次读取系统配置的结果。实际重启/重新登录后是否启动需要独立验证，不应声称已完成真实重启验证。

## 回归

`dotnet run --project tests/DevUp.PortableTests -c Release -- --startup-only` 创建唯一名称的无副作用测试项，检查 Run/RunOnce、启动文件移动、原值类型与环境变量保留、重新注册识别、恢复冲突、过期快照和跨账户/越界请求拒绝，最后清理测试项。

当前普通用户测试环境验证用户注册表及启动目录的禁用和恢复，并验证任务扫描。任务修改、服务配置与 UAC 取消仍需管理员测试环境验证；实际重启不在自动测试中执行。CI 将运行同一组测试，管理员环境下额外验证专用计划任务的禁用与恢复。

`--cleanup-ui` 同时验证自启导航、只读扫描和按钮状态，不修改真实软件配置。

## 调研资料

- Microsoft Run / RunOnce：https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
- Microsoft Task Scheduler Enabled：https://learn.microsoft.com/en-us/windows/win32/taskschd/registeredtask-enabled
- Microsoft ChangeServiceConfigW：https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfigw
- Microsoft StartupTask：https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask
- BCUninstaller 自启实现：https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/tree/master/source/UninstallTools/Startup

参考了 BCUninstaller 对启动目录、注册表、任务、服务分来源处理的设计与 StartupApproved 路径；未引入其程序集或复制代码。实际操作复用 Windows 原生 Registry、Task Scheduler COM 和 SCM API。
