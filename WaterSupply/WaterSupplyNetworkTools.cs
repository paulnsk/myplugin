using System;
using System.Linq;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Piping.Common.Ifaces;
using Nanosoft.Piping.Common.Pipes;
using Nanosoft.WS.Addin;
using Newtonsoft.Json.Linq;

namespace MyPlugin.WaterSupply;

internal sealed class WaterSupplyNetworkTools
{
    private readonly McpCadContext _context;

    internal WaterSupplyNetworkTools(McpCadContext context) => _context = context;

    internal void Register(McpToolRegistry tools)
    {
        tools.Add("watersupply_systems_get", "Системы ВК и страница конфигураций труб. Инструкция: watersupply-pipes.",
            new JObject
            {
                ["offset"] = McpSchema.Integer("По умолчанию 0."),
                ["limit"] = McpSchema.Integer("По умолчанию 20, максимум 50.")
            }, Array.Empty<string>(), ReadSystems);
        tools.Add("watersupply_configuration_set", "Найти или создать конфигурацию системы/изделия трубы. Изделие должно быть в БД проекта. Инструкция: watersupply-pipes.",
            new JObject
            {
                ["systemId"] = McpSchema.Integer("ID из watersupply_systems_get."),
                ["dbId"] = McpSchema.Integer("Проектный ID DbPipe; не внешний ID.")
            }, new[] { "systemId", "dbId" }, Configuration, changesModel: true, supportsItems: true);
        tools.Add("watersupply_pipes_apply", "Назначить конфигурацию существующим горизонтальным трубам. Геометрия и соединения сохраняются. Инструкция: watersupply-pipes.",
            new JObject
            {
                ["configurationId"] = McpSchema.Integer("Из watersupply_configuration_set или watersupply_systems_get."),
                ["ids"] = McpSchema.Array(McpSchema.String("ID TbTrace; от 1 до 100."))
            }, new[] { "configurationId", "ids" }, ApplyPipes, changesModel: true, supportsItems: true);
        tools.Add("watersupply_refresh", "Штатно обновить модель ВК: вертикальные подключения, системы и маркировка. Инструкция: watersupply-pipes.",
            new JObject(), Array.Empty<string>(), Refresh, changesModel: true);
        tools.Add("watersupply_calculate", "Рассчитать сети ВК по настройкам вводов; вернуть страницу кратких результатов и ошибок, без отчётов. Инструкция: watersupply-calculation.",
            new JObject
            {
                ["offset"] = McpSchema.Integer("По умолчанию 0."),
                ["limit"] = McpSchema.Integer("По умолчанию 10, максимум 30.")
            }, Array.Empty<string>(), Calculate, changesModel: true);
    }

    private object ReadSystems(JObject args)
    {
        _context.RequireProject();
        var configurations = CwPlatform.ResolveNotNull<IWaterSystemConfigurationService>().WaterSystemsByType();
        var systems = CwPlatform.ResolveNotNull<IWaterSystemService>().AllSettings;
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 20, 1, 50);
        return new
        {
            systems = systems.Skip(offset).Take(limit)
                .Select(x => new { systemId = x.Id, name = x.Name, shortName = x.ShortSystemName, type = x.WaterSystemType.ToString() }).ToArray(),
            systemsNextOffset = offset + limit < systems.Count ? (int?)(offset + limit) : null,
            configurations = configurations.Skip(offset).Take(limit)
                .Select(x => new { configurationId = x.Id, name = x.Name, systemId = x.SystemId, dbId = x.DbId }).ToArray(),
            nextOffset = offset + limit < configurations.Count ? (int?)(offset + limit) : null
        };
    }

    private object Configuration(JObject args)
    {
        _context.RequireProject();
        var systemId = args.Value<int>("systemId");
        if (!CwPlatform.ResolveNotNull<IWaterSystemService>().IsSystemId(systemId))
        {
            throw new ArgumentException("Неизвестный systemId; прочитайте watersupply_systems_get.");
        }
        var dbId = args.Value<int>("dbId");
        if (CwPlatform.ResolveNotNull<Nanosoft.CAE.Db.DatabaseProxyComponent.IDatabaseProxyComponent>()
            .GetDataObject(typeof(Nanosoft.WS.DbObjects.DbPipe), dbId).Object is not Nanosoft.WS.DbObjects.DbPipe)
        {
            throw new ArgumentException("Труба не найдена в БД проекта; сначала database_import.");
        }
        var service = CwPlatform.ResolveNotNull<IWaterSystemConfigurationService>();
        var configuration = service.GetConfigurationByProps(systemId, dbId, 0, 0);
        service.Save();
        return new { configurationId = configuration.Id };
    }

    private object ApplyPipes(JObject args)
    {
        _context.RequireProject();
        if (args["ids"] is not JArray ids || ids.Count is < 1 or > 100)
        {
            throw new ArgumentException("ids должен содержать от 1 до 100 ID труб.");
        }
        var service = CwPlatform.ResolveNotNull<IWaterSystemConfigurationService>();
        var id = args.Value<int>("configurationId");
        var configuration = service.WaterSystemsByType().SingleOrDefault(x => x.Id == id)
                            ?? throw new ArgumentException("Конфигурация не найдена.");
        var pipes = ids.Values<string>().Distinct().Select(x => _context.Object(x!) as TbTrace
            ?? throw new ArgumentException("Все ids должны указывать на горизонтальные трубы TbTrace.")).ToArray();
        foreach (var pipe in pipes)
        {
            pipe.CopyThread(configuration);
        }
        // CopyThread меняет также дочерние нити: сохраняем всю изменённую часть этой сети.
        _context.Net.ModelObjects.SaveMeModifiedObjects();
        foreach (var pipe in pipes)
        {
            pipe.UpdateView();
        }
        return new { updated = pipes.Length };
    }

    private object Refresh(JObject args)
    {
        _context.RequireProject();
        try
        {
            CwPlatform.ResolveNotNull<INetRefresh>().Refresh();
            return new { refreshed = true };
        }
        finally
        {
            // Штатные сервисы используют собственные IAcadNet, старые оболочки больше не актуальны.
            _context.ForgetOpenedObjects();
        }
    }

    private object Calculate(JObject args)
    {
        var project = _context.Project(null);
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 10, 1, 30);
        try
        {
            var nets = CwPlatform.ResolveNotNull<IWsProjectCalculator>().Calculate(project, false).ToArray();
            return new
            {
                total = nets.Length,
                nets = nets.Skip(offset).Take(limit).Select(x => new
                {
                    systemId = x.SystemId, name = x.SystemName,
                    dischargeLitersPerSecond = x.DesignDischargeSec,
                    requiredHead = x.SystemInput.NeedfulHead,
                    errorCount = x.NetErrors.Count,
                    errors = x.NetErrors.Take(5).Select(e => e.Message).ToArray()
                }).ToArray(),
                nextOffset = offset + limit < nets.Length ? (int?)(offset + limit) : null
            };
        }
        finally
        {
            _context.ForgetOpenedObjects();
        }
    }
}
