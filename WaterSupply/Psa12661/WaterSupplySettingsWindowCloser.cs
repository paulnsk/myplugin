using System;
using System.Linq;
using System.Windows.Forms;
using Nanosoft.Platform.FormExtension;
using Nanosoft.Utils;

namespace MyPlugin.WaterSupply.Psa12661;

/// <summary>
/// Закрывает окно настроек, которое ВК открывает при создании проекта (AddIn.ProjectEvents_ProjectCreated →
/// ShowSettings(WaterConsumerControl)). Тихого режима у этого вызова нет: ни он, ни ShowDialogOnOwner
/// не смотрят CwPlatform.Silent. Закрытие равносильно кнопке «Закрыть»: настройки сохраняются штатно.
/// </summary>
internal sealed class WaterSupplySettingsWindowCloser : IDisposable
{
    private const string SettingsFormType = "Nanosoft.Settings.SettingsForm";

    private readonly Timer _timer = new() { Interval = 200 };
    private readonly IDialogStack _dialogs = CwPlatform.ResolveNotNull<IDialogStack>();

    internal WaterSupplySettingsWindowCloser()
    {
        // Таймер WinForms срабатывает и внутри модального цикла ShowDialog.
        _timer.Tick += (_, _) =>
        {
            // Окно лежит на стеке диалогов продукта; OpenForms — запасной путь, если стек отдал не его.
            var form = Control.FromHandle(_dialogs.GetCurrent()) as Form;
            if (form?.GetType().FullName != SettingsFormType)
            {
                form = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.GetType().FullName == SettingsFormType);
            }
            if (form != null)
            {
                form.Close();
                Closed = true;
            }
        };
        _timer.Start();
    }

    internal bool Closed { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
