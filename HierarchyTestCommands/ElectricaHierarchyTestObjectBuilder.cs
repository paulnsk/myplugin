using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Guard;
using Nanosoft.Acad;
using Nanosoft.Acad.SlopeTraceKit;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using Nanosoft.Data;
using Nanosoft.Utils;
using Nanosoft.Utils.Point3d;

namespace MyPlugin.HierarchyTestCommands;

//based on ElectricaHierarchyTestObjectBuilder из внешнего тестового стенда.
public sealed class ElectricaHierarchyTestObjectBuilder
{
    private readonly IWoObjectFactory _objectFactory;
    private readonly IUgoTypes _ugoTypes;
    private readonly IUgoBaseComponent _ugoBaseComponent;
    private readonly IDatabaseProxyComponent _databaseProxyComponent;
    private readonly IAcadTraceManager _traceManager;

    private Type? _lightingType;
    private Type? _traceType;
    private MethodInfo? _createTraceMethod;
    private List<int>? _luminaireDbIds;
    private int _luminaireIndex;
    private int? _compactLuminaireDbId;
    private readonly Dictionary<double, int> _outlineConfigurationsByDiameter = new();
    private Type? _outlineCanalType;

    public ElectricaHierarchyTestObjectBuilder(
        IWoObjectFactory objectFactory,
        IUgoTypes ugoTypes,
        IUgoBaseComponent ugoBaseComponent,
        IDatabaseProxyComponent databaseProxyComponent,
        IAcadTraceManager traceManager)
    {
        ThrowIf.Argument.IsNull(objectFactory, nameof(objectFactory));
        ThrowIf.Argument.IsNull(ugoTypes, nameof(ugoTypes));
        ThrowIf.Argument.IsNull(ugoBaseComponent, nameof(ugoBaseComponent));
        ThrowIf.Argument.IsNull(databaseProxyComponent, nameof(databaseProxyComponent));
        ThrowIf.Argument.IsNull(traceManager, nameof(traceManager));

        _objectFactory = objectFactory;
        _ugoTypes = ugoTypes;
        _ugoBaseComponent = ugoBaseComponent;
        _databaseProxyComponent = databaseProxyComponent;
        _traceManager = traceManager;
    }

    internal void ResetForProject()
    {
        _luminaireDbIds = null;
        _luminaireIndex = 0;
        _compactLuminaireDbId = null;
        _outlineConfigurationsByDiameter.Clear();
    }

    internal IAcadNode PlaceLuminaire(
        IAcadNet net,
        Point3d position,
        int? explicitDbId,
        double mountingHeight,
        string title)
    {
        ThrowIf.Argument.IsNull(net, nameof(net));
        ThrowIf.Argument.IsNull(title, nameof(title));

        var lightingType = ResolveLightingType()
                           ?? throw new InvalidOperationException("Тип ElLighting не найден.");
        var typeId = _ugoTypes.GetTypeId(lightingType);
        var ugoData = _ugoBaseComponent.UgoList.FirstOrDefault(ugo => ugo.UgoElemTypeId == typeId);
        if (ugoData.IsNull())
        {
            throw new InvalidOperationException("УГО ElLighting не найдено.");
        }

        var previousState = _objectFactory.IsShowPropPageOnCreate;
        _objectFactory.IsShowPropPageOnCreate = false;
        try
        {
            var node = _objectFactory.CreateObject(
                net,
                ugoData,
                lightingType,
                position,
                0,
                new Point3d(0, 0, 1));
            if (node == null)
            {
                throw new InvalidOperationException("ElLighting не создан.");
            }

            BindLuminaireProduct(node, lightingType, explicitDbId);
            lightingType.GetProperty("MountingHeightMm")!.SetValue(node, mountingHeight);
            node.TitleName = title;
            node.SaveMe();
            node.UpdateView();
            return node;
        }
        finally
        {
            _objectFactory.IsShowPropPageOnCreate = previousState;
        }
    }

    internal AcadTrace Connect(
        IAcadNode first,
        IAcadNode second,
        double height = 0)
    {
        ThrowIf.Argument.IsNull(first, nameof(first));
        ThrowIf.Argument.IsNull(second, nameof(second));

        _traceType ??= ResolveAssemblyType(
            "Nanosoft.ObjectLib.WireCommon",
            "Nanosoft.SCS.ScsTrace");
        _createTraceMethod ??= typeof(IAcadTraceManager).GetMethods()
            .FirstOrDefault(method =>
                method.Name == nameof(IAcadTraceManager.CreateTrace) &&
                method.IsGenericMethod &&
                method.GetParameters().Length == 2);
        if (_traceType == null || _createTraceMethod == null)
        {
            throw new InvalidOperationException("Механизм создания трассы не найден.");
        }

        var id = (GlobalId)_createTraceMethod.MakeGenericMethod(_traceType)
            .Invoke(_traceManager, new object[] { first.AcId, second.AcId })!;
        var trace = first.Net.OpenObject(id) as AcadTrace
            ?? throw new InvalidOperationException("Трасса между светильниками не создана.");
        var slopeTrace = (ISlopeTrace)trace;
        slopeTrace.FirstPointHeight = height;
        slopeTrace.LastPointHeight = height;
        trace.SaveMe();
        trace.UpdateView();
        return trace;
    }

    internal IEnumerable<AcadTrace> CreateOutline(
        IAcadNet net,
        IReadOnlyList<Point3d> vertices,
        double diameter)
    {
        ThrowIf.Argument.IsNull(net, nameof(net));
        ThrowIf.Argument.IsNull(vertices, nameof(vertices));
        _traceType ??= ResolveAssemblyType("Nanosoft.ObjectLib.WireCommon", "Nanosoft.SCS.ScsTrace")
            ?? throw new InvalidOperationException("Тип ScsTrace не найден.");
        _outlineCanalType ??= ResolveAssemblyType("Nanosoft.ObjectLib.WireCommon", "Nanosoft.SCS.ScsTubeCanal")
            ?? throw new InvalidOperationException("Тип ScsTubeCanal не найден.");
        if (!_outlineConfigurationsByDiameter.TryGetValue(diameter, out var configurationId))
        {
            configurationId = CreateOutlineConfiguration(diameter);
            _outlineConfigurationsByDiameter.Add(diameter, configurationId);
        }

        var vertexCount = vertices[0].Equals(vertices[vertices.Count - 1]) ? vertices.Count - 1 : vertices.Count;
        for (var i = 0; i < vertexCount; i++)
        {
            var trace = _objectFactory.CreateConnector(net, _traceType, null!, false) as AcadTrace
                ?? throw new InvalidOperationException("Трасса по границе контура не создана.");
            trace.SetVertexPosition(0, vertices[i]);
            trace.SetVertexPosition(1, vertices[(i + 1) % vertexCount]);
            var slopeTrace = (ISlopeTrace)trace;
            slopeTrace.FirstPointHeight = 0;
            slopeTrace.LastPointHeight = 0;
            SetOutlineCanal(trace, configurationId);
            trace.SaveMe();
            trace.UpdateView();
            yield return trace;
        }
    }

    private int CreateOutlineConfiguration(double diameter)
    {
        var tubeType = ResolveAssemblyType("Nanosoft.ObjectLib.WireCommon.Db",
            "Nanosoft.ObjectLib.WireCommon.Db.DbWireCanals.TubeCanal.DbFittings.DbScsTubeCanal")
            ?? throw new InvalidOperationException("Тип изделия трубы не найден.");
        var configurationType = ResolveAssemblyType("Nanosoft.ObjectLib.WireCommon.Db",
            "Nanosoft.ObjectLib.WireCommon.Db.DbWireCanals.TubeCanal.DbMountSystem.DbTbMountSystem")
            ?? throw new InvalidOperationException("Тип конфигурации трубы не найден.");

        // Отдельные записи в БД нового тестового проекта; существующие изделия не меняем.
        var tubeRecord = _databaseProxyComponent.GetDb(tubeType).GetDataTypedSource(tubeType).AddNewDataObject();
        var tube = (IDbObject)tubeRecord.Object;
        tube.DbName = $"Рамка тестового контура Ø{diameter:0}";
        tubeType.GetProperty("ExternalDiameter")!.SetValue(tube, diameter);
        tubeType.GetProperty("InternalDiameter")!.SetValue(tube, diameter * 0.9);
        tubeRecord.SaveObject(tube, true);

        var configurationRecord = _databaseProxyComponent.GetDb(configurationType).GetDataTypedSource(configurationType).AddNewDataObject();
        var configuration = (IDbObject)configurationRecord.Object;
        configuration.DbName = $"Рамки тестовых контуров Ø{diameter:0}";
        configurationType.GetProperty("TubeCanal")!.SetValue(configuration, tube);
        configurationRecord.SaveObject(configuration, true);
        return configurationRecord.Id;
    }

    //based on CanalOptions.AddNewCanalToTrace / SetDataToTrace: только назначение канала, без изменения общих настроек.
    private void SetOutlineCanal(AcadTrace trace, int configurationId)
    {
        var canal = trace.Net.ObjectFactory.CreateDo(_outlineCanalType!, trace.AcId.paperId) as AcadData
            ?? throw new InvalidOperationException("Канал рамки не создан.");
        var previousCanal = _traceType!.GetProperty("Canal")!.GetValue(trace) as IAcadObject;
        previousCanal?.Delete();
        ((IDbIdProvider)canal).DbId = configurationId;
        trace.AddChild(canal);
    }

    internal int GetCompactLuminaireDbId()
    {
        if (_compactLuminaireDbId.HasValue) { return _compactLuminaireDbId.Value; }
        var lightingType = ResolveLightingType() ?? throw new InvalidOperationException("Тип ElLighting не найден.");
        var ids = GetLuminaireDbIds(lightingType);
        if (ids.Count == 0) { throw new InvalidOperationException("В БД не найдены светильники."); }
        var dbType = lightingType.GetCustomAttributes(typeof(DbMapTypeAttribute), false).Cast<DbMapTypeAttribute>().First().DbType;
        var dimensions = dbType.GetProperties().Where(x => x.Name is "LightLength" or "Width" or "Diameter").ToArray();
        var products = _databaseProxyComponent.GetDb(dbType).GetDataTypedSource(dbType).DataObjects;
        _compactLuminaireDbId = products.Where(x => ids.Contains(x.Id)).OrderBy(x => dimensions
            .Select(p => p.GetValue(x.Object) is double value ? value : 0)
            .Where(value => value > 0).DefaultIfEmpty(double.PositiveInfinity).Max()).First().Id;
        return _compactLuminaireDbId.Value;
    }

    private void BindLuminaireProduct(
        IAcadNode node,
        Type lightingType,
        int? explicitDbId = null)
    {
        if (node is not IDbIdProvider idProvider)
        {
            throw new InvalidOperationException($"{node.GetType().FullName} не реализует IDbIdProvider.");
        }

        var dbIds = GetLuminaireDbIds(lightingType);
        if (dbIds.Count == 0)
        {
            throw new InvalidOperationException("В проектной или демонстрационной БД не найдены светильники.");
        }

        if (explicitDbId.HasValue)
        {
            idProvider.DbId = explicitDbId.Value;
        }
        else
        {
            idProvider.DbId = dbIds[_luminaireIndex % dbIds.Count];
            _luminaireIndex++;
        }
    }

    private List<int> GetLuminaireDbIds(Type lightingType)
    {
        if (_luminaireDbIds != null)
        {
            return _luminaireDbIds;
        }

        var dbType = lightingType
            .GetCustomAttributes(typeof(DbMapTypeAttribute), false)
            .OfType<DbMapTypeAttribute>()
            .FirstOrDefault()?
            .DbType;
        if (dbType == null)
        {
            return _luminaireDbIds = new List<int>();
        }

        var projectSource = _databaseProxyComponent.GetDb(dbType);
        var result = projectSource.GetDataTypedSource(dbType).DataObjects
            .Take(HierarchyTestConstants.MaximumLuminaireProducts)
            .Select(x => x.Id)
            .ToList();
        if (result.Count > 0)
        {
            return _luminaireDbIds = result;
        }

        var demoSource = FindDemoSource(dbType, projectSource);
        var demoObjects = demoSource?.GetDataTypedSource(dbType).DataObjects;
        if (demoObjects == null)
        {
            return _luminaireDbIds = result;
        }

        foreach (var dataObject in demoObjects.Take(HierarchyTestConstants.MaximumLuminaireProducts))
        {
            // Прямой импорт синхронный, без отложенного показа ProgressShower.
            var exported = new DataBaseExporter(projectSource).Export(dataObject)
                ?? throw new InvalidOperationException($"Не удалось импортировать светильник «{dataObject.Text}» в тестовый проект.");
            result.Add(exported.Id);
        }

        return _luminaireDbIds = result;
    }

    private static IDataSource? FindDemoSource(Type dbType, IDataSource projectSource)
    {
        foreach (IDataBaseInfo info in AddIn.DataBase.AllSources)
        {
            var source = info.DataSource;
            if (!ReferenceEquals(source, projectSource) &&
                source.GetDataTypedSource(dbType).DataObjects.Count > 0)
            {
                return source;
            }
        }

        return null;
    }

    private Type? ResolveLightingType()
    {
        return _lightingType ??= ResolveAssemblyType(
            "Nanosoft.ObjectLib.ESN",
            "Nanosoft.Electrica.ElLighting");
    }

    private static Type? ResolveAssemblyType(string assemblyName, string fullTypeName)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                               .FirstOrDefault(candidate => candidate.GetName().Name == assemblyName)
                           ?? Assembly.Load(assemblyName);
            return assembly.GetType(fullTypeName, false);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
