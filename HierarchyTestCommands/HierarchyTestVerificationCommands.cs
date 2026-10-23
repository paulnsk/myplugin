using System;
using Guard;
using Nanosoft.Acad.CadCommand;
using Nanosoft.Platform.PlatformIfaces;

namespace MyPlugin.HierarchyTestCommands;

public sealed class CwHierarchyTestExportCommand : ICadCommand
{
    private readonly HierarchyTestExportService _service;
    private readonly IDialogStackMessageBox _messageBox;

    public CwHierarchyTestExportCommand(HierarchyTestExportService service, IDialogStackMessageBox messageBox)
    {
        ThrowIf.Argument.IsNull(service, nameof(service));
        ThrowIf.Argument.IsNull(messageBox, nameof(messageBox));
        _service = service;
        _messageBox = messageBox;
    }

    public string Group => HierarchyTestConstants.CommandGroup;
    public string Name => "CW_HIERARCHY_TEST_GENERATE3D";
    public CadCommandTypeEnum Type => CadCommandTypeEnum.CmdSession;
    public void Invoke()
    {
        try
        {
            _service.Export();
        }
        catch (Exception exception)
        {
            _messageBox.Show(exception.Message, "Тестовая выгрузка", MessageBoxImage.Error);
        }
    }
}

public sealed class CwHierarchyTestGenerate3DAllCommand : ICadCommand
{
    private readonly HierarchyTestExportService _service;
    private readonly IDialogStackMessageBox _messageBox;

    public CwHierarchyTestGenerate3DAllCommand(HierarchyTestExportService service, IDialogStackMessageBox messageBox)
    {
        ThrowIf.Argument.IsNull(service, nameof(service));
        ThrowIf.Argument.IsNull(messageBox, nameof(messageBox));
        _service = service;
        _messageBox = messageBox;
    }

    public string Group => HierarchyTestConstants.CommandGroup;
    public string Name => "CW_HIERARCHY_TEST_GENERATE3D_ALL";
    public CadCommandTypeEnum Type => CadCommandTypeEnum.CmdSession;

    public void Invoke()
    {
        try
        {
            _service.ExportAll();
        }
        catch (Exception exception)
        {
            _messageBox.Show(exception.Message, "Автоматическая тестовая выгрузка", MessageBoxImage.Error);
        }
    }
}

public sealed class CwHierarchyTestValidateCommand : ICadCommand
{
    private readonly HierarchyTestValidationService _service;
    private readonly IDialogStackMessageBox _messageBox;

    public CwHierarchyTestValidateCommand(HierarchyTestValidationService service, IDialogStackMessageBox messageBox)
    {
        ThrowIf.Argument.IsNull(service, nameof(service));
        ThrowIf.Argument.IsNull(messageBox, nameof(messageBox));
        _service = service;
        _messageBox = messageBox;
    }

    public string Group => HierarchyTestConstants.CommandGroup;
    public string Name => "CW_HIERARCHY_TEST_VALIDATE";
    public CadCommandTypeEnum Type => CadCommandTypeEnum.CmdSession;
    public void Invoke()
    {
        try
        {
            _service.Validate();
        }
        catch (Exception exception)
        {
            _messageBox.Show(exception.Message, "Проверка стенда", MessageBoxImage.Error);
        }
    }
}
