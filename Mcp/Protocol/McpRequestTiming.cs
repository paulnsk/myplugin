using System.Collections.Generic;
using System.Diagnostics;
using Guard;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Protocol;

/// <summary>Один замер на HTTP-запрос; последовательные этапы и вложенные расходы.</summary>
internal sealed class McpRequestTiming
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, double> _stages = new();
    private double _previousMilliseconds;
    private double _loggingMilliseconds;

    internal string CallId { get; } = System.Guid.NewGuid().ToString("N");
    internal JToken? RequestId { get; set; }
    internal string? Method { get; set; }
    internal string? Tool { get; set; }
    internal long ResponseBytes { get; set; }
    internal double NetLoadMilliseconds { get; set; }

    internal void Mark(string stage)
    {
        ThrowIf.Argument.IsNull(stage, nameof(stage));
        var now = _clock.Elapsed.TotalMilliseconds;
        _stages.Add(stage, now - _previousMilliseconds);
        _previousMilliseconds = now;
    }

    internal void WriteLog(
        McpLog log,
        string stage,
        object details)
    {
        ThrowIf.Argument.IsNull(log, nameof(log));
        ThrowIf.Argument.IsNull(stage, nameof(stage));
        ThrowIf.Argument.IsNull(details, nameof(details));
        var started = _clock.Elapsed.TotalMilliseconds;
        try
        {
            log.Write(stage, details);
        }
        finally
        {
            _loggingMilliseconds += _clock.Elapsed.TotalMilliseconds - started;
        }
    }

    internal void Complete(McpLog log, int statusCode)
    {
        ThrowIf.Argument.IsNull(log, nameof(log));
        // Эта последняя строка пишется после закрытия ответа и не входит в elapsedMs/logMs.
        log.Write("request.timing", new
        {
            callId = CallId, requestId = RequestId, method = Method, tool = Tool,
            statusCode, elapsedMs = _clock.Elapsed.TotalMilliseconds, responseBytes = ResponseBytes,
            stagesMs = _stages, netLoadMs = NetLoadMilliseconds, logMs = _loggingMilliseconds
        });
    }
}
