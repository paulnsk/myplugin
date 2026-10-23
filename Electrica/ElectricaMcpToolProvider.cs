using Guard;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Utils;

namespace MyPlugin.Electrica;

/// <summary>Продуктовые действия подключаются к общему MCP, не раскрывая ему типы электрики.</summary>
public sealed class ElectricaMcpToolProvider : IMcpToolProvider
{
    public System.Collections.Generic.IReadOnlyList<IMcpDatabaseBinder> DatabaseBinders { get; } =
        new IMcpDatabaseBinder[] { new ElectricaDatabaseBinder() };

    public void Register(McpToolRegistry tools, McpCadContext context)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        ThrowIf.Argument.IsNull(context, nameof(context));
        var operations = new ElectricaPrivateOperations();
        new ElectricaLightingTools(context, operations).Register(tools);
        new ElectricaNetworkTools(context, operations).Register(tools);
        new ElectricaEquipmentTools(context).Register(tools);
    }
}
