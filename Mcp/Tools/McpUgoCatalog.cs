using System;
using System.Linq;
using Guard;
using Nanosoft.Acad;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Utils;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Tools;

internal sealed class McpUgoCatalog
{
    private readonly McpTypeCatalog _types;

    public McpUgoCatalog(McpTypeCatalog types)
    {
        ThrowIf.Argument.IsNull(types, nameof(types));
        _types = types;
    }

    public void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("ugo_search", "УГО для точечного типа: имена/файлы, без картинок. name/file — для objects_create. Геометрия УГО в закрытой базе DWG: block_get; правка: block_update, перенос: block_geometry_copy.",
            new JObject
            {
                ["type"] = McpSchema.String("Тип из types_search."),
                ["query"] = McpSchema.String("Часть имени или описания."),
                ["offset"] = McpSchema.Integer("По умолчанию 0."),
                ["limit"] = McpSchema.Integer("По умолчанию 20, максимум 100.")
            }, new[] { "type" }, Search);
    }

    private object Search(JObject args)
    {
        var type = _types.ResolveObject(args.Value<string>("type")!);
        var query = args.Value<string>("query") ?? "";
        var matches = Choices(type).Where(x => (x.UgoName + " " + x.UgoElemDesc).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 20, 1, 100);
        var page = matches.Skip(offset).Take(limit).Select(x => new { name = x.UgoName, file = x.FileName }).ToArray();
        return new { total = matches.Length, nextOffset = offset + page.Length < matches.Length ? (int?)(offset + page.Length) : null, items = page };
    }

    private static WoUgoData[] Choices(Type type)
    {
        var typeId = CwPlatform.ResolveNotNull<IUgoTypes>().GetTypeId(type);
        return CwPlatform.ResolveNotNull<IUgoBaseComponent>().UgoList.Where(x => x.UgoElemTypeId == typeId).ToArray();
    }

    public static WoUgoData Select(
        Type type,
        string? name = null,
        string? file = null)
    {
        ThrowIf.Argument.IsNull(type, nameof(type));
        if (file != null && name == null)
        {
            throw new ArgumentException("ugoFile передаётся вместе с ugoName.");
        }
        var choices = Choices(type);
        if (name == null && choices.Length > 0)
        {
            return choices[0];
        }
        var matches = choices.Where(x => x.UgoName == name && (file == null || x.FileName == file)).ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException($"Найдено УГО: {matches.Length}. Уточните ugoName/ugoFile по ugo_search для {type.Name}.");
        }
        return matches[0];
    }
}
