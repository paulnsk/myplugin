using Guard;
using Nanosoft.Acad;
using MyPlugin.Mcp;
using Nanosoft.Data;
using Nanosoft.ObjectLib.ESN.Modul;
using Nanosoft.Utils;
using System;

namespace MyPlugin.Electrica;

public sealed class ElectricaDatabaseBinder : IMcpDatabaseBinder
{
    public bool Supports(Type objectType)
    {
        ThrowIf.Argument.IsNull(objectType, nameof(objectType));
        return typeof(ElSwitch).IsAssignableFrom(objectType) || typeof(ElSocket).IsAssignableFrom(objectType) ||
               typeof(IFiderBus).IsAssignableFrom(objectType);
    }

    public bool Bind(IAcadObject obj, IDbObject product)
    {
        ThrowIf.Argument.IsNull(obj, nameof(obj));
        ThrowIf.Argument.IsNull(product, nameof(product));
        var setter = CwPlatform.ResolveNotNull<IElBusTypeSetter>() as ElBusTypeSetter
                     ?? throw new InvalidOperationException("Установленный обработчик привязки БД не поддерживается MCP-адаптером.");
        var changer = ElectricaPrivateOperations.CreateBusChanger(setter, obj);
        if (!changer.IsSuitable(product))
        {
            throw new ArgumentException("Изделие не подходит к существующей структуре объекта.");
        }
        changer.Change(product);
        // Штатный обработчик шин сам делает Load/Save. Выключатели/розетки только согласуют секции.
        if (obj is IFiderBus)
        {
            return true;
        }
        obj.Load();
        return false;
    }
}
