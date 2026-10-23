using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Guard;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.Utils;

namespace MyPlugin.Mcp;

/// <summary>Выполняет инструменты в потоке интерфейса CAD через WPF Dispatcher.</summary>
internal sealed class McpCadDispatcher
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly McpCadContext _context;
    private bool _stopped;

    internal McpCadDispatcher(McpCadContext context)
    {
        ThrowIf.Argument.IsNull(context, nameof(context));
        _context = context;
    }

    internal bool IsExecuting { get; private set; }

    internal async Task<object> InvokeAsync(Func<object> operation, Protocol.McpRequestTiming timing)
    {
        ThrowIf.Argument.IsNull(operation, nameof(operation));
        ThrowIf.Argument.IsNull(timing, nameof(timing));
        // Операция WPF и её Task не null: при остановке Dispatcher задача отменяется.
        var pending = _dispatcher.InvokeAsync(() => Execute(operation, timing), DispatcherPriority.Background)!;
        return await pending.Task!.ConfigureAwait(false);
    }

    private object Execute(Func<object> operation, Protocol.McpRequestTiming timing)
    {
        timing.Mark("cad.queue");
        if (_stopped)
        {
            throw new InvalidOperationException("This MCP session has stopped. Its queued calls will not execute.");
        }
        var documents = CwPlatform.ResolveNotNull<ICwCadDocService>();
        var checker = CwPlatform.ResolveNotNull<Nanosoft.Platform.PlatformIfaces.ICadCommandChecker>();
        if (IsExecuting || !documents.IsQuiescentState() || checker.CheckIsCommandStarted(showWarning: false))
        {
            throw new InvalidOperationException("CAD is busy. Finish the active command/dialog before calling again.");
        }
        _context.EnsureNoModalDialog();
        timing.Mark("cad.checks");
        IsExecuting = true;
        try
        {
            // MCP-вызов не запускает общую статистику CAD-команд с принудительным GC.
            checker.StartCommand();
            try
            {
                var projects = _context.Projects;
                if (projects.IsProjectOpened())
                {
                    projects.LockProjectDocs();
                }
                try
                {
                    timing.Mark("cad.commandEnter");
                    _context.BeginCall();
                    return operation();
                }
                finally
                {
                    timing.NetLoadMilliseconds = _context.NetLoadMilliseconds;
                    projects.UnlockProjectDocs();
                }
            }
            finally
            {
                checker.EndCommand();
            }
        }
        finally
        {
            _context.ForgetOpenedObjects();
            IsExecuting = false;
            timing.Mark("cad.commandExit");
        }
    }

    internal void Stop()
    {
        if (IsExecuting)
        {
            throw new InvalidOperationException("Wait for the current MCP operation to finish.");
        }

        _stopped = true;
    }
}
