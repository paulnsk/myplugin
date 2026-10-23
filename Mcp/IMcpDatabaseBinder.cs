using Nanosoft.Acad;
using Nanosoft.Data;
using System;

namespace MyPlugin.Mcp;

/// <summary>Штатная продуктовая привязка изделия, включая изменение внутренней структуры объекта.</summary>
public interface IMcpDatabaseBinder
{
    bool Supports(Type objectType);

    /// <returns>Обработчик уже сохранил объект. Иначе сохранение выполняет вызывающая операция.</returns>
    bool Bind(IAcadObject obj, IDbObject product);
}
