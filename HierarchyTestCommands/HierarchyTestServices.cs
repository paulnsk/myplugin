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
