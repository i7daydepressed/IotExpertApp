using System;
using System.Collections.Immutable;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public sealed class InputFactService {
    public ImmutableDictionary<string, InputFact> Load(string path, KnowledgeBase knowledgeBase)
        => Parse(KnowledgeJson.ReadBounded(path), knowledgeBase);

    public ImmutableDictionary<string, InputFact> Parse(byte[] bytes, KnowledgeBase knowledgeBase) {
        using JsonDocument document = KnowledgeJson.Parse(bytes);
        JsonElement root = document.RootElement;
        KnowledgeJson.Shape(root, "вход", new[] { "schema_version", "facts" });
        JsonElement schema = root.GetProperty("schema_version");
        KnowledgeJson.Require(schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int version) && version == 1, "версия входных фактов должна быть 1");
        JsonElement items = root.GetProperty("facts");
        KnowledgeJson.Require(items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= 128, "facts должен быть массивом до 128 элементов");
        ImmutableDictionary<string, InputFact>.Builder facts = ImmutableDictionary.CreateBuilder<string, InputFact>(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray()) {
            KnowledgeJson.Shape(item, "факт", new[] { "fact_id", "value", "origin" });
            string id = KnowledgeJson.Id(item.GetProperty("fact_id"), "input.fact_id");
            KnowledgeJson.Require(knowledgeBase.Facts.TryGetValue(id, out FactDefinition? definition) && definition.Role == FactRole.Input,
                "можно задавать только объявленный входной признак: " + id);
            KnowledgeJson.Require(!facts.ContainsKey(id), "повтор входного факта " + id);
            JsonElement valueJson = item.GetProperty("value");
            KnowledgeValue? value = valueJson.ValueKind == JsonValueKind.Null ? null : KnowledgeJson.Value(valueJson, definition!, id);
            facts.Add(id, new InputFact(id, value, KnowledgeJson.Text(item.GetProperty("origin"), id + ".origin")));
        }
        return facts.ToImmutable();
    }
}
