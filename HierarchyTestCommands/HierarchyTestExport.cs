using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Evos.Api.Model.Output;
using Guard;
using Newtonsoft.Json;
using Nanosoft.Acad;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy.Diagnostics;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy.Models;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy.Selection;
using Nanosoft.Platform.PlatformIfaces;
using Nanosoft.Utils;

using Nanosoft.Platform.FormExtension;
using Nanosoft.CAE.Localization;
using Nanosoft.Platform.Localization;

namespace MyPlugin.HierarchyTestCommands;

internal sealed class HierarchyTestExport
{
    public string RootKind = "";
    public string RootId = "";
    public string RootText = "";
    public Guid[] SelectedContours = Array.Empty<Guid>();
    public Guid[] ContourOrder = Array.Empty<Guid>();
    public double X;
    public double Y;
    public double Z;
    public double Rotation;
    public bool UseFloorAbsoluteHeight = true;
    public string? DwgPath;
    public bool GenerationFailed;
    public string? EmptyModelMessage;
    public string[] GenerationErrors = Array.Empty<string>();
    public string[] GenerationWarnings = Array.Empty<string>();

    internal static string ProjectDirectory => Path.GetDirectoryName(global::Nanosoft.Studio.Studio.Instance.Workspace.Path)
        ?? throw new InvalidOperationException("Тестовый проект не открыт.");

    internal static HierarchyTestScene ReadScene()
    {
        return JsonConvert.DeserializeObject<HierarchyTestScene>(File.ReadAllText(Path.Combine(ProjectDirectory, "hierarchy-test-scene.json")))
            ?? throw new InvalidOperationException("Паспорт тестового проекта пуст.");
    }
}

public sealed class HierarchyTestExportService
{
    private readonly IHierarchy3DGenerationService _generation;
    private readonly ICwCadDocService _documents;
    private readonly HierarchyTestProjectService _projects;
    private readonly IDialogStackMessageBox _messageBox;
    private readonly IDialogStack _dialogStack;

    public HierarchyTestExportService(
        IHierarchy3DGenerationService generation,
        ICwCadDocService documents,
        HierarchyTestProjectService projects,
        IDialogStackMessageBox messageBox,
        IDialogStack dialogStack)
    {
        ThrowIf.Argument.IsNull(generation, nameof(generation));
        ThrowIf.Argument.IsNull(documents, nameof(documents));
        ThrowIf.Argument.IsNull(projects, nameof(projects));
        ThrowIf.Argument.IsNull(messageBox, nameof(messageBox));
        ThrowIf.Argument.IsNull(dialogStack, nameof(dialogStack));
        _generation = generation;
        _documents = documents;
        _projects = projects;
        _messageBox = messageBox;
        _dialogStack = dialogStack;
    }

    public void Export() => Export(null);

    public void ExportAll()
    {
        var summary = new List<string>();
        var first = new HierarchyTestAutomaticSelection(0, false, _dialogStack);
        Generate(first, "нулевые координаты");
        Generate(new HierarchyTestAutomaticSelection(0, true, _dialogStack), "XYZ и поворот");
        for (var index = 1; index < first.RootCount; index++)
        {
            Generate(new HierarchyTestAutomaticSelection(index, false, _dialogStack), "нулевые координаты");
        }
        _messageBox.Show(string.Join("\n", summary), "Тестовые 3D-модели", MessageBoxImage.Information);

        void Generate(HierarchyTestAutomaticSelection automatic, string coordinates)
        {
            var result = Export(automatic)
                ?? throw new InvalidOperationException("Автоматическая выгрузка остановлена до подтверждения выбора.");
            var status = result.EmptyModelMessage != null ? "нет объектов, DWG не создан" : result.GenerationFailed ? "ОШИБКА" : "DWG сохранён";
            summary.Add($"{result.RootText} ({coordinates}): {status}");
        }
    }

    private HierarchyTestExport? Export(HierarchyTestAutomaticSelection? automatic)
    {
        var scene = HierarchyTestExport.ReadScene();
        _projects.ActivatePlan(HierarchyTestConstants.FirstPlanName);
        _documents.GetCurrentDwg(out var before);

        // Только тестовая инструментация: запускаем настоящий генератор, не копируем его алгоритм.
        // У него нет события выбора, поэтому временно перехватываем сервисы и обязательно восстанавливаем их.
        var selectionField = GetField("_selectionService");
        var reporterField = GetField("_diagnosticsReporter");
        var originalSelection = (IHierarchySelectionService)selectionField.GetValue(_generation)!;
        var originalReporter = (IAreaHierarchyDiagnosticsReporter)reporterField.GetValue(_generation)!;
        var selection = new RecordingSelection(originalSelection, automatic);
        var reporter = new RecordingReporter(originalReporter, automatic != null);
        FieldInfo? messageBoxField = null;
        IDialogStackMessageBox? originalMessageBox = null;
        RecordingMessageBox? messageBox = null;
        if (automatic != null)
        {
            messageBoxField = GetField("_messageBox");
            originalMessageBox = (IDialogStackMessageBox)messageBoxField.GetValue(_generation)!;
            messageBox = new RecordingMessageBox(originalMessageBox);
        }
        try
        {
            selectionField.SetValue(_generation, selection);
            reporterField.SetValue(_generation, reporter);
            if (messageBox != null)
            {
                messageBoxField!.SetValue(_generation, messageBox);
            }
            _generation.Generate();
        }
        finally
        {
            selectionField.SetValue(_generation, originalSelection);
            reporterField.SetValue(_generation, originalReporter);
            if (messageBoxField != null)
            {
                messageBoxField.SetValue(_generation, originalMessageBox);
            }
        }

        if (selection.Value == null)
        {
            if (automatic != null && reporter.Diagnostics.Any(x => x.Level == OutputLevel.Error))
            {
                throw new InvalidOperationException(string.Join("\n", reporter.Diagnostics.Where(x => x.Level == OutputLevel.Error).Select(x => x.Message)));
            }
            return null;
        }
        var result = CreateRecord(selection.Value, selection.Model!);
        var knownContours = scene.Contours.Select(x => x.Id).ToHashSet();
        if (result.SelectedContours.Any(x => !knownContours.Contains(x)))
        {
            throw new InvalidOperationException("Выбор содержит контуры, которых нет в паспорте стенда. Пересоздайте стенд.");
        }
        result.GenerationErrors = reporter.Diagnostics.Where(x => x.Level == OutputLevel.Error).Select(x => x.Message).ToArray();
        result.GenerationWarnings = reporter.Diagnostics.Where(x => x.Level == OutputLevel.Warning).Select(x => x.Message).ToArray();
        result.EmptyModelMessage = messageBox?.EmptyModelMessage;
        _documents.GetCurrentDwg(out var after);
        result.GenerationFailed = result.EmptyModelMessage != null || result.GenerationErrors.Length > 0 || string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
        var recordPath = Path.Combine(HierarchyTestExport.ProjectDirectory, $"hierarchy-export-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        if (!result.GenerationFailed)
        {
            result.DwgPath = Path.ChangeExtension(recordPath, ".dwg");
            if (_documents.SaveCurrentDwgAs(result.DwgPath) != ErrCode.sOK)
            {
                throw new InvalidOperationException($"Не удалось сохранить результат: {result.DwgPath}");
            }
        }
        File.WriteAllText(recordPath, JsonConvert.SerializeObject(result, Formatting.Indented));
        if (!result.GenerationFailed)
        {
            // SaveCurrentDwgAs только записывает базу на диск, не переименовывая открытый документ.
            // Оставляем открытым сам сохранённый результат, который будет читать проверялка.
            if (_documents.CloseCurrentDwg() != ErrCode.sOK)
            {
                throw new InvalidOperationException("Результат сохранён, но не удалось закрыть безымянный документ.");
            }
            if (_documents.OpenAcadDrawing(result.DwgPath!) != ErrCode.sOK)
            {
                throw new InvalidOperationException($"Результат сохранён, но не удалось открыть {result.DwgPath}");
            }
        }
        return result;
    }

    private FieldInfo GetField(string name) => _generation.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Тестовый перехват устарел: в генераторе нет {name}.");

    private static HierarchyTestExport CreateRecord(HierarchySelection selection, AreaHierarchyModel model)
    {
        var nodes = new List<AreaHierarchyNode>();
        foreach (var item in model.Items)
        {
            nodes.AddRange(Walk(item));
        }
        var root = selection.Root;
        var description = DescribeRoot(root);
        return new HierarchyTestExport
        {
            RootKind = description.Kind, RootId = description.Id, RootText = description.Text,
            SelectedContours = selection.ContoursByNode.Values.SelectMany(x => x).Select(x => x.AcId.ToGuid()).ToArray(),
            ContourOrder = nodes.SelectMany(x => x.PhysicalContours).Select(x => x.AcId.ToGuid()).ToArray(),
            X = selection.TransformMatrix.X, Y = selection.TransformMatrix.Y, Z = selection.TransformMatrix.Z,
            Rotation = selection.TransformMatrix.RotationAngle
        };
    }

    internal static (string Kind, string Id, string Text) DescribeRoot(AreaHierarchyNode root)
    {
        ThrowIf.Argument.IsNull(root, nameof(root));
        return root switch
        {
            TerritoryNode x => ("Territory", x.Id, x.Id),
            ZoneNode x => ("Zone", x.Id, x.Id),
            BuildingNode x => ("Building", x.Id.ToString(), $"Здание {x.Id}"),
            FloorNode x => ("Floor", $"{x.Id.BuildingId}/{x.Id.Number}", $"Этаж {x.Id.Number}, здание {x.Id.BuildingId}"),
            _ => throw new InvalidOperationException("Неизвестный тип рута.")
        };
    }

    private static IEnumerable<AreaHierarchyNode> Walk(AreaHierarchyNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class RecordingSelection : IHierarchySelectionService
    {
        private readonly IHierarchySelectionService _inner;
        private readonly HierarchyTestAutomaticSelection? _automatic;
        internal HierarchySelection? Value;
        internal AreaHierarchyModel? Model;
        internal RecordingSelection(IHierarchySelectionService inner, HierarchyTestAutomaticSelection? automatic)
        {
            ThrowIf.Argument.IsNull(inner, nameof(inner));
            _inner = inner;
            _automatic = automatic;
        }
        public HierarchySelection? Show(AreaHierarchyModel model)
        {
            ThrowIf.Argument.IsNull(model, nameof(model));
            Model = model;
            return Value = _automatic == null ? _inner.Show(model) : _automatic.Show(model, _inner);
        }
    }

    private sealed class RecordingMessageBox : IDialogStackMessageBox
    {
        private readonly IDialogStackMessageBox _inner;
        internal string? EmptyModelMessage;

        internal RecordingMessageBox(IDialogStackMessageBox inner)
        {
            ThrowIf.Argument.IsNull(inner, nameof(inner));
            _inner = inner;
        }

        public void Show(
            string text,
            string caption,
            MessageBoxImage icon = MessageBoxImage.None,
            string? actionLinkText = null,
            Action? onActionLink = null)
        {
            ThrowIf.Argument.IsNull(text, nameof(text));
            ThrowIf.Argument.IsNull(caption, nameof(caption));
            if (text == CaeProjectLocalizationDictionary.ВВыбранныхКонтурахНетОбъектовДляПостроения3D_Модели_.GetLocalizedString())
            {
                EmptyModelMessage = text;
                // Подробности передаются в уже подмененный тихий RecordingReporter, без открытия окна.
                onActionLink?.Invoke();
                return;
            }
            _inner.Show(text, caption, icon, actionLinkText!, onActionLink!);
        }
    }

    private sealed class RecordingReporter : IAreaHierarchyDiagnosticsReporter
    {
        private readonly IAreaHierarchyDiagnosticsReporter _inner;
        private readonly bool _silent;
        internal readonly List<OperationOutputEntry> Diagnostics = new();
        internal RecordingReporter(IAreaHierarchyDiagnosticsReporter inner, bool silent)
        {
            ThrowIf.Argument.IsNull(inner, nameof(inner));
            _inner = inner;
            _silent = silent;
        }
        public void Show(IReadOnlyList<OperationOutputEntry> diagnostics, bool forceShow = false)
        {
            ThrowIf.Argument.IsNull(diagnostics, nameof(diagnostics));
            Diagnostics.AddRange(diagnostics);
            if (!_silent)
            {
                _inner.Show(diagnostics, forceShow);
            }
        }
    }
}
