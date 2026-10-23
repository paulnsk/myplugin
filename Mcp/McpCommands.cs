using System;
using System.Linq;
using System.IO;
using Nanosoft.Acad.CadCommand;
using Nanosoft.Acad.Interop.NativeComponent;
using MyPlugin.Mcp.Protocol;
using MyPlugin.Mcp.Tools;
using Nanosoft.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp;

public sealed class McpStartCommand : ICadCommand
{
    public string Group => "ProjectStudioMcp";
    public string Name => "CW_MCP_START";
    public CadCommandTypeEnum Type => CadCommandTypeEnum.CmdSession;

    public void Invoke()
    {
        McpRuntime.Start();
    }
}

public sealed class McpStopCommand : ICadCommand
{
    public string Group => "ProjectStudioMcp";
    public string Name => "CW_MCP_STOP";
    public CadCommandTypeEnum Type => CadCommandTypeEnum.CmdSession;

    public void Invoke()
    {
        McpRuntime.Stop();
    }
}

/// <summary>The server exists only between explicit START and STOP commands.</summary>
internal static class McpRuntime
{
    private static McpHttpServer? _server;
    private static McpLog? _log;
    private static McpCadDispatcher? _cad;
    internal static readonly string DirectoryPath = Path.Combine(
        Path.GetDirectoryName(typeof(McpRuntime).Assembly.Location)!, "Mcp");

    public static void Start()
    {
        if (_server != null)
        {
            Print("MCP already running: " + _server.Url);
            return;
        }

        var settings = McpSettings.Load();
        var runtimeDirectory = settings.RuntimeDirectory;
        Directory.CreateDirectory(runtimeDirectory);
        var log = new McpLog(runtimeDirectory, settings.Logging);
        try
        {
            var providers = McpProductModules.Load();
            var context = new McpCadContext(providers.SelectMany(x => x.DatabaseBinders).ToArray());
            var cad = new McpCadDispatcher(context);
            var tools = new McpToolRegistry();
            new McpProjectTools(context).Register(tools);
            var types = new McpTypeCatalog(context);
            types.Register(tools);
            new McpDatabaseTools(context, types).Register(tools);
            new McpObjectTools(context, types).Register(tools);
            new McpUgoCatalog(types).Register(tools);
            new McpStructureTools(context).Register(tools);
            foreach (var provider in providers)
            {
                provider.Register(tools, context);
            }
            McpHelpTools.Register(tools);
            tools.Add("server_status", "Server endpoint and diagnostic log; available even while CAD is busy.",
                new JObject(), Array.Empty<string>(),
                _ => new
                {
                    endpoint = _server?.Url, logPath = _log?.FilePath, logError = _log?.LastError, executing = cad.IsExecuting,
                    processId = Environment.ProcessId
                }, usesCad: false);

            var server = new McpHttpServer(settings.Port, tools, log, cad.InvokeAsync);
            try
            {
                server.Start();
                var connectionPath = Path.Combine(runtimeDirectory, $"connection-{Environment.ProcessId}.json");
                File.WriteAllText(connectionPath, JsonConvert.SerializeObject(new
                {
                    url = server.Url,
                    logPath = log.FilePath,
                    processId = Environment.ProcessId
                }, Formatting.Indented));
                _server = server;
                _log = log;
                _cad = cad;
                Print($"MCP: {server.Url}\nConnection settings: {connectionPath}\nLog: {log.FilePath ?? "disabled"}");
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    public static void Stop()
    {
        _cad?.Stop();
        _server?.Dispose();
        _server = null;
        _cad = null;
        _log?.Dispose();
        _log = null;
        Print("MCP stopped.");
    }

    private static void Print(string message)
    {
        CwPlatform.ResolveNotNull<IAcadHighlightManager>().AcadConsolePrint("\n" + message);
    }
}
