using System;
using System.IO;
using System.Linq;
using Guard;
using MyPlugin.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Tools;

internal static class McpHelpTools
{
    public static void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("help_list",
            "Темы инструкций. Сначала прочитай platform, остальные — по задаче.",
            new JObject { ["prefix"] = McpSchema.String("Необязательный префикс тем, например electrica. Без поля — только корневые темы.") }, Array.Empty<string>(), args => new
            {
                skills = Directory.GetDirectories(Path.Combine(McpRuntime.DirectoryPath, "Skills"))
                    .Where(x => File.Exists(Path.Combine(x, "SKILL.md")))
                    .Select(Path.GetFileName)
                    .Where(x => args.Value<string>("prefix") is { } prefix ? x!.StartsWith(prefix, StringComparison.Ordinal) : !x!.Contains('-'))
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray()
            }, usesCad: false);
        tools.Add("help_read",
            "Прочитать тему. Начни с platform, затем инструкции нужного продукта.",
            new JObject { ["name"] = McpSchema.String("Имя инструкции из help_list. Первым прочитай platform, затем инструкцию нужного продукта.") }, new[] { "name" },
            args =>
            {
                var name = args.Value<string>("name")!;
                if (name.Length == 0 || name.Any(x => !(x is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                {
                    throw new ArgumentException("Выберите имя темы из help_list.");
                }

                var path = Path.Combine(McpRuntime.DirectoryPath, "Skills", name, "SKILL.md");
                if (!File.Exists(path))
                {
                    throw new ArgumentException("Тема не найдена. Доступные темы — в help_list.");
                }

                return new { name, markdown = File.ReadAllText(path) };
            }, usesCad: false);
    }
}
