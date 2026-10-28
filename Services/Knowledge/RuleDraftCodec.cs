using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public static class RuleDraftCodec {
    public static RuleDraft FromRule(KnowledgeRule rule) {
        RuleDraft draft = new RuleDraft {
            Id = rule.Id,
            Title = rule.Title,
            PriorityText = rule.Priority.ToString(CultureInfo.InvariantCulture),
            Explanation = rule.Explanation,
            Provenance = rule.Provenance,
            Root = Read(rule.Condition)
        };
        foreach (Conclusion item in rule.Conclusions) draft.Conclusions.Add(new ConclusionDraft { FactId = item.FactId, ValueText = item.Value.Canonical });
        return draft;
    }

    public static JsonObject Write(RuleDraft draft, KnowledgeBase knowledge) {
        KnowledgeJson.Require(int.TryParse(draft.PriorityText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _), "приоритет должен быть Int32");
        int nodes = 0;
        JsonObject condition = WriteCondition(draft.Root, knowledge, new HashSet<ConditionDraft>(), 1, ref nodes);
        JsonArray conclusions = new JsonArray();
        foreach (ConclusionDraft item in draft.Conclusions) {
            KnowledgeJson.Require(knowledge.Facts.TryGetValue(item.FactId, out FactDefinition? fact) && fact.Role != FactRole.Input,
                "заключение должно ссылаться на derived/goal: " + item.FactId);
            conclusions.Add(new JsonObject { ["fact_id"] = item.FactId, ["value"] = Value(item.ValueText, fact!) });
        }
        return new JsonObject {
            ["id"] = draft.Id,
            ["title"] = draft.Title,
            ["priority"] = int.Parse(draft.PriorityText, CultureInfo.InvariantCulture),
            ["condition"] = condition,
            ["conclusions"] = conclusions,
            ["explanation"] = draft.Explanation,
            ["provenance"] = draft.Provenance
        };
    }

    public static string DefaultValue(FactDefinition fact) => fact.Type switch {
        FactType.Boolean => "true",
        FactType.Enum => fact.Options[0],
        _ => (fact.Minimum ?? 0).ToString(CultureInfo.InvariantCulture)
    };

    private static ConditionDraft Read(Condition condition) {
        if (condition is ComparisonCondition comparison)
            return new ConditionDraft(DraftConditionKind.Comparison) { FactId = comparison.FactId, Operation = comparison.Operator, ValueText = comparison.Expected.Canonical };
        ConditionDraft group = new ConditionDraft(condition is AllCondition ? DraftConditionKind.All : DraftConditionKind.Any);
        foreach (Condition child in condition is AllCondition all ? all.Items : ((AnyCondition)condition).Items) group.Children.Add(Read(child));
        return group;
    }

    // из дерева формы собираем JSON: сравнение завершает рекурсию, группа объединяет результаты детей
    // path хранит узлы текущей ветви и обнаруживает цикл, nodes считает все уже обработанные узлы условия
    // значения проверяем по словарю до записи, чтобы bool и числа не стали строками и не потеряли точность
    private static JsonObject WriteCondition(ConditionDraft node, KnowledgeBase knowledge, HashSet<ConditionDraft> path, int depth, ref int nodes) {
        KnowledgeJson.Require(depth <= 16 && ++nodes <= 4096 && path.Add(node), "дерево условия: цикл, глубина больше 16 или больше 4096 узлов");
        JsonObject result;
        if (node.IsComparison) {
            KnowledgeJson.Require(node.Children.Count == 0, "у сравнения не должно быть дочерних условий");
            KnowledgeJson.Require(knowledge.Facts.TryGetValue(node.FactId, out FactDefinition? fact), "неизвестный признак: " + node.FactId);
            result = new JsonObject { ["fact_id"] = node.FactId, ["op"] = node.Operation, ["value"] = Value(node.ValueText, fact!) };
        }
        else {
            KnowledgeJson.Require(node.Children.Count is > 0 and <= 128, "группа И/ИЛИ должна иметь 1..128 условий");
            JsonArray items = new JsonArray();
            foreach (ConditionDraft child in node.Children) items.Add(WriteCondition(child, knowledge, path, depth + 1, ref nodes));
            result = new JsonObject { [node.Kind == DraftConditionKind.All ? "all" : "any"] = items };
        }
        path.Remove(node);
        return result;
    }

    private static JsonNode Value(string text, FactDefinition fact) {
        string json = fact.Type == FactType.Enum ? JsonSerializer.Serialize(text) : (text ?? "").Trim();
        using JsonDocument document = KnowledgeJson.Parse(Encoding.UTF8.GetBytes(json));
        KnowledgeValue value = KnowledgeJson.Value(document.RootElement, fact, fact.Id);
        return fact.Type == FactType.Enum ? JsonValue.Create(value.Canonical)! : JsonNode.Parse(value.Canonical)!;
    }
}
