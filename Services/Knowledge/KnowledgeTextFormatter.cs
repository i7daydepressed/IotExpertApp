using System;
using System.Globalization;
using System.Linq;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public static class KnowledgeTextFormatter {
    public static string Name(KnowledgeBase knowledge, string id) => knowledge.Facts.TryGetValue(id, out FactDefinition? fact) ? id + " · " + fact.Name : id + " · признак отсутствует в словаре";
    public static string Value(KnowledgeValue value) => value.Type == FactType.Boolean ? value.Canonical == "true" ? "да" : "нет" : value.Canonical;
    public static string Condition(KnowledgeBase knowledge, Condition condition, int depth = 0) {
        string indent = new string(' ', depth * 2);
        // каждую группу оставляем отдельной парой скобок: A И (B ИЛИ C) нельзя превратить в плоский список
        return condition switch {
            AllCondition all => Group("ВСЕ условия (И)", all.Items),
            AnyCondition any => Group("ХОТЯ БЫ ОДНО (ИЛИ)", any.Items),
            ComparisonCondition comparison => indent + Name(knowledge, comparison.FactId) + " " + Operator(comparison.Operator) + " " + Value(comparison.Expected),
            _ => throw new ArgumentException("Неизвестный тип условия")
        };
        string Group(string title, System.Collections.Immutable.ImmutableArray<Condition> items) => indent + title + " (\n"
            + string.Join("\n", items.Select(item => Condition(knowledge, item, depth + 1))) + "\n" + indent + ")";
    }
    public static string Rule(KnowledgeBase knowledge, KnowledgeRule rule) => "ЕСЛИ\n" + Condition(knowledge, rule.Condition)
        + "\nТО\n" + string.Join("\n", rule.Conclusions.Select(c => Name(knowledge, c.FactId) + " = " + Value(c.Value)))
        + "\n\nПояснение: " + rule.Explanation + "\nПроисхождение: " + rule.Provenance;
    public static string Fact(FactDefinition fact) => "Тип: " + (fact.Type switch { FactType.Boolean => "логический (да/нет)", FactType.Enum => "перечисление", FactType.Integer => "целое число", _ => "число" })
        + "; роль: " + (fact.Role switch { FactRole.Input => "вход", FactRole.Derived => "промежуточный вывод", _ => "итоговый вывод" })
        + (fact.Options.Length > 0 ? "\nВарианты: " + string.Join(", ", fact.Options) : "")
        + (fact.Minimum.HasValue || fact.Maximum.HasValue ? "\nДиапазон: " + (fact.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "без нижней границы") + " … " + (fact.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "без верхней границы") : "")
        + (string.IsNullOrWhiteSpace(fact.Question) ? "" : "\nВопрос: " + fact.Question);
    public static string Outcome(ConsultationOutcome outcome) => outcome.Description + "\nВид: " + (outcome.Kind switch {
        ConsultationOutcomeKind.Strategy => "стратегия / узкое подтверждение", ConsultationOutcomeKind.Clarification => "уточнение", _ => "отказ / ограничение" }) + " (" + outcome.Kind + ")";
    public static string Status(InferenceStatus status) => status switch {
        InferenceStatus.Success => "Цель вывода получена", InferenceStatus.InsufficientData => "Недостаточно данных",
        InferenceStatus.NoMatch => "Нет подходящего правила", InferenceStatus.Conflict => "Обнаружен конфликт", _ => "Достигнут предел вывода"
    };
    public static string Truth(TruthValue value) => value switch { TruthValue.True => "истина", TruthValue.False => "ложь", _ => "неизвестно" };
    internal static string Operator(string op) => op switch { "eq" => "= (равно)", "ne" => "≠ (не равно)", "gt" => "> (больше)", "gte" => "≥ (не меньше)", "lt" => "< (меньше)", "lte" => "≤ (не больше)", _ => op + " (оператор)" };
}
