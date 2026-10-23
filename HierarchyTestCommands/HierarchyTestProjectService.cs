using System;
using System.IO;
using System.Linq;
using Guard;
using Nanosoft.Acad;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.Acad.Net;
using Nanosoft.Studio;
using Nanosoft.Studio.Solution;
using Nanosoft.Utils;

namespace MyPlugin.HierarchyTestCommands;

//based on HierarchyTestProjectService из внешнего тестового стенда.
public sealed class HierarchyTestProjectService
{
    private readonly IAcadNetFactory _netFactory;
    private readonly IEngProjectService _engProjectService;

    public HierarchyTestProjectService(IAcadNetFactory netFactory, IEngProjectService engProjectService)
    {
        ThrowIf.Argument.IsNull(netFactory, nameof(netFactory));
        ThrowIf.Argument.IsNull(engProjectService, nameof(engProjectService));
        _netFactory = netFactory;
        _engProjectService = engProjectService;
    }

    internal IAcadNet? Create()
    {
        var studio = global::Nanosoft.Studio.Studio.Instance;
        var workspace = studio.Workspace;
        ThrowIf.Variable.IsNull(workspace, nameof(workspace));
        if (workspace.IsOpened)
        {
            workspace.Close();
            if (workspace.IsOpened)
            {
                return null;
            }
        }

        var projectType = studio.PrjTypes.FirstOrDefault(x => x.Name == ProjectTypes.Electrica)
            ?? throw new InvalidOperationException("Тип проекта Электрика не найден.");
        var projectName = Guid.NewGuid().ToString();
        var directoryPath = Path.Combine(HierarchyTestConstants.RootDirectory, projectName);
        Directory.CreateDirectory(directoryPath);
        var projectPath = Path.Combine(directoryPath, projectName + ".csp");
        workspace.AddNew(projectType, projectPath);
        workspace.Create();
        workspace.SaveAs(Path.Combine(directoryPath, projectName + ".csw"));

        return AddAndActivatePlan(HierarchyTestConstants.FirstPlanName);
    }

    internal IAcadNet AddAndActivatePlan(string planName)
    {
        ThrowIf.Argument.IsNull(planName, nameof(planName));
        var project = global::Nanosoft.Studio.Studio.Instance.Workspace.Projects.Single();
        CaeProjectItemManager.CreatePlan(project, planName);
        ActivatePlan(planName);
        var net = _netFactory.NewNet();
        net.Load();
        return net;
    }

    internal void ActivatePlan(string planName)
    {
        ThrowIf.Argument.IsNull(planName, nameof(planName));
        var project = global::Nanosoft.Studio.Studio.Instance.Workspace.Projects.Single();
        var document = project.Documents.OfType<IDocument>()
            .FirstOrDefault(x => x.Text == planName)
            ?? throw new InvalidOperationException($"Не удалось найти план {planName}.");
        _engProjectService.ShowPaper(document.Path);
    }
}
