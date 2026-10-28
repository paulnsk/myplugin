using System;
using System.Linq;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Acad;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using Nanosoft.Piping.Common.Equipment;
using Nanosoft.Piping.Common.Fixture;
using Nanosoft.Piping.Common.Module;
using Nanosoft.Piping.Common.Pipes;
using Nanosoft.WS;
using Newtonsoft.Json.Linq;

namespace MyPlugin.WaterSupply;

internal sealed class WaterSupplyFixtureTools
{
    private readonly McpCadContext _context;

    internal WaterSupplyFixtureTools(McpCadContext context)
    {
        _context = context;
    }

    internal void Register(McpToolRegistry tools)
    {
        tools.Add("watersupply_fixture_insert", "Вставить арматуру из БД проекта в вертикальный участок. Без окна выбора. Инструкция: watersupply-pipes.",
            new JObject
            {
                ["segmentId"] = McpSchema.String("ID TbVerticalPipePart из objects_structure; не ID прибора."),
                ["dbType"] = McpSchema.String("Тип арматуры из database_search, например DbValve."),
                ["dbId"] = McpSchema.Integer("ID изделия в БД проекта."),
                ["height"] = McpSchema.Number("Высота в мм от нуля этажа; без поля — середина выбранного участка.")
            }, new[] { "segmentId", "dbType", "dbId" }, Insert, changesModel: true, supportsItems: true);
    }

    private object Insert(JObject args)
    {
        var segment = _context.Object(args.Value<string>("segmentId")!) as TbVerticalPipePart
                      ?? throw new ArgumentException("segmentId должен указывать на TbVerticalPipePart из структуры вертикального подключения.");
        var parent = segment.ParentObject;
        if (parent is not (TbVerticalPipeJoint or TbStandPipe))
        {
            throw new ArgumentException("Участок не относится к поддерживаемому вертикальному подключению или стояку.");
        }
        var root = parent.MainAcadNodeOwner as AcadVerticalTrace
                   ?? throw new ArgumentException("Не найден владелец вертикальной структуры.");
        var height = args.Value<double?>("height") ?? (segment.HeightDown + segment.HeightUp) / 2;
        if (!double.IsFinite(height) || height < segment.HeightDown || height > segment.HeightUp)
        {
            throw new ArgumentException("height должна находиться в пределах высот выбранного участка.");
        }
        var name = args.Value<string>("dbType");
        var dbType = TbFixtures.VerticalTypesEnums.Select(TbFixtures.GetTypeByEnum).Where(x => x != null)
            .Distinct().SingleOrDefault(x => x!.Name == name || x.FullName == name)
            ?? throw new ArgumentException("dbType не относится к арматуре вертикального подключения.");
        var fixtureType = TbFixtures.GetEnumByType(dbType);
        if (fixtureType is TbFixtureType.Pump or TbFixtureType.WaterGauge or TbFixtureType.Cleanout)
        {
            throw new ArgumentException("Выберите изделие арматуры; насосы, водомеры и прочистки этой командой пока не вставляются.");
        }
        var product = CwPlatform.ResolveNotNull<IDatabaseProxyComponent>().GetDataObject(dbType, args.Value<int>("dbId"))
                      ?? throw new ArgumentException("Изделие не найдено в БД проекта; сначала database_import.");
        var ugo = CwPlatform.ResolveNotNull<IFixtureUgoSettingsService>().GetUgoNameByRealFixtureType(fixtureType)
                  ?? throw new InvalidOperationException("Для этого типа арматуры не настроено УГО.");
        // Та же модельная операция, что у PipeVerticalStructViewModel.AddDevice: без UI выбора изделия и участка.
        var fixture = _context.Net.ObjectFactory.CreateDo<TbVerticalPipeFixture>(root.AcId.paperId);
        var id = fixture.AcId.ToGuid().ToString();
        try
        {
            fixture.FixtureType = fixtureType;
            fixture.DbId = product.Id;
            fixture.SchemeUgoName = ugo;
            fixture.Height = height;
            fixture.UpdateTitleName();
            parent.AddChild(fixture);
            root.UpdateStructure();
            root.Net.ModelObjects.SaveMeModifiedObjects();
            root.UpdateView();
            return new { id };
        }
        catch (Exception exception)
        {
            exception.Data["createdObjectIds"] = new JArray(id);
            throw;
        }
    }
}
