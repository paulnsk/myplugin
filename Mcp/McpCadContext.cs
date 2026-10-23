using System;
using System.IO;
using System.Linq;
using Guard;
using Nanosoft.Acad;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.Acad.Net;
using Nanosoft.Platform.FormExtension;
using Nanosoft.Studio.Solution;
using Nanosoft.Utils;

namespace MyPlugin.Mcp;

public sealed class McpCadContext
{
    internal System.Collections.Generic.IReadOnlyList<IMcpDatabaseBinder> DatabaseBinders { get; }

    public McpCadContext(System.Collections.Generic.IReadOnlyList<IMcpDatabaseBinder> databaseBinders)
    {
        ThrowIf.Argument.IsNull(databaseBinders, nameof(databaseBinders));
        DatabaseBinders = databaseBinders;
    }

    private IAcadNet? _net;
    internal double NetLoadMilliseconds { get; private set; }

    public void ForgetOpenedObjects()
    {
        _net = null;
    }

    // Свежая сеть на вызов, но без загрузки всего проекта: объекты открываются адресно.
    public void BeginCall()
    {
        NetLoadMilliseconds = 0;
    }

    public IAcadNet Net
    {
        get
        {
            if (_net != null)
            {
                return _net;
            }

            RequireProject();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _net = CwPlatform.ResolveNotNull<IAcadNetFactory>().NewNet();
            }
            finally
            {
                NetLoadMilliseconds += watch.Elapsed.TotalMilliseconds;
            }
            return _net;
        }
    }

    public IWoObjectFactory Factory => CwPlatform.ResolveNotNull<IWoObjectFactory>();

    public WoUgoData SelectUgo(
        Type type,
        string? name = null,
        string? file = null)
    {
        ThrowIf.Argument.IsNull(type, nameof(type));
        return Tools.McpUgoCatalog.Select(type, name, file);
    }
    public IEngProjectService Projects => CwPlatform.ResolveNotNull<IEngProjectService>();
    public ICwCadDocService Documents => CwPlatform.ResolveNotNull<ICwCadDocService>();

    public void EnsureNoModalDialog()
    {
        var dialogs = CwPlatform.ResolveNotNull<IDialogStack>();
        var current = dialogs.GetCurrent();
        if (current != IntPtr.Zero && current != dialogs.GetMainFormApp())
        {
            throw new InvalidOperationException("A BIM dialog is open. Close it before using MCP.");
        }
    }

    public void RequireProject()
    {
        if (!global::Nanosoft.Studio.Studio.Instance.Workspace.IsOpened)
        {
            throw new InvalidOperationException("No project is open. Use project_create or project_open.");
        }
    }

    public IProject Project(string? projectPath)
    {
        RequireProject();
        var projects = global::Nanosoft.Studio.Studio.Instance.Workspace.Projects.ToArray();
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            if (projects.Length != 1)
            {
                throw new ArgumentException("Supply projectPath: the workspace contains multiple projects.");
            }

            return projects[0];
        }

        return projects.SingleOrDefault(x =>
                   string.Equals(x.Path, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException("Project is not in the open workspace: " + projectPath);
    }

    public void ActivatePlan(string planPath)
    {
        ThrowIf.Argument.IsNull(planPath, nameof(planPath));
        RequireProject();
        var fullPath = Path.GetFullPath(planPath);
        var found = global::Nanosoft.Studio.Studio.Instance.Workspace.Projects.SelectMany(x => x.Documents.OfType<IDocument>())
            .Any(x => string.Equals(x.Path, fullPath, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(Path.GetExtension(x.Path), ".dwg", StringComparison.OrdinalIgnoreCase));
        if (!found)
        {
            throw new ArgumentException("planPath must identify a DWG in the open workspace.");
        }

        Projects.ShowPaper(fullPath);
    }

    public IAcadObject Object(string id)
    {
        ThrowIf.Argument.IsNull(id, nameof(id));
        var globalId = GlobalId.FromGuid(Guid.Parse(id));
        return Net.OpenObject(globalId) ?? throw new ArgumentException("Object not found in this project: " + id);
    }
}
