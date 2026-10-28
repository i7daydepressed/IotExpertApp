using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public sealed class KnowledgeWorkspace {
    private byte[] baseline;
    private JsonObject document;
    private readonly Stack<byte[]> undo = new Stack<byte[]>();
    public string PathName { get; }
    public KnowledgeBase Knowledge { get; private set; }
    public bool IsDirty => undo.Count > 0;
    public bool CanUndo => undo.Count > 0;
    public bool IsWorkingLocation => IsOutsideProgram(PathName);

    private KnowledgeWorkspace(string path, byte[] bytes) {
        PathName = Path.GetFullPath(path);
        Knowledge = new KnowledgeBaseService().Parse(bytes);
        baseline = (byte[])bytes.Clone();
        document = ParseDocument(bytes);
    }

    public static KnowledgeWorkspace Load(string path) => new KnowledgeWorkspace(path, KnowledgeJson.ReadBounded(path));

    public void Apply(string? existingId, RuleDraft draft, string? version = null) {
        JsonObject candidate = document.DeepClone().AsObject();
        if (version is not null) candidate["version"] = version;
        JsonArray rules = candidate["rules"]!.AsArray();
        JsonObject rule = RuleDraftCodec.Write(draft, Knowledge);
        if (existingId is null) rules.Add(rule);
        else {
            KnowledgeJson.Require(draft.Id == existingId, "ID существующего правила сохраняется; для другого ID создайте новое правило");
            int index = Find(rules, existingId);
            KnowledgeJson.Require(index >= 0, "правило больше не найдено: " + existingId);
            rules[index] = rule;
        }
        Commit(candidate);
    }

    public void Delete(string id) {
        JsonObject candidate = document.DeepClone().AsObject();
        JsonArray rules = candidate["rules"]!.AsArray();
        int index = Find(rules, id);
        KnowledgeJson.Require(index >= 0, "правило не найдено: " + id);
        rules.RemoveAt(index);
        Commit(candidate);
    }

    public void SetVersion(string version) {
        if (version == Knowledge.Version) return;
        JsonObject candidate = document.DeepClone().AsObject();
        candidate["version"] = version;
        Commit(candidate);
    }

    private void Commit(JsonObject candidate) {
        byte[] next = Bytes(candidate);
        KnowledgeBase validated = new KnowledgeBaseService().Parse(next);
        // только проверенный полный документ заменяет черновик, файл здесь не меняется
        undo.Push(Bytes(document));
        document = candidate;
        Knowledge = validated;
    }

    public void Undo() {
        if (undo.Count == 0) return;
        byte[] previous = undo.Pop();
        if (undo.Count == 0) previous = baseline;
        document = ParseDocument(previous);
        Knowledge = new KnowledgeBaseService().Parse(previous);
    }

    public void Discard() {
        document = ParseDocument(baseline);
        Knowledge = new KnowledgeBaseService().Parse(baseline);
        undo.Clear();
    }

    public void EnsureDiskUnchanged() {
        byte[] current = KnowledgeJson.ReadBounded(PathName);
        if (!SHA256.HashData(current).SequenceEqual(SHA256.HashData(baseline)))
            throw new IOException("файл изменен извне; сохранение отменено, черновик сохранен в памяти");
    }

    // проверенный черновик сначала кладем во временный файл рядом с целевым, исходные байты сохраняем в backup
    // перед заменой еще раз сверяем SHA с загруженной версией: внешнюю правку не перезаписываем
    // baseline и undo меняем только после успешного rename; узкую гонку с другим писателем эта проверка не исключает
    public string Save() {
        RequireWorkingPath(PathName);
        EnsureDiskUnchanged();
        if (!IsDirty) return "";
        byte[] bytes = Bytes(document);
        KnowledgeBase validated = new KnowledgeBaseService().Parse(bytes);
        string directory = Path.GetDirectoryName(PathName)!;
        string temp = Path.Combine(directory, ".iot-knowledge-" + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = PathName + ".bak-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        try {
            WriteNew(temp, bytes);
            new KnowledgeBaseService().Load(temp);
            EnsureDiskUnchanged();
            WriteNew(backup, baseline);
            // оптимистическая проверка непосредственно перед rename, без обещания межпроцессной транзакции
            EnsureDiskUnchanged();
            File.Move(temp, PathName, true);
            baseline = bytes;
            Knowledge = validated;
            undo.Clear();
            return backup;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void CreateWorkingCopy(string source, string destination) {
        RequireWorkingPath(destination);
        byte[] bytes = KnowledgeJson.ReadBounded(source);
        new KnowledgeBaseService().Parse(bytes);
        string full = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".iot-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { WriteNew(temp, bytes); File.Move(temp, full, false); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void RequireWorkingPath(string path) {
        KnowledgeJson.Require(IsOutsideProgram(path), "поставляемую БЗ рядом с программой не перезаписываем; создайте рабочую копию в папке пользователя");
    }

    private static bool IsOutsideProgram(string path) {
        string relative = Path.GetRelativePath(AppContext.BaseDirectory, Path.GetFullPath(path));
        return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void WriteNew(string path, byte[] bytes) {
        using FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static JsonObject ParseDocument(byte[] bytes) {
        using JsonDocument checkedJson = KnowledgeJson.Parse(bytes);
        return JsonNode.Parse(checkedJson.RootElement.GetRawText())!.AsObject();
    }
    private static byte[] Bytes(JsonObject json) => Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }) + "\n");
    private static int Find(JsonArray rules, string id) {
        for (int index = 0; index < rules.Count; index++) if (rules[index]!["id"]!.GetValue<string>() == id) return index;
        return -1;
    }
}
