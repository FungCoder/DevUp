using System.Text;
using DevUp.Services;
using FluentCleaner.Services;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法：DevUp.RuleBuilder <上游 Winapp2.ini> <适配规则输出路径>");
    return 1;
}
try
{
    var rules = CleanupRulePolicy.Adapt(File.ReadAllText(args[0], Encoding.UTF8));
    File.WriteAllText(args[1], rules, new UTF8Encoding(false));
    Console.WriteLine($"已适配 {new Winapp2Parser().Parse(rules).Count} 项缓存/日志规则，" +
        $"{Encoding.UTF8.GetByteCount(rules):N0} 字节；请审查差异后发布。");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
