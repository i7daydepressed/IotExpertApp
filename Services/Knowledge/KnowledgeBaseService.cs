using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public sealed class KnowledgeBaseService {
    public KnowledgeBase Load(string path) => Parse(KnowledgeJson.ReadBounded(path));

    public KnowledgeBase Parse(byte[] bytes) {
        using JsonDocument document = KnowledgeJson.Parse(bytes);
        JsonElement root = document.RootElement;
        KnowledgeJson.Shape(root, "БЗ", new[] { "schema_version", "knowledge_base_id", "version", "synthetic", "facts", "rules" }, "question_order", "outcomes");
        JsonElement schemaJson = root.GetProperty("schema_version");
        KnowledgeJson.Require(schemaJson.ValueKind == JsonValueKind.Number && schemaJson.TryGetInt32(out _), "schema_version должна быть целым числом");
        int schema = schemaJson.GetInt32();
        KnowledgeJson.Require(schema is 1 or 2, "поддерживаются schema_version 1 и 2");
        string id = KnowledgeJson.Id(root.GetProperty("knowledge_base_id"), "knowledge_base_id");
        string version = KnowledgeJson.Text(root.GetProperty("version"), "version");
        JsonElement synthetic = root.GetProperty("synthetic");
        KnowledgeJson.Require(synthetic.ValueKind is JsonValueKind.True or JsonValueKind.False, "synthetic должен быть bool");
        JsonElement factsJson = root.GetProperty("facts");
        JsonElement rulesJson = root.GetProperty("rules");
        KnowledgeJson.Require(factsJson.ValueKind == JsonValueKind.Array && factsJson.GetArrayLength() is > 0 and <= 128, "нужно 1..128 признаков");
        KnowledgeJson.Require(rulesJson.ValueKind == JsonValueKind.Array && rulesJson.GetArrayLength() is > 0 and <= 256, "нужно 1..256 правил");
        ImmutableDictionary<string, FactDefinition>.Builder facts = ImmutableDictionary.CreateBuilder<string, FactDefinition>(StringComparer.Ordinal);
        foreach (JsonElement item in factsJson.EnumerateArray()) {
            FactDefinition definition = ReadDefinition(item);
            KnowledgeJson.Require(!facts.ContainsKey(definition.Id), "повтор признака " + definition.Id);
            facts.Add(definition.Id, definition);
        }
        KnowledgeJson.Require(facts.Values.Count(fact => fact.Role == FactRole.Goal) == 1, "БЗ должна содержать ровно один целевой признак");
        HashSet<string> ruleIds = new HashSet<string>(StringComparer.Ordinal);
        ImmutableArray<KnowledgeRule>.Builder rules = ImmutableArray.CreateBuilder<KnowledgeRule>();
        int nodes = 0;
        foreach (JsonElement item in rulesJson.EnumerateArray()) {
            KnowledgeJson.Shape(item, "правило", new[] { "id", "title", "priority", "condition", "conclusions", "explanation", "provenance" });
            string ruleId = KnowledgeJson.Id(item.GetProperty("id"), "rule.id");
            KnowledgeJson.Require(ruleIds.Add(ruleId), "повтор правила " + ruleId);
            JsonElement priorityJson = item.GetProperty("priority");
            KnowledgeJson.Require(priorityJson.ValueKind == JsonValueKind.Number && priorityJson.TryGetInt32(out _), ruleId + ": priority должен быть Int32");
            int priority = priorityJson.GetInt32();
            Condition condition = ReadCondition(item.GetProperty("condition"), facts, 1, ref nodes);
            JsonElement conclusionsJson = item.GetProperty("conclusions");
            KnowledgeJson.Require(conclusionsJson.ValueKind == JsonValueKind.Array && conclusionsJson.GetArrayLength() is > 0 and <= 128, ruleId + ": нужно 1..128 заключений");
            HashSet<string> targets = new HashSet<string>(StringComparer.Ordinal);
            ImmutableArray<Conclusion>.Builder conclusions = ImmutableArray.CreateBuilder<Conclusion>();
            foreach (JsonElement conclusion in conclusionsJson.EnumerateArray()) {
                KnowledgeJson.Shape(conclusion, ruleId + ".conclusion", new[] { "fact_id", "value" });
                string target = KnowledgeJson.Id(conclusion.GetProperty("fact_id"), "conclusion.fact_id");
                KnowledgeJson.Require(facts.ContainsKey(target), ruleId + ": неизвестный признак " + target);
                FactDefinition definition = facts[target];
                KnowledgeJson.Require(definition.Role != FactRole.Input && targets.Add(target), ruleId + ": запись во входной или повтор заключения " + target);
                conclusions.Add(new Conclusion(target, KnowledgeJson.Value(conclusion.GetProperty("value"), definition, ruleId + ".value")));
            }
            rules.Add(new KnowledgeRule(ruleId, KnowledgeJson.Text(item.GetProperty("title"), ruleId + ".title"), priority,
                condition, conclusions.ToImmutable(), KnowledgeJson.Text(item.GetProperty("explanation"), ruleId + ".explanation"),
                KnowledgeJson.Text(item.GetProperty("provenance"), ruleId + ".provenance")));
        }
        ImmutableArray<string> questionOrder = facts.Values.Where(fact => fact.Role == FactRole.Input)
            .Select(fact => fact.Id).OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        ImmutableDictionary<string, ConsultationOutcome>.Builder outcomes = ImmutableDictionary.CreateBuilder<string, ConsultationOutcome>(StringComparer.Ordinal);
        if (schema == 1) {
            KnowledgeJson.Require(!root.TryGetProperty("question_order", out _) && !root.TryGetProperty("outcomes", out _),
                "question_order/outcomes требуют schema_version 2");
        }
        else {
            ReadConsultationMetadata(root, facts, out questionOrder, outcomes);
        }
        return new KnowledgeBase(schema, id, version, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            synthetic.GetBoolean(), facts.ToImmutable(), rules.ToImmutable()) {
            QuestionOrder = questionOrder,
            Outcomes = outcomes.ToImmutable()
        };
    }

    private static void ReadConsultationMetadata(JsonElement root, ImmutableDictionary<string, FactDefinition>.Builder facts,
        out ImmutableArray<string> questionOrder, ImmutableDictionary<string, ConsultationOutcome>.Builder outcomes) {
        KnowledgeJson.Require(root.TryGetProperty("question_order", out JsonElement order)
            && order.ValueKind == JsonValueKind.Array && order.GetArrayLength() <= 128, "схема 2 требует массив question_order");
        ImmutableArray<string>.Builder ordered = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement item in order.EnumerateArray()) {
            string factId = KnowledgeJson.Id(item, "question_order");
            KnowledgeJson.Require(facts.TryGetValue(factId, out FactDefinition? fact) && fact.Role == FactRole.Input,
                "question_order ссылается не на input: " + factId);
            KnowledgeJson.Require(!ordered.Contains(factId), "повтор question_order: " + factId);
            ordered.Add(factId);
        }
        KnowledgeJson.Require(ordered.Count == facts.Values.Count(fact => fact.Role == FactRole.Input),
            "question_order должен перечислить каждый input ровно один раз");
        questionOrder = ordered.ToImmutable();
        FactDefinition goal = facts.Values.Single(fact => fact.Role == FactRole.Goal);
        KnowledgeJson.Require(goal.Type == FactType.Enum, "схема 2 требует enum для единственной цели");
        KnowledgeJson.Require(root.TryGetProperty("outcomes", out JsonElement catalog)
            && catalog.ValueKind == JsonValueKind.Array && catalog.GetArrayLength() is > 0 and <= 128, "схема 2 требует непустой outcomes");
        foreach (JsonElement item in catalog.EnumerateArray()) {
            KnowledgeJson.Shape(item, "исход", new[] { "value", "kind", "title", "description" });
            string value = KnowledgeJson.Text(item.GetProperty("value"), "outcome.value");
            KnowledgeJson.Require(goal.Options.Contains(value, StringComparer.Ordinal) && !outcomes.ContainsKey(value),
                "повтор исхода или значение вне enum цели: " + value);
            ConsultationOutcomeKind kind = KnowledgeJson.Text(item.GetProperty("kind"), "outcome.kind") switch {
                "strategy" => ConsultationOutcomeKind.Strategy,
                "clarification" => ConsultationOutcomeKind.Clarification,
                "refusal" => ConsultationOutcomeKind.Refusal,
                _ => throw new System.IO.InvalidDataException("неизвестный kind исхода")
            };
            outcomes.Add(value, new ConsultationOutcome(value, kind,
                KnowledgeJson.Text(item.GetProperty("title"), "outcome.title"),
                KnowledgeJson.Text(item.GetProperty("description"), "outcome.description")));
        }
        KnowledgeJson.Require(outcomes.Count == goal.Options.Length, "outcomes должен описать все значения enum цели");
    }

    private static FactDefinition ReadDefinition(JsonElement item) {
        KnowledgeJson.Shape(item, "признак", new[] { "id", "name", "type", "role" }, "options", "min", "max", "question");
        string id = KnowledgeJson.Id(item.GetProperty("id"), "fact.id");
        FactType type = KnowledgeJson.Text(item.GetProperty("type"), id + ".type") switch {
            "bool" => FactType.Boolean,
            "enum" => FactType.Enum,
            "integer" => FactType.Integer,
            "number" => FactType.Number,
            _ => throw new System.IO.InvalidDataException(id + ": неизвестный тип")
        };
        FactRole role = KnowledgeJson.Text(item.GetProperty("role"), id + ".role") switch {
            "input" => FactRole.Input,
            "derived" => FactRole.Derived,
            "goal" => FactRole.Goal,
            _ => throw new System.IO.InvalidDataException(id + ": неизвестная роль")
        };
        ImmutableArray<string>.Builder options = ImmutableArray.CreateBuilder<string>();
        if (type == FactType.Enum) {
            KnowledgeJson.Require(item.TryGetProperty("options", out JsonElement values) && values.ValueKind == JsonValueKind.Array
                && values.GetArrayLength() is > 0 and <= 128, id + ": нужен непустой options");
            foreach (JsonElement value in values.EnumerateArray()) options.Add(KnowledgeJson.Text(value, id + ".options"));
            KnowledgeJson.Require(options.Distinct(StringComparer.Ordinal).Count() == options.Count, id + ": повтор options");
        }
        else KnowledgeJson.Require(!item.TryGetProperty("options", out _), id + ": options разрешен только enum");
        decimal? minimum = null;
        decimal? maximum = null;
        if (item.TryGetProperty("min", out JsonElement min)) minimum = KnowledgeJson.Number(min, id + ".min");
        if (item.TryGetProperty("max", out JsonElement max)) maximum = KnowledgeJson.Number(max, id + ".max");
        KnowledgeJson.Require(type is FactType.Integer or FactType.Number || (!minimum.HasValue && !maximum.HasValue), id + ": диапазон только у чисел");
        KnowledgeJson.Require(!minimum.HasValue || !maximum.HasValue || minimum.Value <= maximum.Value, id + ": min больше max");
        KnowledgeJson.Require(type != FactType.Integer ||
            ((!minimum.HasValue || decimal.Truncate(minimum.Value) == minimum.Value) && (!maximum.HasValue || decimal.Truncate(maximum.Value) == maximum.Value)), id + ": границы integer должны быть целыми");
        string? question = item.TryGetProperty("question", out JsonElement questionJson) ? KnowledgeJson.Text(questionJson, id + ".question") : null;
        KnowledgeJson.Require(role == FactRole.Input ? question is not null : question is null, id + ": question нужен только входному признаку");
        return new FactDefinition(id, KnowledgeJson.Text(item.GetProperty("name"), id + ".name"), type, role,
            options.ToImmutable(), minimum, maximum, question);
    }

    // каждый узел либо группа, либо сравнение, смешанный объект отвергаем до построения дерева
    // дети группы разбираются тем же способом, сравнение завершает рекурсию и связывается с объявленным признаком
    private static Condition ReadCondition(JsonElement item, ImmutableDictionary<string, FactDefinition>.Builder facts, int depth, ref int nodes) {
        KnowledgeJson.Require(depth <= 16 && ++nodes <= 4096, "условия превышают глубину 16 или 4096 узлов на БЗ");
        KnowledgeJson.Require(item.ValueKind == JsonValueKind.Object, "условие должно быть объектом");
        if (item.TryGetProperty("all", out JsonElement all) || item.TryGetProperty("any", out _)) {
            bool conjunction = item.TryGetProperty("all", out all);
            string key = conjunction ? "all" : "any";
            KnowledgeJson.Shape(item, "группа", new[] { key });
            JsonElement children = conjunction ? all : item.GetProperty("any");
            KnowledgeJson.Require(children.ValueKind == JsonValueKind.Array && children.GetArrayLength() is > 0 and <= 128, "нужно 1..128 условий в группе");
            ImmutableArray<Condition>.Builder conditions = ImmutableArray.CreateBuilder<Condition>();
            foreach (JsonElement child in children.EnumerateArray()) conditions.Add(ReadCondition(child, facts, depth + 1, ref nodes));
            return conjunction ? new AllCondition(conditions.ToImmutable()) : new AnyCondition(conditions.ToImmutable());
        }
        KnowledgeJson.Shape(item, "сравнение", new[] { "fact_id", "op", "value" });
        string factId = KnowledgeJson.Id(item.GetProperty("fact_id"), "condition.fact_id");
        KnowledgeJson.Require(facts.ContainsKey(factId), "условие ссылается на неизвестный признак " + factId);
        FactDefinition definition = facts[factId];
        string operation = KnowledgeJson.Text(item.GetProperty("op"), "condition.op");
        KnowledgeJson.Require(operation is "eq" or "ne" or "gt" or "gte" or "lt" or "lte", "неизвестный оператор " + operation);
        KnowledgeJson.Require(operation is "eq" or "ne" || definition.Type is FactType.Integer or FactType.Number, "порядок разрешен только для чисел");
        return new ComparisonCondition(factId, operation, KnowledgeJson.Value(item.GetProperty("value"), definition, "condition.value"));
    }
}
