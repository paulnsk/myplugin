using Nanosoft.Acad;
using Nanosoft.Acad.Net;
using Nanosoft.CAE;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;
using Guard;
using Nanosoft.ObjectLib.WireCommon.CaeFider.Calculation.Power;
using Nanosoft.ObjectLib.WireCommon.CaeFider.GroupRouter;
using Nanosoft.ObjectLib.WireCommon.Db.DbWires;
using Nanosoft.ObjectLib.WireCommon.Db.DbFider;
using Nanosoft.ObjectLib.WireCommon.EtModel;
using Nanosoft.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPlugin.Electrica;

internal sealed class ElectricaNetworkTools
{
    private readonly McpCadContext _context;
    private readonly ElectricaPrivateOperations _operations;

    internal ElectricaNetworkTools(McpCadContext context, ElectricaPrivateOperations operations)
    {
        ThrowIf.Argument.IsNull(context, nameof(context));
        ThrowIf.Argument.IsNull(operations, nameof(operations));
        _context = context;
        _operations = operations;
    }

    internal void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("electrica_cables_route", "Проложить кабели выбранных групп/шкафов по уже нарисованным трассам. Логические связи должны существовать. Инструкция: electrica-cables.",
            new JObject
            {
                ["ids"] = McpSchema.Array(McpSchema.String("ID группы или шкафа; 1–100, без одновременного выбора шкафа и его групп.")),
                ["replace"] = McpSchema.Boolean("Удалить прежнюю прокладку выбранных групп перед новой; по умолчанию false.")
            }, new[] { "ids" }, Route, changesModel: true);
        tools.Add("electrica_cables_inspect", "Перестроить и сохранить штатную модель кабелей; показать страницу кабелей группы. Это не чистое чтение. Инструкция: electrica-cables.",
            new JObject
            {
                ["groupId"] = McpSchema.String("ID электрической группы."),
                ["offset"] = McpSchema.Integer("По умолчанию 0."),
                ["limit"] = McpSchema.Integer("По умолчанию 20, максимум 100.")
            }, new[] { "groupId" }, ReadCables, changesModel: true);
        tools.Add("electrica_cables_bind", "Назначить изделие ElWire из БД проекта всем либо выбранным кабелям группы. Проверяет число жил. Инструкция: electrica-cables.",
            new JObject
            {
                ["groupId"] = McpSchema.String("ID группы."),
                ["dbId"] = McpSchema.Integer("ID ElWire в БД проекта."),
                ["cableOutId"] = McpSchema.String("Необязательный фильтр из electrica_cables_inspect."),
                ["segmentId"] = McpSchema.String("Необязательный фильтр из electrica_cables_inspect."),
                ["threads"] = McpSchema.String("Необязательный фильтр жил, точная строка из electrica_cables_inspect.")
            }, new[] { "groupId", "dbId" }, BindCables, changesModel: true, supportsItems: true);
        tools.Add("electrica_calculate", "Перестроить электрическую модель, выполнить расчёты и вернуть страницу результатов групп. Без окна. Инструкция: electrica-network.",
            new JObject
            {
                ["mark"] = McpSchema.Boolean("Также выполнить штатную маркировку оборудования; по умолчанию false."),
                ["groupIds"] = McpSchema.Array(McpSchema.String("Необязательный выбор групп для ответа, не ограничивает сам расчёт.")),
                ["offset"] = McpSchema.Integer("Смещение страницы результатов, по умолчанию 0."),
                ["limit"] = McpSchema.Integer("По умолчанию 10, максимум 30.")
            },
            Array.Empty<string>(), Calculate, changesModel: true);
    }

    private object Route(JObject args)
    {
        _context.RequireProject();
        var ids = (args["ids"] as JArray)?.Values<string>().Select(x => Guid.Parse(x!)).ToArray()
                  ?? throw new ArgumentException("ids должен быть массивом.");
        if (ids.Length is < 1 or > 100 || ids.Distinct().Count() != ids.Length)
        {
            throw new ArgumentException("Нужны 1–100 разных ids.");
        }
        var net = CwPlatform.ResolveNotNull<IAcadNetFactory>().NewNet();
        net.Load(false);
        var targets = ids.Select(x => net.OpenObject(GlobalId.FromGuid(x))
                                      ?? throw new ArgumentException($"Объект {x} не найден.")).ToArray();
        if (targets.Any(x => x is not (ElFider or IFidersShell)))
        {
            throw new ArgumentException("ids должны указывать на электрические группы или шкафы.");
        }
        var includedGroups = targets.SelectMany(x => x switch
        {
            IFidersShell shell => shell.AllFiders.Select(f => f.AcId),
            CaeFider group => new[] { group.AcId },
            _ => Array.Empty<GlobalId>()
        }).ToArray();
        if (includedGroups.Distinct().Count() != includedGroups.Length)
        {
            throw new ArgumentException("Выбраны пересекающиеся наборы групп. Передайте шкафы либо отдельные группы, без повторного обхода.");
        }
        // Геометрию и индексы подготавливаем один раз на весь список, как в штатном мастере.
        net.DijkstraAlgorithm.Initialize();
        var wizard = CwPlatform.ResolveNotNull<IGroupBuilderWizard>() as GroupBuilderWizard
                     ?? throw new InvalidOperationException("Установленный мастер прокладки не поддерживается MCP-адаптером.");
        try
        {
            var unrouted = _operations.Route(wizard, net, targets, args.Value<bool?>("replace") == true);
            net.ModelObjects.SaveMeModifiedObjects();
            return new
            {
                processed = ids.Length, unroutedCount = unrouted.Count,
                unroutedObjectIds = unrouted.Take(100).Select(x => x.ToGuid().ToString()).ToArray(), hasMore = unrouted.Count > 100
            };
        }
        catch (Exception exception)
        {
            exception.Data["appliedObjectIds"] = new JArray(ids.Select(x => x.ToString()));
            throw;
        }
        finally
        {
            // Штатный алгоритм накапливает подсветку для своего окна. В MCP окна и ожидания клика нет.
            CwPlatform.ResolveNotNull<ICaeObjectHighlightService>().Reset();
        }
    }

    private object ReadCables(JObject args)
    {
        var net = BuildModel();
        var group = Group(net, args.Value<string>("groupId")!);
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 20, 1, 100);
        var cables = Cables(group).Skip(offset).Take(limit + 1).ToArray();
        return new
        {
            offset, hasMore = cables.Length > limit,
            cables = cables.Take(limit).Select(x => new
            {
                cableOutId = x.ParentSummary.CabelOut.AcId.ToGuid().ToString(), segmentId = x.SegId.ToGuid().ToString(),
                threads = x.ThreadData.ToString(), lengthMeters = x.CableLength, count = x.CabelNumber,
                dbId = x.CableRef?.CabelDbId ?? 0, title = x.CableName
            }).ToArray()
        };
    }

    private object BindCables(JObject args)
    {
        _context.RequireProject();
        var wire = CwPlatform.ResolveNotNull<IDatabaseProxyComponent>().GetDataObject(typeof(ElWire), args.Value<int>("dbId"))?.Object as ElWire
                   ?? throw new ArgumentException("Кабель ElWire не найден в БД проекта. Сначала database_import.");
        var cableOutId = OptionalId(args, "cableOutId");
        var segmentId = OptionalId(args, "segmentId");
        var threads = args.Value<string>("threads");
        var net = BuildModel();
        var group = Group(net, args.Value<string>("groupId")!);
        var cables = Cables(group).Where(x =>
            (cableOutId == null || x.ParentSummary.CabelOut.AcId.ToGuid() == cableOutId) &&
            (segmentId == null || x.SegId.ToGuid() == segmentId) &&
            (threads == null || x.ThreadData.ToString() == threads)).ToArray();
        if (cables.Length == 0)
        {
            throw new ArgumentException("Подходящих кабелей нет. Проверьте логические связи и прокладку через electrica_cables_inspect.");
        }
        if (cables.Any(x => !x.IsWireSuitablePredicate()(wire)))
        {
            throw new ArgumentException("У изделия недостаточно жил для части выбранных кабелей. Уточните фильтр либо выберите другое изделие.");
        }
        try
        {
            foreach (var cable in cables)
            {
                cable.SetWire(wire);
                // SetWire перехватывает исключения внутри себя. Успех подтверждаем по сохранённой привязке, не по отсутствию исключения.
                if (cable.CableRef?.CabelDbId != wire.DbId)
                {
                    throw new InvalidOperationException("Штатный алгоритм не сохранил изделие кабеля.");
                }
            }
            CwPlatform.ResolveNotNull<ICanalWireTracer>().TraceWires(group, true);
            net.ModelObjects.SaveMeModifiedObjects();
            return new { groupId = group.AcId.ToGuid().ToString(), updated = cables.Length, dbId = wire.DbId };
        }
        catch (Exception exception)
        {
            exception.Data["appliedObjectIds"] = new JArray(group.AcId.ToGuid().ToString());
            throw;
        }
    }

    private object Calculate(JObject args)
    {
        if (args["groupIds"] != null && args["groupIds"] is not JArray)
        {
            throw new ArgumentException("groupIds должен быть массивом ID групп.");
        }
        var selected = (args["groupIds"] as JArray)?.Values<string>().Select(x => Guid.Parse(x!)).ToHashSet();
        if (selected is { Count: < 1 or > 100 })
        {
            throw new ArgumentException("groupIds: 1–100 ID групп либо не передавайте поле.");
        }
        var net = BuildModel();
        if (selected != null)
        {
            foreach (var id in selected)
            {
                Group(net, id.ToString());
            }
        }
        CwPlatform.ResolveNotNull<IElNetCalculator>().CalculateAcadNet(net);
        if (args.Value<bool?>("mark") == true)
        {
            CwPlatform.ResolveNotNull<IElTeEquipmentNumeratorService>().MarkEquipment(net);
            net.ModelObjects.SaveMeModifiedObjects();
        }
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 10, 1, 30);
        var groups = net.Nodes.OfType<ElFider>().Where(x => selected == null || selected.Contains(x.AcId.ToGuid()))
            .OrderBy(x => x.AcId.ToGuid()).Skip(offset).Take(limit + 1).ToArray();
        return new
        {
            calculated = true, marked = args.Value<bool?>("mark") == true, offset, hasMore = groups.Length > limit,
            groups = groups.Take(limit).Select(CalculationResult).ToArray()
        };
    }

    private static object CalculationResult(ElFider group)
    {
        var result = group.CalcResult;
        var data = group.GetPowerCalculations(PowerCalculationMode.Normal, ElPowerCalculator.CalcMode.EMERGENCY)
            .GetMaxCalculationData<IPowerCalcData>() as PcData;
        var failed = data == null ? null : FailedChecks(data.CheckContainer).ToArray();
        return new
        {
            id = group.AcId.ToGuid().ToString(), title = group.TitleName,
            powerKw = result.Pcalc, currentA = result.Icalc, deltaUPercent = result.DeltaU,
            minKzKa = result.KzResult.Ikz_min_1.Ikz,
            maxKzKa = group.PhaseCount == PhaseCountEnum.THREE ? result.KzResult.Ikz_max_3.Ikz : result.KzResult.Ikz_max_1.Ikz,
            failedChecksCount = failed?.Length, failedChecks = failed?.Take(5).ToArray(), hasMoreChecks = failed?.Length > 5
        };
    }

    private static IEnumerable<string> FailedChecks(ElCalcCheckContainer container)
    {
        foreach (var item in container.Children)
        {
            if (item is ElCalcCheck { IsValid: false } check)
            {
                yield return check.ToString();
            }
            else if (item is ElCalcCheckContainer nested)
            {
                foreach (var message in FailedChecks(nested))
                {
                    yield return message;
                }
            }
        }
    }

    private IAcadNet BuildModel()
    {
        _context.RequireProject();
        // Публичный headless-вход строит группы и кабельные записи, но сам электрический расчёт не запускает.
        return CwPlatform.ResolveNotNull<IEtModelService>().BuildEtModel().AcadNet;
    }

    private static CaeFider Group(IAcadNet net, string id)
    {
        return net.OpenObject(GlobalId.FromGuid(Guid.Parse(id))) as ElFider
               ?? throw new ArgumentException("groupId должен указывать на электрическую группу.");
    }

    private static IEnumerable<ElThreadedCable> Cables(CaeFider group)
    {
        return group.FiderGroupNet?.CabelRecords.OfType<ElThreadedCable>() ?? Enumerable.Empty<ElThreadedCable>();
    }

    private static Guid? OptionalId(JObject args, string name)
    {
        var value = args.Value<string>(name);
        return value == null ? null : Guid.Parse(value);
    }
}
