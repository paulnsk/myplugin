using System.IO;
using Guard;
using Nanosoft.Acad.CadCommand;
using Nanosoft.Acad.Interop.NativeComponent;
using Newtonsoft.Json;

namespace MyPlugin.HierarchyTestCommands;

//based on CwHierarchy3DTestGenerateCommand из внешнего тестового стенда.
public class CwHierarchy3DTestGenerateCommand : ICadCommand
{
    private readonly HierarchyTestProjectService _projects;
    private readonly ElectricaHierarchyTestObjectBuilder _objects;
    private readonly HierarchyTestScenePainter _painter;
    private readonly ICwCadDocService _documents;
    private readonly IAcadHighlightManager _highlightManager;

    public CwHierarchy3DTestGenerateCommand(
        HierarchyTestProjectService projects,
        ElectricaHierarchyTestObjectBuilder objects,
        HierarchyTestScenePainter painter,
        ICwCadDocService documents,
        IAcadHighlightManager highlightManager)
    {
        ThrowIf.Argument.IsNull(projects, nameof(projects));
        ThrowIf.Argument.IsNull(objects, nameof(objects));
        ThrowIf.Argument.IsNull(painter, nameof(painter));
        ThrowIf.Argument.IsNull(documents, nameof(documents));
        ThrowIf.Argument.IsNull(highlightManager, nameof(highlightManager));
        _projects = projects;
        _objects = objects;
        _painter = painter;
        _documents = documents;
        _highlightManager = highlightManager;
    }

    public string Group => HierarchyTestConstants.CommandGroup;
    public virtual string Name => "CW_HIERARCHY_TEST_GENERATE_SAMPLE";
    public CadCommandTypeEnum Type => CadCommandTypeEnum.CmdSession;

    protected virtual bool WithError => false;

    public void Invoke()
    {
        var net = _projects.Create();
        if (net == null)
        {
            return;
        }

        _objects.ResetForProject();
        var scene = WithError ? HierarchyTestScene.CreateError() : HierarchyTestScene.Create();
        _painter.Draw(net, scene, "План 1");
        var secondPlan = _projects.AddAndActivatePlan("План 2");
        _painter.Draw(secondPlan, scene, "План 2");
        _documents.ZoomAll();
        _projects.ActivatePlan("План 1");
        _documents.ZoomAll();
        var workspace = global::Nanosoft.Studio.Studio.Instance.Workspace;
        workspace.SaveAll();
        var descriptionPath = Path.Combine(Path.GetDirectoryName(workspace.Path)!, "hierarchy-test-scene.json");
        File.WriteAllText(descriptionPath, JsonConvert.SerializeObject(scene, Formatting.Indented));
        _highlightManager.AcadConsolePrint(
            $"\nТестовый проект: {workspace.Path}\n{(WithError ? "Пример ошибки билдера: совпадающие контуры." : "Семь тестов на Плане 1; повернутая копия подзоны на Плане 2.")} Паспорт: {descriptionPath}");
    }
}

public sealed class CwHierarchyTestGenerateSampleErrorCommand : CwHierarchy3DTestGenerateCommand
{
#pragma warning disable CW1001 // Параметры проверяет базовый конструктор.
    public CwHierarchyTestGenerateSampleErrorCommand(
        HierarchyTestProjectService projects,
        ElectricaHierarchyTestObjectBuilder objects,
        HierarchyTestScenePainter painter,
        ICwCadDocService documents,
        IAcadHighlightManager highlightManager)
        : base(projects, objects, painter, documents, highlightManager) { }
#pragma warning restore CW1001

    public override string Name => "CW_HIERARCHY_TEST_GENERATE_SAMPLE_ERROR";
    protected override bool WithError => true;
}
