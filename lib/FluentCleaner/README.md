# FluentCleaner 规则解析器

- 上游：https://github.com/builtbybel/FluentCleaner
- 固定提交：`18977b765e6c917e68224847a30d0429d4145e7a`
- 许可证：MIT，完整文本见本目录 `LICENSE`，也内嵌于 DevUp.exe。
- 原始路径：`FluentCleaner.Core/Models/{CleanerEntry,FileKeyEntry,ExcludeKeyEntry,RegKeyEntry}.cs`、`FluentCleaner.Core/Services/Winapp2Parser.cs`。
- 上述源码未经修改，通过主工程的 `Compile` 引用编译，不引入上游 UI 或额外 NuGet 依赖。

DevUp 实际复用解析器读取内嵌的清理规则。`src/DevUp/Cleaning/Rules.ini` 为 DevUp 自有规则，未复制 Winapp2 数据库；清理执行、安全校验和 WPF 界面由 DevUp 实现。规则不支持运行时导入，也不执行脚本或注册表删除。
