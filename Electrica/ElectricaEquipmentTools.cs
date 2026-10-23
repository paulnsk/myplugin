using Guard;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;
using Nanosoft.CAE.Model.BoM;
using Nanosoft.ObjectLib.ESN.Db.DbImpl;
using Nanosoft.ObjectLib.WireCommon.Db.DbWires;
using Nanosoft.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace MyPlugin.Electrica;

internal sealed class ElectricaEquipmentTools
{
    private readonly McpCadContext _context;

    internal ElectricaEquipmentTools(McpCadContext context)
    {
        ThrowIf.Argument.IsNull(context, nameof(context));
        _context = context;
    }

    internal void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("electrica_group_equipment_add", "Добавить аппарат из БД проекта в электрическую группу без окна выбора. Не заменяет существующий. Инструкция: electrica-network.",
            new JObject
            {
                ["groupId"] = McpSchema.String("ID ElFider, не ID шкафа."),
                ["dbType"] = McpSchema.String("Тип аппарата из database_search, например DbElAutomat."),
                ["dbId"] = McpSchema.Integer("ID изделия в БД проекта.")
            }, new[] { "groupId", "dbType", "dbId" }, AddEquipment, changesModel: true, supportsItems: true);
        tools.Add("electrica_supply_get", "Прочитать трансформатор и питающие кабели шкафа; компактная карточка. Инструкция: electrica-network.",
            new JObject { ["boardId"] = McpSchema.String("ID шкафа.") }, new[] { "boardId" }, ReadSupply, supportsItems: true);
        var cable = new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject
            {
                ["dbId"] = McpSchema.Integer("ElWire из БД проекта."),
                ["lengthMeters"] = McpSchema.Number("Длина, м, не мм."),
                ["count"] = McpSchema.Integer("Параллельных кабелей, по умолчанию 1.")
            },
            ["required"] = new JArray("dbId", "lengthMeters")
        };
        var cables = McpSchema.Array(cable);
        cables["minItems"] = 0;
        cables["maxItems"] = 100;
        tools.Add("electrica_supply_update",
            "Задать трансформатор/питающие кабели шкафа. Переданный список заменяет прежний; не переданные поля сохраняются. Инструкция: electrica-network.",
            new JObject
            {
                ["boardId"] = McpSchema.String("ID шкафа."),
                ["transformerDbId"] = McpSchema.Integer("DbElTransformer из БД проекта."),
                ["cables"] = cables
            }, new[] { "boardId" }, UpdateSupply, changesModel: true, supportsItems: true);
    }

    private object AddEquipment(JObject args)
    {
        var group = _context.Object(args.Value<string>("groupId")!) as ElFider
                    ?? throw new ArgumentException("groupId должен указывать на электрическую группу.");
        var name = args.Value<string>("dbType");
        var type = DbElFiderKitItem.FillingTypes.SingleOrDefault(x => x.Name == name || x.FullName == name)
                   ?? throw new ArgumentException("dbType не относится к аппаратам комплектации электрической группы.");
        var record = CwPlatform.ResolveNotNull<IDatabaseProxyComponent>().GetDataObject(type, args.Value<int>("dbId"))
                     ?? throw new ArgumentException("Аппарат не найден в БД проекта; сначала database_import.");
        // Тот же mapper и сервис, что у штатного добавления, но изделие уже выбрано агентом, окно не нужно.
        var objectType = CwPlatform.ResolveNotNull<IDbDataToObjectTypeDictionary>().ObjectType(record.Object);
        if (!typeof(ElFiderGadget).IsAssignableFrom(objectType))
        {
            throw new InvalidOperationException("Для изделия не найден тип аппарата группы.");
        }
        var gadget = CwPlatform.ResolveNotNull<IFiderFillingService>().AddFiderFilling(objectType, record.Id, group, false)
                     ?? throw new InvalidOperationException("Штатный сервис не создал аппарат.");
        group.Net.ModelObjects.SaveMeModifiedObjects();
        return new { createdObjectIds = new[] { gadget.AcId.ToGuid().ToString() } };
    }

    private object ReadSupply(JObject args)
    {
        var board = Board(args);
        return new
        {
            boardId = board.AcId.ToGuid().ToString(), transformerDbId = board.UsingTransformer?.DbId,
            cables = board.WiresToTransformer.Take(100).Select(x => new { dbId = x.UsingWireDbId, lengthMeters = x.Length, count = x.Count }).ToArray(),
            hasMore = board.WiresToTransformer.Count > 100
        };
    }

    private object UpdateSupply(JObject args)
    {
        var board = Board(args);
        var db = CwPlatform.ResolveNotNull<IDatabaseProxyComponent>();
        var transformerId = args.Value<int?>("transformerDbId");
        if (transformerId != null && db.GetDataObject(typeof(DbElTransformer), transformerId.Value)?.Object is not DbElTransformer)
        {
            throw new ArgumentException("Трансформатор не найден в БД проекта; сначала database_import.");
        }
        if (args["cables"] != null && (args["cables"] is not JArray array || array.Count > 100 || array.Any(x => x is not JObject)))
        {
            throw new ArgumentException("cables должен быть массивом из 0–100 объектов; [] очищает список.");
        }
        var cables = (args["cables"] as JArray)?.Cast<JObject>().Select(x =>
        {
            if (x["dbId"] == null || x["lengthMeters"] == null || x.Properties().Any(p => p.Name is not ("dbId" or "lengthMeters" or "count")))
            {
                throw new ArgumentException("Для кабеля нужны dbId и lengthMeters; необязательное поле — count.");
            }
            var id = x.Value<int>("dbId");
            var length = x.Value<double>("lengthMeters");
            var count = x.Value<int?>("count") ?? 1;
            if (!double.IsFinite(length) || length < 0 || count < 1)
            {
                throw new ArgumentException("lengthMeters >= 0, count >= 1.");
            }
            if (db.GetDataObject(typeof(ElWire), id)?.Object is not ElWire)
            {
                throw new ArgumentException("Питающий кабель не найден в БД проекта; сначала database_import.");
            }
            return new ElWireWrapper { UsingWireDbId = id, Length = length, Count = count };
        }).ToList();
        if (transformerId == null && cables == null)
        {
            throw new ArgumentException("Передайте transformerDbId и/или cables.");
        }
        if (transformerId != null)
        {
            board.TransformerDbId = transformerId.Value;
        }
        if (cables != null)
        {
            board.WiresToTransformer = cables;
        }
        board.SaveMe();
        return new { boardId = board.AcId.ToGuid().ToString() };
    }

    private ElBaseBoard Board(JObject args)
    {
        return _context.Object(args.Value<string>("boardId")!) as ElBaseBoard
               ?? throw new ArgumentException("boardId должен указывать на электрический шкаф.");
    }
}
