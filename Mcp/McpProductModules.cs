using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;

namespace MyPlugin.Mcp;

internal static class McpProductModules
{
    internal static IReadOnlyList<IMcpToolProvider> Load()
    {
        // Только модуль работающего продукта: остальные продуктовые зависимости не загружаем.
        var assembly = ProductModules.Load();
        if (assembly == null)
        {
            return Array.Empty<IMcpToolProvider>();
        }

        var providers = assembly.GetExportedTypes()
            .Where(x => !x.IsAbstract && typeof(IMcpToolProvider).IsAssignableFrom(x))
            .Select(x => (IMcpToolProvider)Activator.CreateInstance(x)!).ToArray();
        if (providers.Length == 0)
        {
            var context = AssemblyLoadContext.GetLoadContext(typeof(McpProductModules).Assembly)!;
            throw new InvalidOperationException($"В модуле {assembly.Location} не найден совместимый {nameof(IMcpToolProvider)}. Контекст загрузки: {context.Name ?? "без имени"}.");
        }
        return providers;
    }
}
