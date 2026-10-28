using System;
using System.Linq;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Acad;
using Nanosoft.Acad.BuildingModel;
using Nanosoft.Acad.Net;
using Nanosoft.Piping.Common.Base;
using Nanosoft.Piping.Common.Module;
using Nanosoft.Piping.Common.Pipes;
using Newtonsoft.Json.Linq;

namespace MyPlugin.WaterSupply;

internal sealed class WaterSupplyStandTools
{
    private readonly McpCadContext _context;

    internal WaterSupplyStandTools(McpCadContext context) => _context = context;

    internal void Register(McpToolRegistry tools)
    {
        tools.Add("watersupply_stands_transfer", "Штатно перенести/совместить выбранные стояки между этажами по маркерам. Исходные стояки остаются. Инструкция: watersupply-pipes.",
            new JObject
            {
                ["sourceFloorId"] = McpSchema.String("ID исходного TbFloorContour."),
                ["targetFloorId"] = McpSchema.String("ID другого этажа с маркером."),
                ["ids"] = McpSchema.Array(McpSchema.String("ID стояка на исходном этаже; 1–100.")),
                ["combine"] = McpSchema.Boolean("Совместить с одноимёнными стояками целевого этажа; по умолчанию true.")
            }, new[] { "sourceFloorId", "targetFloorId", "ids" }, Transfer, changesModel: true, supportsItems: true);
        tools.Add("watersupply_stand_cut", "Обрезать верх или низ стояка по подключённым трубам. Инструкция: watersupply-pipes.",
            new JObject
            {
                ["id"] = McpSchema.String("ID стояка."),
                ["end"] = McpSchema.String("top или bottom.")
            }, new[] { "id", "end" }, Cut, changesModel: true, supportsItems: true);
    }

    private object Transfer(JObject args)
    {
        var sourceId = args.Value<string>("sourceFloorId")!;
        var targetId = args.Value<string>("targetFloorId")!;
        var sourceAcId = _context.Object(sourceId).AcId;
        var targetAcId = _context.Object(targetId).AcId;
        if (sourceAcId == targetAcId)
        {
            throw new ArgumentException("Исходный и целевой этажи должны различаться.");
        }
        if (args["ids"] is not JArray ids || ids.Count is < 1 or > 100)
        {
            throw new ArgumentException("ids: от 1 до 100 стояков.");
        }
        var selected = ids.Values<string>().Select(x => _context.Object(x!).AcId).ToHashSet();
        // Сервис связывает стояки с этажами через AreaContours; ему нужна загруженная сеть проекта.
        var net = CwPlatform.ResolveNotNull<IAcadNetFactory>().NewNet();
        net.Load();
        var service = CwPlatform.ResolveNotNull<IStandPipeMovementService>();
        var floors = service.GetFloorItems(net);
        var source = floors.SingleOrDefault(x => x.FloorContour.AcId == sourceAcId)
                     ?? throw new ArgumentException("Исходный этаж не найден сервисом стояков.");
        var target = floors.SingleOrDefault(x => x.FloorContour.AcId == targetAcId)
                     ?? throw new ArgumentException("Целевой этаж не найден сервисом стояков.");
        // Та же геометрическая привязка маркера, которую использует штатный сервис переноса.
        var markedFloors = net.Nodes.OfType<TbFloorMarker>().Cast<IAreaObject>()
            .Select(x => x.AreaContours.OfType<TbFloorContour>().FirstOrDefault()?.AcId).ToHashSet();
        if (!markedFloors.Contains(source.FloorContour.AcId) || !markedFloors.Contains(target.FloorContour.AcId))
        {
            throw new ArgumentException("Обоим этажам нужны маркеры совмещения.");
        }
        var stands = source.Children.OfType<StandPipeItem>().ToArray();
        if (!selected.IsSubsetOf(stands.Select(x => x.StandPipe.AcId)))
        {
            throw new ArgumentException("Выбранные стояки должны принадлежать исходному этажу.");
        }
        foreach (var stand in stands)
        {
            stand.IsChecked = selected.Contains(stand.StandPipe.AcId);
        }
        try
        {
            service.MoveStandPipes(source, target, args.Value<bool?>("combine") ?? true, true, true, TransferMode.None);
            return new { processed = selected.Count, targetFloorId = targetId };
        }
        finally
        {
            _context.ForgetOpenedObjects();
        }
    }

    private object Cut(JObject args)
    {
        var stand = _context.Object(args.Value<string>("id")!) as TbStandPipe
                    ?? throw new ArgumentException("id должен указывать на стояк.");
        switch (args.Value<string>("end"))
        {
            case "top": stand.CutUpStandPipe(); break;
            case "bottom": stand.CutDownStandPipe(); break;
            default: throw new ArgumentException("end: top или bottom.");
        }
        // CutUp/DownStandPipe сами сохраняют и перерисовывают стояк.
        return new { id = stand.AcId.ToGuid().ToString(), bottom = stand.PipeHeightDown, top = stand.PipeHeightUp };
    }
}
