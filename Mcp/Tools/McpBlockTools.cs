using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Guard;
using MyPlugin.Mcp.Protocol;
using Newtonsoft.Json.Linq;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using CadApplication = HostMgd.ApplicationServices.Application;
using CadDocument = HostMgd.ApplicationServices.Document;

namespace MyPlugin.Mcp.Tools;

/// <summary>Правка определений блоков в закрытых DWG, без изменения проекта BIM.</summary>
internal sealed class McpBlockTools
{
    private readonly McpLog _log;

    public McpBlockTools(McpLog log)
    {
        ThrowIf.Argument.IsNull(log, nameof(log));
        _log = log;
    }

    public void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("drawing_open", "Открыть DWG на экране nanoCAD без подключения к BIM-проекту. Уже открытый файл активируется. По умолчанию только для чтения; файл автоматически не сохраняется.",
            new() { ["file"] = McpSchema.String("Полный путь DWG."), ["readOnly"] = McpSchema.Boolean("Только чтение; по умолчанию true. Для уже открытого документа режим не меняется.") },
            new[] { "file" }, OpenDrawing);
        tools.Add("block_preview", "Показать определение блока крупно в отдельном чертеже-копии. Исходный DWG должен быть закрыт; он не изменяется. Копия в runtime/block-previews открывается только для чтения, не входит в BIM-проект. Это просмотр, не редактор исходного блока.",
            new() { ["file"] = McpSchema.String("Полный путь закрытого DWG."), ["name"] = McpSchema.String("Имя блока из blocks_find/ugo_search.") },
            new[] { "file", "name" }, Preview);
        tools.Add("blocks_find", "Определения блоков в закрытом DWG: имена и число объектов. file берётся, например, из ugo_search. Не открывает и не меняет проект.",
            new() { ["file"] = McpSchema.String("Полный путь DWG."), ["query"] = McpSchema.String("Часть имени блока."),
                ["offset"] = McpSchema.Integer("По умолчанию 0."), ["limit"] = McpSchema.Integer("По умолчанию 20, максимум 100.") },
            new[] { "file" }, Find);
        tools.Add("block_get", "Геометрия определения блока в закрытом DWG. Координаты локальные, не координаты вставок на плане. Возвращает handles для точечного удаления/копирования; выдача постраничная.",
            new() { ["file"] = McpSchema.String("Полный путь DWG."), ["name"] = McpSchema.String("Имя блока из blocks_find/ugo_search."),
                ["offset"] = McpSchema.Integer("По умолчанию 0."), ["limit"] = McpSchema.Integer("По умолчанию 20, максимум 100.") },
            new[] { "file", "name" }, Get);
        tools.Add("block_update", "Добавить примитивы или удалить выбранную геометрию внутри существующего блока закрытого DWG. Не меняет имя, точку вставки и метаданные блока. Сохраняет файл с резервной копией; существующие планы затем требуют обновления УГО.",
            new() { ["file"] = McpSchema.String("Полный путь закрытого DWG."), ["name"] = McpSchema.String("Имя существующего блока."),
                ["add"] = McpBlockGeometry.Schema(), ["remove"] = HandlesSchema("Handles из block_get, только геометрия этого блока; атрибуты удалять нельзя.") },
            new[] { "file", "name" }, Update, changesModel: true);
        tools.Add("block_geometry_copy", "Скопировать выбранные примитивы в существующий блок ДРУГОГО закрытого DWG. Добавляет геометрию, не заменяет определение/метаданные УГО. Базовые точки совмещаются. Слои с теми же именами берутся из назначения. Сохраняет назначение с резервной копией.",
            new() { ["sourceFile"] = McpSchema.String("Полный путь исходного DWG."), ["sourceName"] = McpSchema.String("Блок-источник."),
                ["handles"] = HandlesSchema("Handles геометрии из block_get/block_update, включая дуги и штриховки. 1–100. Для ассоциативной штриховки нужны также все объекты её границы."),
                ["file"] = McpSchema.String("Полный путь другого DWG."), ["name"] = McpSchema.String("Существующий блок назначения.") },
            new[] { "sourceFile", "sourceName", "handles", "file", "name" }, CopyGeometry, changesModel: true);
    }

    private static JObject HandlesSchema(string description) => new()
    {
        ["type"] = "array", ["items"] = McpSchema.String("Шестнадцатеричный handle."),
        ["maxItems"] = 100, ["description"] = description
    };

    private static object OpenDrawing(JObject args)
    {
        var file = GetFile(args.Value<string>("file")!);
        var readOnly = args.Value<bool?>("readOnly") ?? true;
        var documents = CadApplication.DocumentManager;
        var document = documents.Cast<CadDocument>().FirstOrDefault(x => string.Equals(Path.GetFullPath(x.Name), file, StringComparison.OrdinalIgnoreCase));
        var alreadyOpen = document != null;
        document ??= documents.Open(file, readOnly);
        documents.MdiActiveDocument = document;
        return new { file, alreadyOpen, readOnly = document.IsReadOnly };
    }

    private object Preview(JObject args)
    {
        var file = GetFile(args.Value<string>("file")!);
        var name = args.Value<string>("name")!;
        var directory = Path.Combine(McpSettings.Load().RuntimeDirectory, "block-previews");
        Directory.CreateDirectory(directory);
        var previewFile = Path.Combine(directory, "block-" + Guid.NewGuid().ToString("N") + ".dwg");
        using (var source = Open(file))
        using (var transaction = source.TransactionManager.StartTransaction())
        {
            var block = GetBlock(source, transaction, name);
            // Wblock переносит геометрию определения в модель отдельного DWG, не меняя исходную базу.
            _log.Write("block.preview.extract", new { file, name });
            using var preview = source.Wblock(block.ObjectId);
            _log.Write("block.preview.save", new { previewFile });
            preview.SaveAs(previewFile, source.OriginalFileVersion);
        }
        var documents = CadApplication.DocumentManager;
        _log.Write("block.preview.open", new { previewFile });
        var document = documents.Open(previewFile, true);
        _log.Write("block.preview.opened", new { previewFile });
        // Новый DWG не входит в блокировки документов BIM-проекта, взятые диспетчером MCP.
        using var documentLock = document.LockDocument();
        documents.MdiActiveDocument = document;
        ZoomToDrawing(document);
        _log.Write("block.preview.ready", new { previewFile });
        return new { file = previewFile, sourceFile = file, name, readOnly = true };
    }

    private void ZoomToDrawing(CadDocument document)
    {
        var database = document.Database;
        _log.Write("block.preview.extents", new { file = document.Name });
        database.UpdateExt(true);
        _log.Write("block.preview.extentsReady", new { file = document.Name });
        var min = database.Extmin;
        var max = database.Extmax;
        if (!double.IsFinite(min.X) || !double.IsFinite(min.Y) || !double.IsFinite(max.X) || !double.IsFinite(max.Y) || min.X > max.X || min.Y > max.Y) { return; }
        using var view = document.Editor.GetCurrentView();
        var aspect = view.Width / view.Height;
        var width = Math.Max(max.X - min.X, 1);
        var height = Math.Max(max.Y - min.Y, 1);
        view.Target = new Point3d((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        view.ViewDirection = Vector3d.ZAxis;
        view.ViewTwist = 0;
        view.CenterPoint = Point2d.Origin;
        view.Height = Math.Max(height, width / aspect) * 1.2;
        view.Width = view.Height * aspect;
        _log.Write("block.preview.setView", new { file = document.Name });
        document.Editor.SetCurrentView(view);
    }

    private static object Find(JObject args)
    {
        using var database = Open(args.Value<string>("file")!);
        using var transaction = database.TransactionManager.StartTransaction();
        var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var query = args.Value<string>("query") ?? "";
        var blocks = table.Cast<ObjectId>().Select(id => (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead))
            .Where(x => !x.IsLayout && !x.IsAnonymous && !x.IsFromExternalReference && x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name).ToArray();
        var paging = Page(args);
        var offset = paging.Offset;
        var limit = paging.Limit;
        var page = blocks.Skip(offset).Take(limit).Select(x => new { name = x.Name, count = x.Cast<ObjectId>().Count() }).ToArray();
        return new { total = blocks.Length, nextOffset = Next(offset, page.Length, blocks.Length), items = page };
    }

    private static object Get(JObject args)
    {
        using var database = Open(args.Value<string>("file")!);
        using var transaction = database.TransactionManager.StartTransaction();
        var block = GetBlock(database, transaction, args.Value<string>("name")!);
        var ids = block.Cast<ObjectId>().ToArray();
        var paging = Page(args);
        var offset = paging.Offset;
        var limit = paging.Limit;
        var page = ids.Skip(offset).Take(limit).Select(id => McpBlockGeometry.Describe((Entity)transaction.GetObject(id, OpenMode.ForRead))).ToArray();
        return new { name = block.Name, origin = McpBlockGeometry.Point(block.Origin), total = ids.Length,
            nextOffset = Next(offset, page.Length, ids.Length), items = page };
    }

    private object Update(JObject args)
    {
        if (args["add"] != null && args["add"] is not JArray) { throw new ArgumentException("add: нужен массив примитивов."); }
        var additions = args["add"] as JArray ?? new JArray();
        var removals = ReadHandles(args, "remove");
        if (additions.Count > 100 || additions.Any(x => x is not JObject) || additions.Count + removals.Length == 0)
        {
            throw new ArgumentException("Передайте add (до 100 примитивов) и/или remove (до 100 handles).");
        }
        var file = GetFile(args.Value<string>("file")!);
        using var database = Open(file);
        var added = new List<string>();
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var block = GetBlock(database, transaction, args.Value<string>("name")!);
            foreach (var entity in SelectGeometry(database, transaction, block, removals))
            {
                entity.UpgradeOpen();
                entity.Erase();
            }
            block.UpgradeOpen();
            foreach (var item in additions.Cast<JObject>())
            {
                using var entity = McpBlockGeometry.Create(item);
                entity.SetDatabaseDefaults(database);
                entity.Layer = item.Value<string>("layer") ?? "0";
                var id = block.AppendEntity(entity);
                transaction.AddNewlyCreatedDBObject(entity, true);
                if (entity is Hatch hatch)
                {
                    _log.Write("block.update.hatch", new { file, handle = id.Handle.ToString() });
                    McpBlockHatch.Fill(hatch, item, transaction);
                    _log.Write("block.update.hatchReady", new { file, handle = id.Handle.ToString() });
                }
                added.Add(id.Handle.ToString());
            }
            transaction.Commit();
        }
        var backup = Save(database, file);
        return new { file, added, removed = removals.Length, backup };
    }

    private static object CopyGeometry(JObject args)
    {
        var sourceFile = GetFile(args.Value<string>("sourceFile")!);
        var file = GetFile(args.Value<string>("file")!);
        if (string.Equals(sourceFile, file, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Для копирования между базами укажите два разных DWG.");
        }
        var handles = ReadHandles(args, "handles");
        if (handles.Length == 0) { throw new ArgumentException("Нужен хотя бы один handle."); }
        using var source = Open(sourceFile);
        using var target = Open(file);
        var added = new List<string>();
        using (var sourceTransaction = source.TransactionManager.StartTransaction())
        using (var targetTransaction = target.TransactionManager.StartTransaction())
        {
            var sourceBlock = GetBlock(source, sourceTransaction, args.Value<string>("sourceName")!);
            var targetBlock = GetBlock(target, targetTransaction, args.Value<string>("name")!);
            var entities = SelectGeometry(source, sourceTransaction, sourceBlock, handles);
            var selectedIds = entities.Select(x => x.ObjectId).ToHashSet();
            if (entities.OfType<Hatch>().Where(x => x.Associative)
                .Any(x => x.GetAssociatedObjectIds().Cast<ObjectId>().Any(id => !selectedIds.Contains(id))))
            {
                throw new ArgumentException("Для ассоциативной штриховки выберите также все объекты её границы; отдельно скопировать её нельзя.");
            }
            var ids = new ObjectIdCollection(entities.Select(x => x.ObjectId).ToArray());
            using var mapping = new IdMapping();
            source.WblockCloneObjects(ids, targetBlock.ObjectId, mapping, DuplicateRecordCloning.Ignore, false);
            var displacement = Matrix3d.Displacement(targetBlock.Origin - sourceBlock.Origin);
            foreach (var entity in entities)
            {
                var id = mapping[entity.ObjectId].Value;
                var clone = (Entity)targetTransaction.GetObject(id, OpenMode.ForWrite);
                clone.TransformBy(displacement);
                added.Add(id.Handle.ToString());
            }
            targetTransaction.Commit();
        }
        var backup = Save(target, file);
        return new { file, added, backup };
    }

    private static Entity[] SelectGeometry(
        Database database,
        Transaction transaction,
        BlockTableRecord block,
        string[] handles)
    {
        return handles.Select(handle =>
        {
            var id = database.GetObjectId(false, new Handle(long.Parse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0);
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity || entity.OwnerId != block.ObjectId || !McpBlockGeometry.IsSupported(entity))
            {
                throw new ArgumentException($"Handle {handle}: нужна геометрия выбранного блока (линия, окружность, дуга, полилиния, текст или штриховка), не атрибут.");
            }
            return entity;
        }).ToArray();
    }

    private static BlockTableRecord GetBlock(Database database, Transaction transaction, string name)
    {
        var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        if (!table.Has(name)) { throw new ArgumentException($"Блок «{name}» не найден."); }
        var block = (BlockTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
        if (block.IsLayout || block.IsFromExternalReference)
        {
            throw new ArgumentException("Нужно определение блока, не пространство модели/листа и не внешняя ссылка.");
        }
        return block;
    }

    private static string[] ReadHandles(JObject args, string name)
    {
        if (args[name] == null) { return Array.Empty<string>(); }
        if (args[name] is not JArray items || items.Count > 100 || items.Any(x => x.Type != JTokenType.String))
        {
            throw new ArgumentException($"{name}: нужен массив до 100 строк handles.");
        }
        return items.Values<string>().Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Database Open(string file)
    {
        file = GetFile(file);
        if (CadApplication.DocumentManager.Cast<CadDocument>().Any(x => string.Equals(Path.GetFullPath(x.Name), file, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Этот DWG открыт в nanoCAD. Закройте его перед чтением/изменением файла через block-инструменты.");
        }
        var database = new Database(false, true);
        try
        {
            // Для записи исходник должен оставаться неизменным; удерживаем запрет внешней записи до Save.
            database.ReadDwgFile(file, FileShare.Read, false, "");
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static string GetFile(string file)
    {
        if (!Path.IsPathFullyQualified(file) || !string.Equals(Path.GetExtension(file), ".dwg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Укажите полный путь DWG.");
        }
        file = Path.GetFullPath(file);
        if (!File.Exists(file)) { throw new FileNotFoundException("DWG не найден.", file); }
        return file;
    }

    private static string Save(Database database, string file)
    {
        var backupDirectory = Path.Combine(McpSettings.Load().RuntimeDirectory, "dwg-backups");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, Path.GetFileNameWithoutExtension(file) + "-" + Guid.NewGuid().ToString("N") + ".dwg");
        File.Copy(file, backup);
        database.CloseInput(true);
        try
        {
            database.SaveAs(file, database.OriginalFileVersion);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Не удалось сохранить DWG. Резервная копия исходника: {backup}", exception);
        }
        return backup;
    }

    private static (int Offset, int Limit) Page(JObject args) =>
        (Math.Max(0, args.Value<int?>("offset") ?? 0), Math.Clamp(args.Value<int?>("limit") ?? 20, 1, 100));

    private static int? Next(int offset, int count, int total) => offset + count < total ? offset + count : null;
}
