using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Nanosoft.Studio.Utils;
using Nanosoft.Utils;

namespace MyPlugin.Mcp;

internal static class McpProductModules
{
    internal static IReadOnlyList<IMcpToolProvider> Load()
    {
        var product = CwPlatform.ResolveNotNull<IApplicationInformation>().ApplicationType;
        var path = Path.Combine(Path.GetDirectoryName(typeof(McpProductModules).Assembly.Location)!, $"MyPlugin.{product}.dll");
        if (!File.Exists(path))
        {
            return Array.Empty<IMcpToolProvider>();
        }

        // Только модуль работающего продукта: остальные продуктовые зависимости не загружаем.
        var context = AssemblyLoadContext.GetLoadContext(typeof(McpProductModules).Assembly)!;
        var assembly = context.LoadFromAssemblyPath(path);
        var providers = assembly.GetExportedTypes()
            .Where(x => !x.IsAbstract && typeof(IMcpToolProvider).IsAssignableFrom(x))
            .Select(x => (IMcpToolProvider)Activator.CreateInstance(x)!).ToArray();
        if (providers.Length == 0)
        {
            throw new InvalidOperationException($"В модуле {path} не найден совместимый {nameof(IMcpToolProvider)}. Контекст загрузки: {context.Name ?? "без имени"}.");
        }
        return providers;
    }
}
