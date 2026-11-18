using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MyPlugin.HierarchyTestCommands;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using Nanosoft.Data;
using Nanosoft.Piping.Common.Base;
using Nanosoft.Piping.Common.Ifaces;
using Nanosoft.Piping.Common.WaterSystems;
using Nanosoft.WS.Addin.Module.WaterConsumer;
using Nanosoft.PipingDB.CalcData;
using Nanosoft.WS.DbObjects;
using Nanosoft.WS.Model;

namespace MyPlugin.WaterSupply.Psa12661;

/// <summary>
/// Настройки нового проекта ВК, без которых расчёт не построит сеть или отбракует приборы:
/// система В1, нормативный водопотребитель с ГВС, сортамент труб в БД проекта и конфигурация трубы В1.
/// </summary>
internal sealed class WaterSupplyDemoSetup
{
    private const double DefaultPipeDiameter = 25;

    private WaterSupplyDemoSetup(string systemName, IWaterTubeConfiguration configuration, int pipeDbId, int consumerId, string summary)
    {
        SystemName = systemName;
        Configuration = configuration;
        PipeDbId = pipeDbId;
        ConsumerId = consumerId;
        Summary = summary;
    }

    internal string SystemName { get; }
    internal IWaterTubeConfiguration Configuration { get; }
    internal int PipeDbId { get; }
    internal int ConsumerId { get; }
    internal string Summary { get; }

    internal static WaterSupplyDemoSetup Create(string projectDirectory)
    {
        var systems = CwPlatform.ResolveNotNull<IWaterSystemService>();
        // Холодный хозяйственно-питьевой водопровод; предпочитаем штатное обозначение В1 (кириллица).
        var system = systems.GetSystemsByType(TbNetType.ColdPotable)
                         .OrderBy(x => x.ShortSystemName == "В1" || x.Name == "В1" ? 0 : 1).FirstOrDefault()
                     ?? throw new InvalidOperationException("В проекте нет системы холодного водопровода (ColdPotable).");

        var consumer = CreateConsumer(projectDirectory);
        var (pipeDbId, series, count) = ImportPipeSeries();
        var configurations = CwPlatform.ResolveNotNull<IWaterSystemConfigurationService>();
        var configuration = configurations.GetConfigurationByProps(system.Id, pipeDbId, 0, 0);
        configurations.Save();
        return new WaterSupplyDemoSetup(system.Name, configuration, pipeDbId, consumer.Id,
            $"система {system.Name}; водопотребитель «{consumer.UserDischargeName}»; трубы «{series}» ({count} шт. в БД проекта)");
    }

    // Норма с ГВС: иначе WsProjectCalculator.IsValidWaterUser отбракует смесители и расчёт не начнётся.
    private static UserDischargeInfo CreateConsumer(string projectDirectory)
    {
        var provider = CwPlatform.ResolveNotNull<ICalcToolsProvider>();
        var norms = provider.DataSources[provider.CurrentSpVersion].GetDataTypedSource(typeof(DbUserDischarge))
                    ?? throw new InvalidOperationException("Не загружены нормативные водопотребители текущей редакции СП.");
        var candidates = norms.DataObjects.Cast<IDataObject>().Select(x => x.Object).OfType<DbUserDischarge>()
            .Where(x => !double.IsNaN(x.MaxHotDischargeHour) && x.MaxDischargeHour > 0).ToArray();
        var norm = candidates.FirstOrDefault(x => x.User.Contains("Административ", StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault()
                   ?? throw new InvalidOperationException("В текущей редакции СП нет водопотребителя с горячей водой.");
        var service = CwPlatform.ResolveNotNull<IWsWaterConsumerService>();
        var consumer = service.CreateNewUserDischargeInfo(norm);
        consumer.UserCount = 100;
        consumer.WaterUsePeriod = 24;
        service.Save(projectDirectory);
        return consumer;
    }

    // Весь сортамент одной серии: расчёт подбирает диаметр внутри серии из БД проекта.
    private static (int dbId, string series, int count) ImportPipeSeries()
    {
        var databases = CwPlatform.ResolveNotNull<IDatabaseProxyComponent>();
        var dbType = typeof(DbPipe);
        var projectSource = databases.GetDb(dbType);
        static bool IsWaterPipe(object value) =>
            value is DbPipe { PipeType: WsPipeType.WaterSupply } pipe && !string.IsNullOrWhiteSpace(pipe.DbSort) && pipe.Diameter > 0;

        // Сначала БД проекта, затем внешние: серия с наибольшим числом диаметров 15…50.
        var best = new[] { projectSource }.Concat(ProjectDatabaseProducts.ExternalSources(dbType, projectSource))
            .SelectMany(source => ProjectDatabaseProducts.Objects(source, dbType, IsWaterPipe)
                .GroupBy(x => ((DbPipe)x.Object).DbSort)
                .Select(group => (source, series: group.Key, items: group.ToArray())))
            .Select((x, order) => (x.source, x.series, x.items, order,
                diameters: x.items.Select(i => ((DbPipe)i.Object).Diameter).Where(d => d >= 15 && d <= 50).Distinct().Count()))
            .OrderByDescending(x => x.diameters).ThenBy(x => x.order).FirstOrDefault();
        if (best.items == null || best.diameters == 0)
        {
            throw new InvalidOperationException("Ни в БД проекта, ни в подключённых БД нет водопроводных труб (DbPipe, тип «Водоснабжение», с серией).");
        }

        var preferred = best.items.OrderBy(x => Math.Abs(((DbPipe)x.Object).Diameter - DefaultPipeDiameter)).First();
        if (ReferenceEquals(best.source, projectSource))
        {
            return (preferred.Id, best.series, best.items.Length);
        }

        var imported = ProjectDatabaseProducts.Import(projectSource, best.items);
        return (imported[Array.IndexOf(best.items, preferred)].Id, best.series, imported.Count);
    }

    internal static string ProjectDirectory()
    {
        var project = global::Nanosoft.Studio.Studio.Instance.Workspace.Projects.Single();
        return Path.GetDirectoryName(project.Path)!;
    }

    internal static IReadOnlyList<int> ProductIds(Type dbType) =>
        ProjectDatabaseProducts.GetOrImport(CwPlatform.ResolveNotNull<IDatabaseProxyComponent>(), dbType, 1);
}
