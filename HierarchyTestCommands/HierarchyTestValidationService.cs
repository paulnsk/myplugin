using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Evos.Api.Model.Output;
using Guard;
using Newtonsoft.Json;
using Nanosoft.Acad;
using Nanosoft.Acad.BuildingModel;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.Acad.Net;
using Nanosoft.CAE.Evos.Ui.TreeLog.Interfaces;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy.Diagnostics;
using Nanosoft.Platform.PlatformIfaces;
using Nanosoft.Utils;
using Nanosoft.Utils.Point3d;

namespace MyPlugin.HierarchyTestCommands;

public sealed class HierarchyTestValidationService
{
    // Сотая доля миллиметра: достаточно строго для стенда, без ошибок из-за округления сохранённого DWG.
    private const double CoordinateTolerance = 0.01;
    private readonly IAcadNetFactory _netFactory;
    private readonly ICwCadDocService _documents;
    private readonly IDialogStackMessageBox _messageBox;
    private readonly IOutputLogService _outputLog;

    public HierarchyTestValidationService(
        IAcadNetFactory netFactory,
        ICwCadDocService documents,
        IDialogStackMessageBox messageBox,
        IOutputLogService outputLog)
    {
        ThrowIf.Argument.IsNull(netFactory, nameof(netFactory));
        ThrowIf.Argument.IsNull(documents, nameof(documents));
        ThrowIf.Argument.IsNull(messageBox, nameof(messageBox));
        ThrowIf.Argument.IsNull(outputLog, nameof(outputLog));
        _netFactory = netFactory;
        _documents = documents;
        _messageBox = messageBox;
        _outputLog = outputLog;
    }

    public void Validate()
    {
        var scene = HierarchyTestExport.ReadScene();
        var records = Directory.GetFiles(HierarchyTestExport.ProjectDirectory, "hierarchy-export-*.json").OrderBy(x => x).ToArray();
        if (records.Length == 0)
        {
            _messageBox.Show("Нет сохранённых тестовых выгрузок. Выполните CW_HIERARCHY_TEST_GENERATE3D.", "Проверка стенда", MessageBoxImage.Information);
            return;
        }
        var diagnostics = new List<OperationOutputEntry>();
        var summary = new List<string>();
        _documents.GetCurrentDwg(out var originalDrawing);
        try
        {
            foreach (var recordPath in records)
            {
                var before = diagnostics.Count;
                var label = Path.GetFileNameWithoutExtension(recordPath);
                try
                {
                    var export = JsonConvert.DeserializeObject<HierarchyTestExport>(File.ReadAllText(recordPath))
                        ?? throw new InvalidOperationException("Запись выгрузки пуста.");
                    var coordinates = export.X == 0 && export.Y == 0 && export.Z == 0 && export.Rotation == 0
                        ? ""
                        : $", XYZ=({export.X}; {export.Y}; {export.Z}), поворот {export.Rotation}°";
                    label = $"{summary.Count + 1}. {export.RootText}, контуров {export.SelectedContours.Length}{coordinates}";
                    export.RootText = label;
                    ValidateExport(scene, export, diagnostics);
                }
                catch (Exception exception)
                {
                    AddError(diagnostics, $"{label}: {exception.Message}");
                }
                var status = diagnostics.Count != before ? "ОШИБКА" : "OK";
                summary.Add($"{label}: {status}");
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(originalDrawing))
            {
                _documents.ActivateAcadDrawing(originalDrawing);
            }
        }

        var text = string.Join("\n", summary);
        if (diagnostics.Count == 0)
        {
            _messageBox.Show(text, "Проверка стенда", MessageBoxImage.Information);
        }
        else
        {
            _messageBox.Show(text, "Проверка стенда", MessageBoxImage.Warning, "Подробности",
                () => _outputLog.ShowOutput(diagnostics, new AreaHierarchyLogTreeTemplates(), title: "Проверка тестовых выгрузок"));
        }
    }

    private void ValidateExport(
        HierarchyTestScene scene,
        HierarchyTestExport export,
        List<OperationOutputEntry> diagnostics)
    {
        var knownContours = scene.Contours.Select(x => x.Id).ToHashSet();
        if (export.SelectedContours.Any(x => !knownContours.Contains(x)))
        {
            throw new InvalidOperationException("Выбор не соответствует паспорту проекта.");
        }
        var expected = new HierarchyTestExpectedCoordinates(scene, export);
        if (export.GenerationFailed || export.DwgPath == null)
        {
            throw new InvalidOperationException($"Модель не создана. {export.EmptyModelMessage ?? string.Join("; ", export.GenerationErrors)}");
        }
        if (!File.Exists(export.DwgPath))
        {
            throw new InvalidOperationException($"Нет файла результата: {export.DwgPath}");
        }
        if (!_documents.IsDrawningOpened(export.DwgPath) && _documents.OpenAcadDrawing(export.DwgPath) != ErrCode.sOK)
        {
            throw new InvalidOperationException($"Не удалось открыть {export.DwgPath}");
        }
        if (_documents.ActivateAcadDrawing(export.DwgPath) != ErrCode.sOK)
        {
            throw new InvalidOperationException($"Не удалось активировать {export.DwgPath}");
        }
        var net = _netFactory.NewNet();
        net.LoadCurrentDocument();
        var lights = net.GetNodes<ElementModelDwgView>().ToList();
        var traces = net.GetNodes<ConnectorModelDwgView>().ToDictionary(x => x, x => x.GetVertices());
        foreach (var obj in expected.Objects)
        {
            var points = expected.GetPoints(obj);
            var sourceId = GlobalId.FromGuid(obj.Id);
            if (obj.Kind == "Light")
            {
                var matches = lights.Where(x => x.NoteName == obj.Name).ToList();
                if (matches.Count != 1)
                {
                    AddError(diagnostics, $"{export.RootText}: {obj.Name}, ожидался один светильник, найдено {matches.Count}.", new[] { sourceId });
                }
                else if (!points[0].Equals(matches[0].Position, CoordinateTolerance))
                {
                    AddError(diagnostics, $"{export.RootText}: {obj.Name}, ожидалось {points[0]}, получено {matches[0].Position}.", new[] { sourceId });
                }
                foreach (var match in matches)
                {
                    lights.Remove(match);
                }
            }
            else if (obj.Kind == "Trace")
            {
                var match = traces.FirstOrDefault(x => SamePoints(points, x.Value)).Key;
                if (match == null)
                {
                    AddError(diagnostics, $"{export.RootText}: {obj.Name}, не найдена трасса с точками {string.Join(" → ", points.Select(x => x.ToString()))}.", new[] { sourceId });
                }
                else
                {
                    traces.Remove(match);
                }
            }
            else
            {
                throw new InvalidOperationException($"Проверялка не знает тип объекта {obj.Kind}.");
            }
        }
        if (lights.Count > 0 || traces.Count > 0)
        {
            AddError(diagnostics, $"{export.RootText}: лишние объекты — светильников {lights.Count}, трасс {traces.Count}.",
                lights.Select(x => x.AcId).Concat(traces.Keys.Select(x => x.AcId)));
        }
    }

    private static bool SamePoints(IReadOnlyList<Point3d> expected, IReadOnlyList<Point3d> actual)
    {
        return expected.Count == actual.Count &&
            (expected.Select((point, i) => point.Equals(actual[i], CoordinateTolerance)).All(x => x) ||
             expected.Select((point, i) => point.Equals(actual[actual.Count - 1 - i], CoordinateTolerance)).All(x => x));
    }

    private static void AddError(
        List<OperationOutputEntry> diagnostics,
        string message,
        IEnumerable<GlobalId>? objects = null)
    {
        diagnostics.Add(new OperationOutputEntry(OutputLevel.Error, new AreaHierarchyDiagnosticPayload(typeof(HierarchyTestValidationService), message, objects)));
    }
}
