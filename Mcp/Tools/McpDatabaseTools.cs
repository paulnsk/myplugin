using System;
using System.Linq;
using Guard;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Data;
using Nanosoft.Utils;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Tools;

internal sealed class McpDatabaseTools
{
    private readonly McpCadContext _context;
    private readonly McpTypeCatalog _types;

    public McpDatabaseTools(McpCadContext context, McpTypeCatalog types)
    {
        ThrowIf.Argument.IsNull(types, nameof(types));
        ThrowIf.Argument.IsNull(context, nameof(context));
        _context = context;
        _types = types;
    }

    public void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("database_search",
            "Search products in a discovered DB type. Searches project and loaded external databases; returns sourcePath, dbType and dbId. No import is implicit.",
            new JObject
            {
                ["type"] =
                    McpSchema.String("DB type from types_describe.databaseTypes or types_search(database=true)."),
                ["query"] = McpSchema.String("Substring of record title/category; default empty."),
                ["offset"] = McpSchema.Integer("Default 0."), ["limit"] = McpSchema.Integer("Default 30, max 100.")
            }, new[] { "type" }, Search);
        var readSchema = ProductSchema();
        readSchema["fields"] = McpSchema.Array(McpSchema.String("Имена свойств; * — все. Без fields только карточка изделия."));
        tools.Add("database_get", "Карточка изделия. fields выбирает свойства, [\"*\"] — все.",
            readSchema, new[] { "type", "dbId" }, args =>
            {
                var type = _types.ResolveDatabase(args.Value<string>("type")!);
                var source = Source(type, args.Value<string>("sourcePath"));
                var item = source.GetDataTypedSource(type)?.GetDataObject(args.Value<int>("dbId"))
                           ?? throw new ArgumentException("DB record not found.");
                var fields = args["fields"]?.Values<string>().ToArray();
                var properties = fields == null ? null : McpProperties.Read(item.Object, fields.Contains("*") ? null : fields);
                return new
                {
                    dbId = item.Id, type = type.FullName, title = item.Text,
                    properties
                };
            });
        tools.Add("database_import",
            "Import one selected external product and its dependencies into the project, synchronously. Returns the PROJECT dbId for objects_create.",
            ProductSchema(), new[] { "type", "dbId", "sourcePath" }, Import, true);
        tools.Add("database_update", "Изменить свойства изделия ТОЛЬКО в БД проекта. Внешние базы не изменяются. Поля — из types_describe(database=true).",
            new JObject
            {
                ["type"] = McpSchema.String("Тип БД."),
                ["dbId"] = McpSchema.Integer("ID записи БД проекта."),
                ["properties"] = McpSchema.Properties("Изменяемые скалярные свойства.")
            }, new[] { "type", "dbId", "properties" }, Update, changesModel: true, supportsItems: true);
    }

    private static JObject ProductSchema()
    {
        return new JObject
        {
            ["type"] = McpSchema.String("Full DB type."),
            ["dbId"] = McpSchema.Integer("Record id in the selected source."),
            ["sourcePath"] = McpSchema.String("sourcePath returned by database_search; omit for the project DB.")
        };
    }

    private object Update(JObject args)
    {
        var type = _types.ResolveDatabase(args.Value<string>("type")!);
        var item = Source(type, null).GetDataTypedSource(type)?.GetDataObject(args.Value<int>("dbId"))
                   ?? throw new ArgumentException("Изделие не найдено в БД проекта.");
        var value = item.Object;
        var setters = McpProperties.Prepare(type, args["properties"] as JObject
            ?? throw new ArgumentException("properties должен быть объектом."));
        McpProperties.Apply(value, setters);
        item.SaveObject(value, true);
        return new { dbId = item.Id };
    }

    private IDataSource Source(Type type, string? path)
    {
        _context.RequireProject();
        var project = CwPlatform.ResolveNotNull<IDatabaseProxyComponent>().GetDb(type)
                      ?? throw new ArgumentException("No project database for this type.");
        if (string.IsNullOrEmpty(path))
        {
            return project;
        }

        var info = AddIn.DataBase.AllSources.FirstOrDefault(x =>
            string.Equals(x.DbFile, path, StringComparison.OrdinalIgnoreCase));
        return info?.DataSource ?? throw new ArgumentException("Database is not loaded: " + path);
    }

    private object Search(JObject args)
    {
        _context.RequireProject();
        var type = _types.ResolveDatabase(args.Value<string>("type")!);
        var project = Source(type, null);
        var query = args.Value<string>("query") ?? "";
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 30, 1, 100);
        var matches = AddIn.DataBase.AllSources.SelectMany(info =>
        {
            var table = info.DataSource.GetDataTypedSource(type);
            return (table?.DataObjects ?? Enumerable.Empty<IDataObject>())
                .Where(x => (x.Text + " " + x.Category).Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(x => new
                {
                    dbId = x.Id, dbType = type.FullName, title = x.Text, category = x.Category,
                    sourcePath = info.DbFile, sourceName = info.DbName,
                    isProject = ReferenceEquals(info.DataSource, project)
                });
        }).Skip(offset).Take(limit + 1).ToArray();
        return new { offset, hasMore = matches.Length > limit, products = matches.Take(limit).ToArray() };
    }

    private object Import(JObject args)
    {
        var type = _types.ResolveDatabase(args.Value<string>("type")!);
        var source = Source(type, args.Value<string>("sourcePath"));
        var target = Source(type, null);
        var item = source.GetDataTypedSource(type)?.GetDataObject(args.Value<int>("dbId"))
                   ?? throw new ArgumentException("DB record not found.");
        //based on the stand's fixed GetLuminaireDbIds: no asynchronous ProgressShower/UserSelect.
        var imported = ReferenceEquals(source, target)
            ? item
            : new DataBaseExporter(target).Export(item)
              ?? throw new InvalidOperationException("Product import failed.");
        return new { dbId = imported.Id, dbType = type.FullName, title = imported.Text, isProject = true };
    }
}
