using System;
using Nanosoft.Acad;
using Nanosoft.Acad.Net;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using Nanosoft.CAE.Evos.Ui.TreeLog.Interfaces;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy;
using Nanosoft.Platform.FormExtension;
using Nanosoft.Platform.PlatformIfaces;
using Nanosoft.Studio;
using Nanosoft.Studio.Utils;
using Nanosoft.Utils;

namespace MyPlugin.HierarchyTestCommands;

// BIM-контейнер предоставляет только продуктовые сервисы; объекты стенда создаёт сам плагин.
internal sealed class HierarchyTestServices
{
    private readonly HierarchyTestProjectService _projects;
    private readonly ElectricaHierarchyTestObjectBuilder _objects;
    private readonly HierarchyTestScenePainter _painter;
    private readonly ICwCadDocService _documents;

    internal HierarchyTestServices()
    {
        _documents = CwPlatform.ResolveNotNull<ICwCadDocService>();
        _projects = new HierarchyTestProjectService(CwPlatform.ResolveNotNull<IAcadNetFactory>(), CwPlatform.ResolveNotNull<IEngProjectService>());
        var factory = CwPlatform.ResolveNotNull<IWoObjectFactory>();
        var types = CwPlatform.ResolveNotNull<IUgoTypes>();
        var ugo = CwPlatform.ResolveNotNull<IUgoBaseComponent>();
        _objects = new ElectricaHierarchyTestObjectBuilder(factory, types, ugo,
            CwPlatform.ResolveNotNull<IDatabaseProxyComponent>(), CwPlatform.ResolveNotNull<IAcadTraceManager>());
        _painter = new HierarchyTestScenePainter(factory, types, ugo, CwPlatform.ResolveNotNull<ICwCadDrawManager>(), _objects);
    }

    internal void Generate(bool withError)
    {
        if (CwPlatform.ResolveNotNull<IApplicationInformation>().ApplicationType != ApplicationTypeEnum.Electrica)
        {
            throw new InvalidOperationException("Тестовый проект иерархии создаётся в Электрике.");
        }

        var highlight = CwPlatform.ResolveNotNull<IAcadHighlightManager>();
        CwHierarchy3DTestGenerateCommand command = withError
            ? new CwHierarchyTestGenerateSampleErrorCommand(_projects, _objects, _painter, _documents, highlight)
            : new CwHierarchy3DTestGenerateCommand(_projects, _objects, _painter, _documents, highlight);
        command.Invoke();
    }

    internal void GenerateRotations()
    {
        if (CwPlatform.ResolveNotNull<IApplicationInformation>().ApplicationType != ApplicationTypeEnum.Electrica)
        {
            throw new InvalidOperationException("Демонстрация поворотов создаётся в Электрике.");
        }
        var net = _projects.Create();
        if (net == null) { return; }
        _objects.ResetForProject();
        var scene = HierarchyRotationScene.Create();
        _painter.Draw(net, scene, "План 1");
        _documents.ZoomAll();
        var secondPlan = _projects.AddAndActivatePlan("План 2");
        _painter.Draw(secondPlan, scene, "План 2");
        _documents.ZoomAll();
        _projects.ActivatePlan("План 1");
        global::Nanosoft.Studio.Studio.Instance.Workspace.SaveAll();
        CwPlatform.ResolveNotNull<IAcadHighlightManager>().AcadConsolePrint(
            "\nПлан 1: ромашка из 12 двухэтажных зданий. План 2: башня из 10 этажей (0–90°). Выгрузка — штатной командой 3D по иерархии.");
    }

    internal void Export(bool all)
    {
        var service = new HierarchyTestExportService(CwPlatform.ResolveNotNull<IHierarchy3DGenerationService>(),
            _documents, _projects, CwPlatform.ResolveNotNull<IDialogStackMessageBox>(), CwPlatform.ResolveNotNull<IDialogStack>());
        if (all)
        {
            service.ExportAll();
        }
        else
        {
            service.Export();
        }
    }

    internal void Validate()
    {
        new HierarchyTestValidationService(CwPlatform.ResolveNotNull<IAcadNetFactory>(), _documents,
            CwPlatform.ResolveNotNull<IDialogStackMessageBox>(), CwPlatform.ResolveNotNull<IOutputLogService>()).Validate();
    }
}
