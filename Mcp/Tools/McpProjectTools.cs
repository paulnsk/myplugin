using System;
using System.IO;
using System.Linq;
using Guard;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Studio.Solution;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Tools;

internal sealed class McpProjectTools
{
    private readonly McpCadContext _context;
    public McpProjectTools(McpCadContext context)
    {
        ThrowIf.Argument.IsNull(context, nameof(context));
        _context = context;
    }

    public void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("app_context", "Продукт, рабочий набор, проекты и пути DWG. Начни с help_read(name=platform).", new(), Array.Empty<string>(), _ => Describe());
        tools.Add("project_create", "Create a project/workspace in a NEW empty directory. Does not close an existing workspace. projectType comes from app_context.",
            new() { ["directory"] = McpSchema.String("Absolute path to a new or empty directory."), ["projectType"] = McpSchema.String("Exact registered project type name.") },
            new[] { "directory", "projectType" }, Create, changesModel: true);
        tools.Add("project_open", "Open an existing CSW workspace. Close the current workspace first; no implicit replacement.",
            new() { ["path"] = McpSchema.String("Absolute .csw path.") }, new[] { "path" }, Open, changesModel: true);
        tools.Add("project_save", "Save the current workspace and all its project documents.", new(), Array.Empty<string>(), _ =>
        {
            _context.RequireProject();
            global::Nanosoft.Studio.Studio.Instance.Workspace.SaveAll();
            return new { saved = true };
        }, changesModel: true);
        tools.Add("plan_create", "Create and activate one DWG plan in an existing project. Returns its full planPath.",
            new() { ["name"] = McpSchema.String("Plan name without path or extension."), ["projectPath"] = McpSchema.String("Required only for a multi-project workspace.") },
            new[] { "name" }, CreatePlan, changesModel: true);
        tools.Add("plan_activate", "Activate a workspace DWG. Object creation tools also accept planPath explicitly.",
            new() { ["planPath"] = McpSchema.String("Path from app_context/plan_create.") }, new[] { "planPath" }, args =>
        {
            _context.ActivatePlan(args.Value<string>("planPath")!);
            return new { activated = true };
        });
        tools.Add("plan_import", "Подключить существующий DWG к проекту без окон. По умолчанию копирует файл в проект; исходник остаётся нетронутым.",
            new JObject
            {
                ["path"] = McpSchema.String("Полный путь исходного DWG."),
                ["projectPath"] = McpSchema.String("Для рабочего набора с несколькими проектами."),
                ["copy"] = McpSchema.Boolean("Копировать в папку проекта; по умолчанию true."),
                ["modelScale"] = McpSchema.Number("По умолчанию 1: единица DWG — миллиметр."),
                ["paperScale"] = McpSchema.Number("По умолчанию 100.")
            }, new[] { "path" }, ImportPlan, changesModel: true, supportsItems: true);
        tools.Add("view_zoom_all", "Fit the current drawing to its objects.", new(), Array.Empty<string>(), _ =>
        {
            _context.Documents.ZoomAll();
            return new { done = true };
        });
        tools.Add("plan_get_bounds", "Габариты всех объектов пространства модели DWG в мировых координатах (мм): bounds.min/max. Без габаритов поле bounds отсутствует. Не распознаёт стены/здания.",
            new() { ["planPath"] = McpSchema.String("Путь DWG из app_context/plan_import.") },
            new[] { "planPath" }, GetBounds, supportsItems: true);
    }

    private object GetBounds(JObject args)
    {
        _context.ActivatePlan(args.Value<string>("planPath")!);
        // В общей CAD-обёртке нет габаритов DWG. Позднее связывание оставляет зависимость от nanoCAD внутри MCP, без новых ссылок в csproj.
        var application = System.Reflection.Assembly.Load("hostmgd").GetType("HostMgd.ApplicationServices.Application", true)!;
        dynamic documents = application.GetProperty("DocumentManager")!.GetValue(null)!;
        dynamic document = documents.MdiActiveDocument
                           ?? throw new InvalidOperationException("Нет активного документа nanoCAD.");
        dynamic database = document.Database;
        // Обновляем габариты только по явному запросу, а не при каждом чтении контекста проекта.
        database.UpdateExt(true);
        dynamic minPoint = database.Extmin;
        dynamic maxPoint = database.Extmax;
        var min = new[] { (double)minPoint.X, (double)minPoint.Y, (double)minPoint.Z };
        var max = new[] { (double)maxPoint.X, (double)maxPoint.Y, (double)maxPoint.Z };
        var hasBounds = min.Concat(max).All(double.IsFinite) && !min.Where((x, i) => x > max[i]).Any();
        return new { bounds = hasBounds ? new { min, max } : null };
    }

    private object Describe()
    {
        var studio = global::Nanosoft.Studio.Studio.Instance;
        _context.Documents.GetCurrentDwg(out var drawing);
        return new
        {
            projectTypes = studio.PrjTypes.Select(x => new { name = x.Name, title = x.Text }).ToArray(),
            workspacePath = studio.Workspace.IsOpened ? studio.Workspace.Path : null,
            currentDrawing = drawing,
            projects = studio.Workspace.Projects.Select(x => new
            {
                path = x.Path, name = x.Text,
                plans = x.Documents.OfType<IDocument>().Where(d => string.Equals(Path.GetExtension(d.Path), ".dwg", StringComparison.OrdinalIgnoreCase))
                    .Select(d => new { name = d.Text, path = d.Path }).ToArray()
            }).ToArray()
        };
    }

    //based on HierarchyTestProjectService.Create: project type is discovered, not hard-coded to Electrica.
    private object Create(JObject args)
    {
        var studio = global::Nanosoft.Studio.Studio.Instance;
        if (studio.Workspace.IsOpened)
        {
            throw new InvalidOperationException("Close the current workspace manually first; MCP does not discard or replace it.");
        }

        var directory = Path.GetFullPath(args.Value<string>("directory")!);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new ArgumentException("Project directory must be empty: " + directory);
        }

        var type = studio.PrjTypes.SingleOrDefault(x => x.Name == args.Value<string>("projectType"))
                   ?? throw new ArgumentException("Unknown projectType. Call app_context.");
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Use a project subdirectory, not a drive root.");
        }

        var project = studio.Workspace.AddNew(type, Path.Combine(directory, name + ".csp"))
                      ?? throw new InvalidOperationException("Product did not create the project.");
        studio.Workspace.Create();
        studio.Workspace.SaveAs(Path.Combine(directory, name + ".csw"));
        return new { workspacePath = studio.Workspace.Path, projectPath = project.Path, next = "plan_create" };
    }

    private object Open(JObject args)
    {
        var workspace = global::Nanosoft.Studio.Studio.Instance.Workspace;
        if (workspace.IsOpened)
        {
            throw new InvalidOperationException("Close the current workspace manually before opening another.");
        }

        var path = Path.GetFullPath(args.Value<string>("path")!);
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".csw", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An existing .csw file is required.");
        }

        workspace.Open(path);
        if (!workspace.IsOpened)
        {
            throw new InvalidOperationException("Workspace opening was cancelled or failed.");
        }

        return Describe();
    }

    //based on AddExistingPlanCommand.Do, параметры вместо диалогов выбора файла и масштаба.
    private object ImportPlan(JObject args)
    {
        var source = Path.GetFullPath(args.Value<string>("path")!);
        if (!File.Exists(source) || !string.Equals(Path.GetExtension(source), ".dwg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Нужен существующий DWG: " + source);
        }
        var project = _context.Project(args.Value<string>("projectPath"));
        var target = args.Value<bool?>("copy") ?? true
            ? Path.Combine(Path.GetDirectoryName(project.Path)!, Path.GetFileName(source)) : source;
        var modelScale = args.Value<double?>("modelScale") ?? 1;
        var paperScale = args.Value<double?>("paperScale") ?? 100;
        if (!double.IsFinite(modelScale) || modelScale <= 0 || !double.IsFinite(paperScale) || paperScale <= 0)
        {
            throw new ArgumentException("Масштабы должны быть положительными конечными числами.");
        }
        if (project.Documents.OfType<IDocument>().Any(x => string.Equals(x.Path, target, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("План уже подключён: " + target);
        }
        if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, target, false);
        }
        var document = global::Nanosoft.Studio.Studio.Instance.DocTypes[typeof(Plan)].Create() as IDocument
                       ?? throw new InvalidOperationException("Не удалось создать документ плана.");
        document.Open(target);
        project.Children.Add(document);
        if (document.Object is Plan plan)
        {
            plan.AcadScale = modelScale;
            plan.PaperScale = paperScale;
        }
        project.Save();
        return new { planPath = document.Path, name = document.Text };
    }

    private object CreatePlan(JObject args)
    {
        var name = args.Value<string>("name")!;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.HasExtension(name))
        {
            throw new ArgumentException("Plan name must be a filename without an extension.");
        }

        var project = _context.Project(args.Value<string>("projectPath"));
        var expectedPath = Path.Combine(Path.GetDirectoryName(project.Path)!, name + ".dwg");
        if (File.Exists(expectedPath) || project.Documents.OfType<IDocument>().Any(x => x.Text == name))
        {
            throw new ArgumentException("Plan already exists: " + name);
        }

        CaeProjectItemManager.CreatePlan(project, name);
        var plan = project.Documents.OfType<IDocument>().Single(x => x.Text == name);
        _context.ActivatePlan(plan.Path);
        return new { planPath = plan.Path, name = plan.Text };
    }
}
