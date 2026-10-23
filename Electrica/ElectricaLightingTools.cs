using System;
using System.Collections.Generic;
using System.Linq;
using Nanosoft.Acad;
using Guard;
using Nanosoft.Acad.Net;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using Nanosoft.Acad.Interop.NativeComponent;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;
using Nanosoft.CAE.RoomsAndFloors;
using Nanosoft.Electrica.Calc.LightCalc;
using Nanosoft.ObjectLib.ESN.ElContours;
using Nanosoft.ObjectLib.ESN.LumRoomData;
using Nanosoft.ObjectLib.ESN.Db.DbImpl;
using Nanosoft.Utils;
using Nanosoft.Platform.FormExtension;
using Nanosoft.Platform.PlatformIfaces;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Electrica;

internal sealed class ElectricaLightingTools
{
    private readonly McpCadContext _context;
    private readonly ElectricaPrivateOperations _operations;

    internal ElectricaLightingTools(McpCadContext context, ElectricaPrivateOperations operations)
    {
        ThrowIf.Argument.IsNull(context, nameof(context));
        ThrowIf.Argument.IsNull(operations, nameof(operations));
        _context = context;
        _operations = operations;
    }

    internal void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("electrica_lighting_place",
            "Ки: рассчитать и расставить светильники в комнате. Существующие светильники заменяются только при replace=true. Инструкция: electrica-lighting.",
            new JObject
            {
                ["planPath"] = McpSchema.String("DWG комнаты."),
                ["roomId"] = McpSchema.String("ID RoomContour."),
                ["dbId"] = McpSchema.Integer("Светильник из БД проекта (database_import)."),
                ["lux"] = McpSchema.Number("Нормируемая освещённость, по умолчанию 300 лк."),
                ["mountingHeight"] = McpSchema.Number("Высота установки, по умолчанию 3000 мм."),
                ["workSurfaceHeight"] = McpSchema.Number("Рабочая поверхность, по умолчанию 800 мм."),
                ["ceilingReflectance"] = McpSchema.Number("Коэффициент отражения потолка; без поля сохраняется значение комнаты."),
                ["wallsReflectance"] = McpSchema.Number("Коэффициент отражения стен."),
                ["floorReflectance"] = McpSchema.Number("Коэффициент отражения пола."),
                ["reserveFactor"] = McpSchema.Number("Коэффициент запаса."),
                ["ugoName"] = McpSchema.String("Необязательное имя из ugo_search."),
                ["ugoFile"] = McpSchema.String("Файл УГО для неоднозначного имени."),
                ["replace"] = McpSchema.Boolean("По умолчанию false. true разрешает штатную замену светильников внутри комнаты.")
            }, new[] { "planPath", "roomId", "dbId" }, Place, changesModel: true, supportsItems: true);
    }

    private object Place(JObject args)
    {
        _context.ActivatePlan(args.Value<string>("planPath")!);
        // Установщик использует геометрический индекс сети. Загружаем свежую сеть комнаты, включая уже существующие светильники.
        var net = CwPlatform.ResolveNotNull<IAcadNetFactory>().NewNet();
        net.Load(false);
        var room = net.OpenObject(GlobalId.FromGuid(Guid.Parse(args.Value<string>("roomId")!))) as RoomContour
                   ?? throw new ArgumentException("roomId должен указывать на помещение.");
        var paper = CwPlatform.ResolveNotNull<IAcadDBSetter>().GetCurrentPaperId();
        if (room.AcId.paperId != paper)
        {
            throw new ArgumentException("Помещение находится не на planPath.");
        }
        if (room.CountourRectArray.Count == 0)
        {
            throw new ArgumentException("Помещение не разбито на прямоугольные области. Его нужно разбить штатной командой либо пропустить.");
        }
        var dbId = args.Value<int>("dbId");
        if (CwPlatform.ResolveNotNull<IDatabaseProxyComponent>().GetDataObject(typeof(DbElLighting), dbId) == null)
        {
            throw new ArgumentException("Светильника нет в БД проекта; сначала database_import.");
        }
        var existing = net.ModelAreaData.GetObjectsInsideArea<ElLighting>(room, true);
        if (existing.Count > 0 && args.Value<bool?>("replace") != true)
        {
            throw new ArgumentException($"В комнате уже {existing.Count} светильников. Для замены нужен replace=true.");
        }
        var lux = Number(args, "lux", 300, 0, double.MaxValue);
        var height = Number(args, "mountingHeight", 3000, 0, room.RoomHeightMm);
        var workHeight = Number(args, "workSurfaceHeight", 800, 0, height);
        if (lux == 0 || workHeight >= height)
        {
            throw new ArgumentException("Освещённость должна быть положительной, рабочая поверхность — ниже светильников.");
        }
        var extension = room.GetExtension<ElRoomExtension>()
                        ?? throw new ArgumentException("У помещения нет электрических данных.");
        var data = extension.LumRoomData;
        var ceiling = Number(args, "ceilingReflectance", data.RoCeiling, 0, 1);
        var walls = Number(args, "wallsReflectance", data.RoWalls, 0, 1);
        var floor = Number(args, "floorReflectance", data.RoSurface, 0, 1);
        var reserve = Number(args, "reserveFactor", data.SafeCoefK, double.Epsilon, double.MaxValue);
        var ugo = _context.SelectUgo(typeof(ElLighting), args.Value<string>("ugoName"), args.Value<string>("ugoFile"));
        var bank = CwPlatform.ResolveNotNull<IObjectBankCollection>();
        var before = bank.GetObjects(typeof(ElLighting), paper).ToHashSet();
        data.LumLl = lux;
        data.LightingDbId = dbId;
        data.CalcMode = ElLumLtCalcMode.BY_LIGHTING;
        data.CalculationType = CalculationType.ByActLux;
        data.LightingCountType = LightingCountType.Recommended;
        data.LightingMountHeightMm = height;
        data.WorkSurfaceHeightMm = workHeight;
        data.RoCeiling = ceiling;
        data.RoWalls = walls;
        data.RoSurface = floor;
        data.SafeCoefK = reserve;
        room.SaveMe();
        try
        {
            // Свежий калькулятор не переносит выбор светильника из предыдущей комнаты через UI-кэш настроек.
            var calculator = new ElLightingCalculator(
                CwPlatform.ResolveNotNull<IWsLightCalculator>(),
                CwPlatform.ResolveNotNull<IUgoBaseComponent>(),
                CwPlatform.ResolveNotNull<IUgoTypes>(),
                CwPlatform.ResolveNotNull<ILightInstaller>(),
                CwPlatform.ResolveNotNull<IWoUgoManager>(),
                CwPlatform.ResolveNotNull<IScaledSelectionService>(),
                CwPlatform.ResolveNotNull<IMessageBox>(),
                CwPlatform.ResolveNotNull<IDialogStack>());
            if (!_operations.Place(calculator, extension, ugo))
            {
                throw new InvalidOperationException("Ки не смог рассчитать/разместить светильники. Проверьте области помещения, фотометрические данные изделия и высоты.");
            }
            var created = bank.GetObjects(typeof(ElLighting), paper).Where(x => !before.Contains(x)).ToArray();
            return new { roomId = room.AcId.ToGuid().ToString(), count = created.Length, lux = data.CalcLux, createdObjectIds = created.Select(x => x.ToGuid().ToString()).ToArray() };
        }
        catch (Exception exception)
        {
            exception.Data["createdObjectIds"] = new JArray(bank.GetObjects(typeof(ElLighting), paper).Where(x => !before.Contains(x)).Select(x => x.ToGuid().ToString()));
            exception.Data["appliedObjectIds"] = new JArray(room.AcId.ToGuid().ToString());
            throw;
        }
    }

    private static double Number(
        JObject args,
        string name,
        double fallback,
        double min,
        double max)
    {
        var value = args.Value<double?>(name) ?? fallback;
        return double.IsFinite(value) && value >= min && value <= max ? value
            : throw new ArgumentException($"{name}: допустимо конечное число от {min} до {max}.");
    }
}
