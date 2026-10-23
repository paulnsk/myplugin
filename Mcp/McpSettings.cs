using System;
using System.IO;
using Newtonsoft.Json;

namespace MyPlugin.Mcp;

internal sealed class McpSettings
{
    public string RuntimeDirectory { get; set; } = "";
    public int Port { get; set; }
    public bool Logging { get; set; }

    internal static McpSettings Load()
    {
        var path = Path.Combine(McpRuntime.DirectoryPath, "settings.json");
        var settings = JsonConvert.DeserializeObject<McpSettings>(File.ReadAllText(path),
                           new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error })
                       ?? throw new InvalidOperationException("Пустой конфиг MCP: " + path);
        if (settings.Port is < 1024 or > 65535)
        {
            throw new ArgumentException("port в settings.json должен быть от1024 до65535.");
        }
        if (string.IsNullOrWhiteSpace(settings.RuntimeDirectory) || !Path.IsPathFullyQualified(settings.RuntimeDirectory))
        {
            throw new ArgumentException("runtimeDirectory в settings.json должен быть абсолютным путём.");
        }
        return settings;
    }
}
