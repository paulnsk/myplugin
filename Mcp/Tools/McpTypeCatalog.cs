using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Guard;
using Nanosoft.Acad;
using Nanosoft.Acad.Ifaces.Contours;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Data;
using Nanosoft.Utils;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Tools;

internal sealed class McpTypeCatalog
{
    private readonly McpCadContext _context;

    public McpTypeCatalog(McpCadContext context)
    {
        ThrowIf.Argument.IsNull(context, nameof(context));
        _context = context;
    }

    public Type[] ObjectTypes()
    {
        return CwPlatform.ResolveNotNull<IUgoTypes>().GetTypes().Cast<Type>()
            .Where(x => !x.IsAbstract && typeof(IAcadNode).IsAssignableFrom(x)).Distinct().ToArray();
    }

    public Type[] DatabaseTypes()
    {
        return AddIn.DataModel.DataAssemblies
            .SelectMany(x => x.DataTypes).Select(x => x.Type)
            .Where(x => !x.IsAbstract).Distinct().ToArray();
    }

    public static Type[] MappedDatabaseTypes(Type type)
    {
        ThrowIf.Argument.IsNull(type, nameof(type));
        return type.GetCustomAttributes(typeof(DbMapTypeAttribute), true)
            .Cast<DbMapTypeAttribute>().Select(x => x.DbType).Distinct().ToArray();
    }

    public Type ResolveObject(string name)
    {
        ThrowIf.Argument.IsNull(name, nameof(name));
        return Resolve(ObjectTypes(), name);
    }

    public Type ResolveDatabase(string name)
    {
        ThrowIf.Argument.IsNull(name, nameof(name));
        return Resolve(DatabaseTypes(), name);
    }

    private static Type Resolve(IEnumerable<Type> types, string name)
    {
        var matches = types.Where(x => x.FullName == name || x.Name == name).ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                $"Type '{name}' has {matches.Length} matches. Use a full type name returned by types_search.");
        }

        return matches[0];
    }

    public void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("types_search",
            "Discover registered CAD types or DB types by CLR name/localized metadata. No hard-coded product catalog. Results are paged.",
            new JObject
            {
                ["query"] = McpSchema.String(
                    "Substring, e.g. Lighting, светильник, FloorContour, RoomContour or Trace."),
                ["database"] = McpSchema.Boolean("Search DB types instead of CAD types; default false."),
                ["offset"] = McpSchema.Integer("Default 0."), ["limit"] = McpSchema.Integer("Default 30, maximum 100.")
            }, Array.Empty<string>(), Search);
        tools.Add("types_describe",
            "Свойства типа или объекта по id, в том числе внутренних групп/портов. readOnly отмечает свойства без setter. query и страницы ограничивают ответ.",
            new JObject
            {
                ["type"] = McpSchema.String("Full type name from types_search."),
                ["id"] = McpSchema.String("Вместо type: ID существующего объекта."),
                ["database"] = McpSchema.Boolean("Default false."),
                ["query"] = McpSchema.String("Фильтр имён/подписей свойств."),
                ["offset"] = McpSchema.Integer("По умолчанию 0."),
                ["limit"] = McpSchema.Integer("Число свойств: по умолчанию 20, максимум 100.")
            },
            Array.Empty<string>(), Describe);
    }

    private object Search(JObject args)
    {
        var query = args.Value<string>("query") ?? "";
        var database = args.Value<bool?>("database") ?? false;
        var matches = (database ? DatabaseTypes() : ObjectTypes()).Where(x =>
                (x.FullName + " " + Caption(x)).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.FullName).ToArray();
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 30, 1, 100);
        return new
        {
            total = matches.Length, offset,
            types = matches.Skip(offset).Take(limit)
                .Select(x => new { type = x.FullName, caption = Caption(x), mode = Mode(x) }).ToArray()
        };
    }

    private object Describe(JObject args)
    {
        var name = args.Value<string>("type");
        var id = args.Value<string>("id");
        var database = args.Value<bool?>("database") ?? false;
        if ((name == null) == (id == null) || (id != null && database))
        {
            throw new ArgumentException("Передайте либо type (с database для БД), либо id объекта.");
        }
        var type = id != null ? _context.Object(id).GetType() : database ? ResolveDatabase(name!) : ResolveObject(name!);
        var query = args.Value<string>("query") ?? "";
        var properties = McpProperties.Readable(type)
            .Where(x => (x.Name + " " + Caption(x)).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 20, 1, 100);
        return new
        {
            type = type.FullName, caption = Caption(type), mode = id == null ? Mode(type) : null,
            databaseTypes = MappedDatabaseTypes(type).Select(x => x.FullName).ToArray(),
            total = properties.Length, offset,
            properties = properties.Skip(offset).Take(limit).Select(x => new
            {
                name = x.Name, valueType = (Nullable.GetUnderlyingType(x.PropertyType) ?? x.PropertyType).Name,
                readOnly = x.SetMethod?.IsPublic != true,
                caption = Caption(x), enumValues = (Nullable.GetUnderlyingType(x.PropertyType) ?? x.PropertyType).IsEnum
                    ? Enum.GetNames(Nullable.GetUnderlyingType(x.PropertyType) ?? x.PropertyType)
                    : null
            }).ToArray()
        };
    }

    internal static string Mode(Type type)
    {
        ThrowIf.Argument.IsNull(type, nameof(type));
        return typeof(IAcadContour).IsAssignableFrom(type) ? "contour_create"
            : typeof(AcadTrace).IsAssignableFrom(type) ? "traces_connect" : "objects_create";
    }

    internal static string Caption(MemberInfo member)
    {
        ThrowIf.Argument.IsNull(member, nameof(member));
        return string.Join(" ", member.GetCustomAttributes(true)
            .Select(x => x.GetType().GetProperty("Text")?.GetValue(x)?.ToString()).Where(x => !string.IsNullOrEmpty(x))
            .Distinct());
    }
}
