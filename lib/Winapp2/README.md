# Winapp2 适配规则数据

- 原项目及作者：Winapp2 / MoscaDotTo 与贡献者，https://github.com/MoscaDotTo/Winapp2
- 来源：`Non-CCleaner/FluentCleaner/Winapp2.ini`
- 固定提交：`53ae41946c3d3c8bb348d81081c34b6050f0b561`
- 许可证：CC BY-SA 4.0，全文见本目录 `LICENSE`，并以内嵌资源 `Winapp2.LICENSE` 随 exe 分发；https://creativecommons.org/licenses/by-sa/4.0/
- 修改者：DevUp，2026-11-16。

修改内容：仅保留 AppData 下经策略允许的缓存/日志 FileKey 和相关排除条件；移除注册表操作、宽泛目录删除、隐私数据、备份、笔记缓存等规则；将 REMOVESELF 转为只递归文件的 RECURSE，不删除目录；全部默认不勾选并由执行器保留最近七天文件。

适配数据位于 `src/DevUp/Cleaning/Winapp2.safe.ini`，单独继续采用 CC BY-SA 4.0；自有 C# 执行器、适配策略和更新代码保持原有 MIT 许可证。导出规则包的元数据包含原作者项目链接、许可链接、来源提交及修改说明。未将 Winapp2 数据声明为 MIT。

更新规则时，用 `tools/DevUp.RuleBuilder` 复用客户端同一份安全策略生成，再检查源文件差异、更新来源提交及规则版本、运行回归测试后发布。不要直接把上游完整数据库替换到客户端。
