using System;
using System.Collections.Generic;
using Guard;
using MyPlugin.Mcp;
using MyPlugin.Mcp.Protocol;

namespace MyPlugin.WaterSupply;

public sealed class WaterSupplyMcpToolProvider : IMcpToolProvider
{
    public IReadOnlyList<IMcpDatabaseBinder> DatabaseBinders { get; } = Array.Empty<IMcpDatabaseBinder>();

    public void Register(McpToolRegistry tools, McpCadContext context)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        ThrowIf.Argument.IsNull(context, nameof(context));
        new WaterSupplySettingsTools(context).Register(tools);
        new WaterSupplyNetworkTools(context).Register(tools);
        new WaterSupplyStandTools(context).Register(tools);
        new WaterSupplyFixtureTools(context).Register(tools);
    }
}
