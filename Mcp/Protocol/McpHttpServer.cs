using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Guard;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Protocol;

/// <summary>Stateless Streamable HTTP: JSON responses, no SSE/session storage.</summary>
internal sealed class McpHttpServer : IDisposable
{
    private const int MaximumRequestCharacters = 1024 * 1024;
    private readonly HttpListener _listener = new();
    private readonly McpToolRegistry _tools;
    private readonly McpLog _log;
    private readonly Func<Func<object>, McpRequestTiming, Task<object>> _runInCad;
    private volatile bool _stopped;

    public McpHttpServer(
        int port,
        McpToolRegistry tools,
        McpLog log,
        Func<Func<object>, McpRequestTiming, Task<object>> runInCad)
    {
        ThrowIf.Argument.IsNull(runInCad, nameof(runInCad));
        ThrowIf.Argument.IsNull(log, nameof(log));
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        _tools = tools;
        _log = log;
        _runInCad = runInCad;
        Url = $"http://127.0.0.1:{port}/mcp/";
        _listener.Prefixes.Add(Url);
    }

    public string Url { get; }

    public void Start()
    {
        _listener.Start();
        _log.Write("server.started", new { url = Url });
        _ = ListenAsync();
    }

    private async Task ListenAsync()
    {
        while (!_stopped)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (_stopped && exception is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            // Read-only discovery remains available while a CAD operation is running.
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var timing = new McpRequestTiming();
        try
        {
            var origin = context.Request.Headers["Origin"];
            if (origin != null && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                                   !uri.IsLoopback || uri.Scheme != "http"))
            {
                context.Response.StatusCode = 403;
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                body.Append(buffer, 0, read);
                if (body.Length > MaximumRequestCharacters)
                {
                    context.Response.StatusCode = 413;
                    return;
                }
            }

            timing.Mark("request.read");
            JObject request;
            try
            {
                request = JObject.Parse(body.ToString());
            }
            catch (JsonException)
            {
                await Respond(context.Response, Error(null, -32700, "Invalid JSON object."), timing).ConfigureAwait(false);
                return;
            }

            timing.RequestId = request["id"];
            timing.Method = request["method"]?.Type == JTokenType.String ? (string?)request["method"] : null;
            timing.Mark("request.parse");
            if ((string?)request["jsonrpc"] != "2.0" || request["method"]?.Type != JTokenType.String)
            {
                await Respond(context.Response, Error(request["id"], -32600, "Invalid JSON-RPC request."), timing)
                    .ConfigureAwait(false);
                return;
            }

            if (request["id"] == null)
            {
                context.Response.StatusCode = 202;
                return;
            }

            object response;
            try
            {
                var result = await DispatchAsync(request, timing).ConfigureAwait(false);
                response = new { jsonrpc = "2.0", id = request["id"], result };
            }
            catch (MissingMethodException exception)
            {
                response = Error(request["id"], -32601, exception.Message);
            }
            catch (ArgumentException exception)
            {
                response = Error(request["id"], -32602, exception.Message);
            }
            catch (Exception exception)
            {
                timing.WriteLog(_log, "protocol.error", new { exception = exception.ToString() });
                response = Error(request["id"], -32603, exception.Message);
            }

            timing.Mark("dispatch.complete");
            await Respond(context.Response, response, timing).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!_stopped)
            {
                timing.WriteLog(_log, "http.error", new { exception = exception.ToString() });
            }
        }
        finally
        {
            var statusCode = context.Response.StatusCode;
            try
            {
                context.Response.Close();
            }
            finally
            {
                timing.Mark("response.close");
                timing.Complete(_log, statusCode);
            }
        }
    }

    private async Task<object> DispatchAsync(JObject request, McpRequestTiming timing)
    {
        var parameters = request["params"] as JObject ?? new JObject();
        switch ((string?)request["method"])
        {
            case "initialize":
                var requested = (string?)parameters["protocolVersion"];
                var version = requested is "2025-03-26" or "2025-06-18" or "2025-11-25" ? requested : "2025-11-25";
                return new
                {
                    protocolVersion = version,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "projectstudio-bim", version = "0.1.0" },
                    instructions = "Начни с help_read(name=platform), затем app_context и инструкции продукта из help_list. Ответы инструментов — один JSON в content.text."
                };
            case "ping": return new { };
            case "tools/list": return _tools.List();
            case "tools/call":
                var name = (string?)parameters["name"] ?? throw new ArgumentException("Tool name is required.");
                var tool = _tools.Get(name);
                var arguments = parameters["arguments"] as JObject ?? new JObject();
                var callId = timing.CallId;
                timing.Tool = name;
                var watch = Stopwatch.StartNew();
                timing.WriteLog(_log, "call.started", new { callId, name, arguments });
                timing.Mark("tool.prepare");
                try
                {
                    object Execute()
                    {
                        timing.WriteLog(_log, "call.executing", new { callId, name });
                        try
                        {
                            return tool.Execute(arguments);
                        }
                        finally
                        {
                            timing.Mark("tool.execute");
                        }
                    }

                    var value = tool.UsesCad ? await _runInCad(Execute, timing).ConfigureAwait(false) : Execute();
                    var data = JToken.FromObject(value, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                    timing.Mark("result.materialize");
                    timing.WriteLog(_log, "call.completed",
                        new { callId, name, elapsedMs = watch.ElapsedMilliseconds, result = data });
                    var result = ToolResult(new JObject { ["callId"] = callId, ["value"] = data }, false);
                    timing.Mark("result.envelope");
                    return result;
                }
                catch (Exception exception)
                {
                    while (exception is TargetInvocationException { InnerException: not null })
                    {
                        foreach (System.Collections.DictionaryEntry entry in exception.Data)
                        {
                            exception.InnerException.Data[entry.Key] = entry.Value;
                        }
                        exception = exception.InnerException;
                    }

                    var details = new JObject
                    {
                        ["callId"] = callId, ["message"] = exception.Message,
                        ["createdObjectIds"] = exception.Data["createdObjectIds"] as JArray,
                        ["appliedObjectIds"] = exception.Data["appliedObjectIds"] as JArray,
                        ["completedResults"] = exception.Data["completedResults"] as JArray,
                        ["failedItemIndex"] = exception.Data["failedItemIndex"] is int index ? index : null
                    };
                    foreach (var empty in details.Properties().Where(x => x.Value.Type == JTokenType.Null).ToArray())
                    {
                        empty.Remove();
                    }
                    timing.WriteLog(_log, "call.failed",
                        new
                        {
                            callId, name, elapsedMs = watch.ElapsedMilliseconds, details,
                            exception = exception.ToString()
                        });
                    return ToolResult(details, true);
                }
            default: throw new MissingMethodException("Unsupported MCP method: " + request["method"]);
        }
    }

    private static object ToolResult(JObject data, bool isError)
    {
        return new
        {
            content = new[] { new { type = "text", text = data.ToString(Formatting.None) } },
            isError
        };
    }

    private static object Error(
        JToken? id,
        int code,
        string message)
    {
        return new { jsonrpc = "2.0", id, error = new { code, message } };
    }

    private static async Task Respond(
        HttpListenerResponse response,
        object value,
        McpRequestTiming timing)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
        timing.ResponseBytes = bytes.Length;
        timing.Mark("response.serialize");
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        timing.Mark("response.write");
    }

    public void Dispose()
    {
        _stopped = true;
        _listener.Close();
    }
}
