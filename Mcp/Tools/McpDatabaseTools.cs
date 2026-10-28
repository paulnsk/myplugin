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
        var importSchema = ProductSchema();
        importSchema["seriesProperty"] = McpSchema.String("Необязательно: строковое свойство серии/сортамента из database_get. Импортирует все изделия с тем же значением, без списка в ответе.");
        tools.Add("database_import",
            "Импортировать изделие и зависимости в БД проекта. seriesProperty добавляет весь выбранный сортамент. Возвращает проектный dbId выбранного изделия.",
            importSchema, new[] { "type", "dbId", "sourcePath" }, Import, true);
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
        var seriesProperty = args.Value<string>("seriesProperty");
        var records = new[] { item };
        if (seriesProperty != null)
        {
            var property = type.GetProperty(seriesProperty);
            if (property == null || property.PropertyType != typeof(string) || property.GetMethod == null || property.GetIndexParameters().Length != 0)
            {
                throw new ArgumentException("seriesProperty должно быть читаемым строковым свойством изделия.");
            }
            var series = property.GetValue(item.Object) as string;
            if (string.IsNullOrWhiteSpace(series))
            {
                throw new ArgumentException("У выбранного изделия не заполнено свойство серии; импорт всего пустого раздела не выполняется.");
            }
            records = source.GetDataTypedSource(type)!.DataObjects
                .Where(x => string.Equals(property.GetValue(x.Object) as string, series, StringComparison.Ordinal)).ToArray();
        }
        // Один синхронный экспортёр сохраняет штатные зависимости и соответствие ID всей серии.
        var exporter = new DataBaseExporter(target);
        IDataObject? selected = null;
        foreach (var record in records)
        {
            var imported = ReferenceEquals(source, target) ? record : exporter.Export(record)
                ?? throw new InvalidOperationException("Не удалось импортировать изделие.");
            if (record.Id == item.Id)
            {
                selected = imported;
            }
        }
        return new { dbId = selected!.Id, count = records.Length };
    }
}
