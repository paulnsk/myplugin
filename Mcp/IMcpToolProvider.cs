using MyPlugin.Mcp.Protocol;

namespace MyPlugin.Mcp;

/// <summary>Продукт добавляет предметные операции, не создавая зависимость общего MCP от продукта.</summary>
public interface IMcpToolProvider
{
    System.Collections.Generic.IReadOnlyList<IMcpDatabaseBinder> DatabaseBinders { get; }

    void Register(McpToolRegistry tools, McpCadContext context);
}
