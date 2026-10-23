using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Guard;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy.Models;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy.Selection;
using Nanosoft.CAE.Module.BuildingModel.AreaHierarchy.Selection.Coordinates;
using Nanosoft.Platform.FormExtension;
using Nanosoft.Studio.Utils;
using Nanosoft.Studio.WPF.TreeControl.ViewModel;
using Nanosoft.Utils;

namespace MyPlugin.HierarchyTestCommands;

// Управляем настоящими контролами формы; методы выбора во view model не вызываем.
internal sealed class HierarchyTestAutomaticSelection
{
    private const string CoordinatesName = "Тест XYZ и поворота";
    private readonly int _rootIndex;
    private readonly bool _shifted;
    private readonly IDialogStack _dialogStack;

    internal HierarchyTestAutomaticSelection(
        int rootIndex,
        bool shifted,
        IDialogStack dialogStack)
    {
        ThrowIf.Argument.IsNull(dialogStack, nameof(dialogStack));
        _rootIndex = rootIndex;
        _shifted = shifted;
        _dialogStack = dialogStack;
    }

    internal int RootCount { get; private set; }

    internal HierarchySelection? Show(AreaHierarchyModel model, IHierarchySelectionService service)
    {
        ThrowIf.Argument.IsNull(model, nameof(model));
        ThrowIf.Argument.IsNull(service, nameof(service));
        var roots = new List<(AreaHierarchyNode Node, string Text)>();
        foreach (var node in model.Items)
        {
            var itemType = typeof(HierarchySelectionForm).Assembly.GetType(
                "Nanosoft.CAE.Module.BuildingModel.AreaHierarchy.Selection.HierarchyNodeSelectionItem", true)!;
            var item = Activator.CreateInstance(itemType, new object[] { node, false })!;
            roots.Add((node, (string)itemType.GetProperty("Text")!.GetValue(item)!));
        }
        roots.Sort((first, second) => StringNumberCompareHelper.Compare(first.Text, second.Text));
        RootCount = roots.Count;
        var root = roots[_rootIndex];
        if (_rootIndex == 0 && !_shifted)
        {
            SaveCoordinates(root.Node);
        }

        // Таймер работает внутри модального цикла ShowDialog. Между действиями форма успевает обновиться.
        var step = 0;
        var action = "Поиск окна выбора в стеке диалогов";
        Exception? failure = null;
        HierarchySelectionForm? form = null;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += Tick;
        timer.Start();
        try
        {
            var selection = service.Show(model);
            if (failure != null)
            {
                throw new InvalidOperationException($"Автовыбор «{root.Text}» не выполнен. Шаг: {action}.\n{failure}", failure);
            }
            if (selection != null && (!ReferenceEquals(selection.Root, root.Node) ||
                (_shifted ? selection.TransformMatrix.Name != CoordinatesName : !ReferenceEquals(selection.TransformMatrix, AreaHierarchyInsertAreaMatrix.EmptyMatrix))))
            {
                throw new InvalidOperationException("Окно подтвердило не тот рут или не те координаты, которые выбирала автоматизация.");
            }
            return selection;
        }
        finally
        {
            timer.Stop();
            timer.Tick -= Tick;
        }

        void Tick(object? sender, EventArgs args)
        {
            try
            {
                form ??= HwndSource.FromHwnd(_dialogStack.GetCurrent())?.RootVisual as HierarchySelectionForm;
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
                {
                    throw new TimeoutException("Окно выбора не ответило за 30 секунд.");
                }
                if (form == null || !form.IsVisible)
                {
                    return;
                }

                action = step switch
                {
                    0 => "Открытие списка рутов",
                    1 => "Клик по строке рута",
                    2 => "Открытие списка координат",
                    3 => "Выбор координат",
                    4 => "Нажатие OK",
                    _ => "Завершение выбора"
                };

                switch (step)
                {
                    case 0:
                        form.Activate();
                        var rootButton = (ToggleButton)form.FindName("RootButton")!;
                        ((IToggleProvider)new ToggleButtonAutomationPeer(rootButton).GetPattern(PatternInterface.Toggle)!).Toggle();
                        break;
                    case 1:
                        var rootTree = FindControls<TreeView>((DependencyObject)form.FindName("RootTree")!).Single();
                        rootTree.UpdateLayout();
                        var item = rootTree.Items.OfType<ItemViewModel>().Single(x =>
                            ReferenceEquals(x.Item.GetType().GetProperty("Node")?.GetValue(x.Item), root.Node));
                        var row = (TreeViewItem?)rootTree.ItemContainerGenerator.ContainerFromItem(item)
                            ?? throw new InvalidOperationException("Не найдена строка рута в дереве.");
                        row.BringIntoView();
                        rootTree.UpdateLayout();
                        var text = FindControls<TextBlock>(row).First(x => x.Text == root.Text);
                        // Клик по тексту подтверждает рут и закрывает список — точно как при ручном выборе.
                        Click(text);
                        break;
                    case 2:
                        var combo = FindControls<ComboBox>(form).Single();
                        combo.IsDropDownOpen = true;
                        combo.UpdateLayout();
                        break;
                    case 3:
                        var coordinates = FindControls<ComboBox>(form).Single();
                        var matrix = coordinates.Items.Cast<AreaHierarchyInsertAreaMatrix>().Single(x =>
                            _shifted ? x.Name == CoordinatesName : ReferenceEquals(x, AreaHierarchyInsertAreaMatrix.EmptyMatrix));
                        var comboPeer = new ComboBoxAutomationPeer(coordinates);
                        var matrixPeer = new ListBoxItemAutomationPeer(matrix, comboPeer);
                        ((ISelectionItemProvider)matrixPeer).Select();
                        coordinates.IsDropDownOpen = false;
                        break;
                    case 4:
                        var accept = FindControls<Button>(form).Single(x => x.IsDefault);
                        if (!accept.IsEnabled)
                        {
                            throw new InvalidOperationException("После выбора рута кнопка OK недоступна.");
                        }
                        timer.Stop();
                        ((IInvokeProvider)new ButtonAutomationPeer(accept).GetPattern(PatternInterface.Invoke)!).Invoke();
                        break;
                }
                step++;
            }
            catch (Exception exception)
            {
                failure = exception;
                timer.Stop();
                if (form != null)
                {
                    form.DialogResult = false;
                }
            }
        }
    }

    private static void SaveCoordinates(AreaHierarchyNode root)
    {
        var description = HierarchyTestExportService.DescribeRoot(root);
        var key = description.Kind == "Floor" ? description.Id.Replace('/', ':') : description.Id;
        var path = Path.Combine(HierarchyTestExport.ProjectDirectory, "AreaHierarchyMatrices.xml");
        var matrices = TableFile.Open<AreaHierarchyInsertAreaMatrix>(path).ToList();
        matrices.RemoveAll(x => x.RootKind == description.Kind && x.RootKey == key && x.Name == CoordinatesName);
        matrices.Add(new AreaHierarchyInsertAreaMatrix
        {
            RootKind = description.Kind, RootKey = key, Name = CoordinatesName,
            X = 12345, Y = -6789, Z = 2345, RotationAngle = -17
        });
        TableFile.Save(matrices, path);
    }

    private static IEnumerable<T> FindControls<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T control)
        {
            yield return control;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            foreach (var child in FindControls<T>(VisualTreeHelper.GetChild(element, index)))
            {
                yield return child;
            }
        }
    }

    private static void Click(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        if (!SetCursorPos((int)point.X, (int)point.Y))
        {
            throw new InvalidOperationException("Не удалось переместить мышь к строке рута.");
        }
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags,
        uint x,
        uint y,
        uint data,
        UIntPtr extraInfo);
}
