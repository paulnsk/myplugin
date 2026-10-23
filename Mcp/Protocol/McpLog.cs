using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace MyPlugin.Mcp.Protocol;

internal sealed class McpLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private bool _disabled;

    public McpLog(string directory, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"mcp-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            { AutoFlush = true };
    }

    public string? FilePath { get; }
    public string? LastError { get; private set; }

    public void Write(string stage, object? details)
    {
        if (_writer == null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                var serialized = JsonConvert.SerializeObject(details);
                var bounded = serialized.Length > 32768
                    ? new { truncated = true, preview = serialized.Substring(0, 32768) }
                    : details;
                _writer.WriteLine(JsonConvert.SerializeObject(new
                    { time = DateTimeOffset.Now, stage, details = bounded }));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                  or UnauthorizedAccessException)
            {
                // A full disk must not turn a successful CAD operation into a failed/retried mutation.
                LastError = exception.Message;
                _disabled = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disabled = true;
            _writer?.Dispose();
        }
    }
}