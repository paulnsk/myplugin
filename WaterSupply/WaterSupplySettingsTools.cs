using System;
using System.IO;
using System.Linq;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;
using Nanosoft.WS.Addin.Module.WaterConsumer;
using Nanosoft.WS.Model;
using Newtonsoft.Json.Linq;

namespace MyPlugin.WaterSupply;

internal sealed class WaterSupplySettingsTools
{
    private readonly McpCadContext _context;

    internal WaterSupplySettingsTools(McpCadContext context) => _context = context;

    internal void Register(McpToolRegistry tools)
    {
        tools.Add("watersupply_consumers_get", "Водопотребители проекта и страница нормативных типов для добавления. Инструкция: watersupply-settings.",
            new JObject
            {
                ["query"] = McpSchema.String("Фильтр нормативных типов по названию; по умолчанию пустой."),
                ["offset"] = McpSchema.Integer("По умолчанию 0."),
                ["limit"] = McpSchema.Integer("По умолчанию 10, максимум 30.")
            }, Array.Empty<string>(), Read);
        tools.Add("watersupply_consumer_set", "Добавить нормативного водопотребителя либо изменить существующего. Возвращает userId для UserIndex приборов. Инструкция: watersupply-settings.",
            new JObject
            {
                ["userId"] = McpSchema.Integer("ID существующего потребителя; без него создаётся новый."),
                ["normDbId"] = McpSchema.Integer("ID нормативного типа из watersupply_consumers_get; нужен для нового потребителя."),
                ["count"] = McpSchema.Number("Количество потребителей, больше 0."),
                ["hours"] = McpSchema.Number("Период водопотребления, часов в сутки: больше 0, не больше 24.")
            }, new[] { "count", "hours" }, Set, changesModel: true, supportsItems: true);
    }

    private object Read(JObject args)
    {
        _context.RequireProject();
        var query = args.Value<string>("query") ?? "";
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 10, 1, 30);
        var norms = Norms().DataObjects.Where(x => x.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Skip(offset).Take(limit + 1).ToArray();
        var consumers = CwPlatform.ResolveNotNull<IWsWaterConsumerService>().UserDischargeInfos.Values
            .OrderBy(x => x.Id).Skip(offset).Take(limit + 1).ToArray();
        return new
        {
            consumers = consumers.Take(limit)
                .Select(x => new { userId = x.Id, title = x.UserDischargeName, count = x.UserCount, hours = x.WaterUsePeriod }).ToArray(),
            consumersNextOffset = consumers.Length > limit ? (int?)(offset + limit) : null,
            norms = norms.Take(limit).Select(x => new { normDbId = x.Id, title = x.Text }).ToArray(),
            nextOffset = norms.Length > limit ? (int?)(offset + limit) : null
        };
    }

    private object Set(JObject args)
    {
        var project = _context.Project(null);
        var count = args.Value<double>("count");
        var hours = args.Value<double>("hours");
        if (!double.IsFinite(count) || count <= 0 || !double.IsFinite(hours) || hours <= 0 || hours > 24)
        {
            throw new ArgumentException("count > 0; 0 < hours <= 24.");
        }
        var service = CwPlatform.ResolveNotNull<IWsWaterConsumerService>();
        var id = args.Value<int?>("userId");
        if (id != null && args["normDbId"] != null)
        {
            throw new ArgumentException("Для изменения передайте userId без normDbId.");
        }
        var consumer = id != null
            ? service.GetUserDischargeInfoById(id.Value) ?? throw new ArgumentException("Водопотребитель не найден.")
            : service.CreateNewUserDischargeInfo(Norms().GetDataObject(args.Value<int?>("normDbId")
                ?? throw new ArgumentException("Для нового потребителя нужен normDbId.")).Object as DbUserDischarge
                ?? throw new ArgumentException("Нормативный тип не найден в текущей редакции СП."));
        consumer.UserCount = count;
        consumer.WaterUsePeriod = hours;
        service.Save(Path.GetDirectoryName(project.Path)!);
        return new { userId = consumer.Id };
    }

    private static Nanosoft.Data.IDataTypedSource Norms()
    {
        var provider = CwPlatform.ResolveNotNull<ICalcToolsProvider>();
        return provider.DataSources[provider.CurrentSpVersion].GetDataTypedSource(typeof(DbUserDischarge))
               ?? throw new InvalidOperationException("Не загружены нормативные водопотребители текущей редакции СП.");
    }
}
