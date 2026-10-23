using System;
using System.Linq;
using Guard;
using Nanosoft.Acad;
using Nanosoft.CAE.Composer;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Utils;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Tools;

/// <summary>Структура объектов и логические подключения через штатные сервисы продукта.</summary>
internal sealed class McpStructureTools
{
    private readonly McpCadContext _context;

    internal McpStructureTools(McpCadContext context)
    {
        ThrowIf.Argument.IsNull(context, nameof(context));
        _context = context;
    }

    internal void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("objects_structure", "Прямые дети, порты или потребители объекта, без рекурсивной выгрузки проекта.",
            new JObject
            {
                ["id"] = McpSchema.String("ID объекта."),
                ["relation"] = McpSchema.String("children (по умолчанию), ports, slaves или parent."),
                ["offset"] = McpSchema.Integer("По умолчанию 0."),
                ["limit"] = McpSchema.Integer("По умолчанию 20, максимум 100.")
            }, new[] { "id" }, Read, supportsItems: true);
        tools.Add("objects_add_child", "Добавить штатный элемент структуры: например, отходящую группу в шкаф Электрики. Возвращает ID новых прямых детей.",
            new JObject { ["id"] = McpSchema.String("ID родителя; состав добавляемого элемента определяет продукт.") },
            new[] { "id" }, AddChild, changesModel: true, supportsItems: true);
        tools.Add("ports_connect", "Логически подключить потребителей к порту/группе. Не рисует трассы и не подбирает кабели/аппараты.",
            new JObject
            {
                ["portId"] = McpSchema.String("ID порта из objects_structure, не ID шкафа."),
                ["slaveIds"] = McpSchema.Array(McpSchema.String("ID потребителя на плане."))
            }, new[] { "portId", "slaveIds" }, Connect, changesModel: true, supportsItems: true);
    }

    private object Read(JObject args)
    {
        var obj = _context.Object(args.Value<string>("id")!);
        var objects = (args.Value<string>("relation") ?? "children") switch
        {
            "children" => obj.ContentList.AsEnumerable(),
            "ports" => obj is IPortMaster master ? master.PortList.OfType<IAcadObject>() : Enumerable.Empty<IAcadObject>(),
            "slaves" => obj is IPort port ? port.Slaves.Select(x => x.SlaveOwner).OfType<IAcadObject>() : Enumerable.Empty<IAcadObject>(),
            "parent" => obj.ParentObject == null ? Enumerable.Empty<IAcadObject>() : new[] { obj.ParentObject },
            _ => throw new ArgumentException("relation: children, ports, slaves или parent.")
        };
        var found = objects.ToArray();
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 20, 1, 100);
        return new
        {
            total = found.Length, offset,
            objects = found.Skip(offset).Take(limit).Select(x => new
            {
                id = x.AcId.ToGuid().ToString(), type = x.GetType().FullName, title = x.TitleName, isPort = x is IPort
            }).ToArray()
        };
    }

    private object AddChild(JObject args)
    {
        _context.ForgetOpenedObjects();
        var parent = _context.Object(args.Value<string>("id")!);
        var previous = parent.ContentList.Select(x => x.AcId.ToGuid()).ToHashSet();
        CwPlatform.ResolveNotNull<ICaeComposeService>().AddDevice(parent);
        var created = parent.ContentList.Where(x => !previous.Contains(x.AcId.ToGuid()))
            .Select(x => x.AcId.ToGuid().ToString()).ToArray();
        if (created.Length == 0)
        {
            throw new InvalidOperationException("Продукт не добавил элемент структуры к этому объекту.");
        }
        return new { createdObjectIds = created };
    }

    //based on MasterDeviceConnecting.AddReference, без подсветки и ожидания Enter.
    private object Connect(JObject args)
    {
        if (args["slaveIds"] is not JArray ids || ids.Count is < 1 or > 1000)
        {
            throw new ArgumentException("slaveIds: от 1 до 1000 ID потребителей.");
        }
        _context.ForgetOpenedObjects();
        var master = _context.Object(args.Value<string>("portId")!);
        if (master is not IPort port || master is not IAcadMasterSlaveObject masterObject)
        {
            throw new ArgumentException("portId должен указывать на порт/группу из objects_structure.");
        }
        var slaves = ids.Select(x => _context.Object(x.Value<string>()!)).ToArray();
        if (slaves.Any(x => x is not IAcadMasterSlaveObject || x.AcId == master.AcId))
        {
            throw new ArgumentException("Потребители должны поддерживать логическое подключение и отличаться от порта.");
        }
        IMasterDeviceConnectingResolver? resolver = CwPlatform.ResolveNotNull<IMasterDeviceConnecting>().ConnectingResolver;
        var resolved = slaves.Cast<IAcadMasterSlaveObject>().SelectMany(x => resolver == null
            ? new[] { x }.AsEnumerable()
            : resolver.ResolveMasterSlaveObjectOnAdd(masterObject, x)).Where(x => x != null).Distinct().ToList();
        try
        {
            port.AddSlave(resolved);
            master.Net.ModelObjects.SaveMeModifiedObjects();
            return new
            {
                portId = master.AcId.ToGuid().ToString(),
                connectedObjectIds = port.Slaves.Select(x => x.SlaveOwner).OfType<IAcadObject>()
                    .Select(x => x.AcId.ToGuid().ToString()).ToArray()
            };
        }
        catch (Exception exception)
        {
            exception.Data["appliedObjectIds"] = new JArray(slaves.Select(x => x.AcId.ToGuid().ToString()).Prepend(master.AcId.ToGuid().ToString()));
            throw;
        }
    }
}
