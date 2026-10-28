using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public sealed class ExplanationService {
    // берем правила, оценки и происхождение из завершенного снимка расчета, не читаем текущий файл БЗ заново
    // дерево раскрывает первое основание факта, остальные оставляет ссылками, лимиты сокращают только текст
    public string Describe(InferenceResult result) {
        StringBuilder output = new StringBuilder();
        int displayedNodes = 0;
        KnowledgeBase knowledge = result.KnowledgeBase;
        output.AppendLine(knowledge.Synthetic ? "ИСКУССТВЕННАЯ БЗ: проверка алгоритма, не диагностика ESP32" : "Внешняя база знаний");
        output.AppendLine($"БЗ: {knowledge.Id}, версия {knowledge.Version}, SHA256 {knowledge.Sha256}");
        output.AppendLine("Статус: " + (result.Status switch {
            InferenceStatus.Success => knowledge.Outcomes.Count > 0 ? "исход консультации доказан" : "цель доказана",
            InferenceStatus.InsufficientData => "недостаточно данных",
            InferenceStatus.NoMatch => "подходящее решение не найдено",
            InferenceStatus.Conflict => "конфликт, итог не подтвержден",
            _ => "лимит вывода или анализа, итог не подтвержден"
        }));
        if (result.Status == InferenceStatus.Success) {
            string goalId = knowledge.Facts.Values.Single(fact => fact.Role == FactRole.Goal).Id;
            if (knowledge.Outcomes.TryGetValue(result.Facts[goalId].Value.Canonical, out ConsultationOutcome? outcome)) {
                string label = outcome.Kind switch {
                    ConsultationOutcomeKind.Strategy => "Рекомендация",
                    ConsultationOutcomeKind.Clarification => "Требуется уточнение",
                    _ => "Отказ от рекомендации"
                };
                output.AppendLine(label + ": " + outcome.Value + " — " + outcome.Title);
                output.AppendLine(outcome.Description);
            }
        }
        foreach (InferenceStep step in result.Steps) {
            KnowledgeRule rule = knowledge.Rules.Single(item => item.Id == step.RuleId);
            output.AppendLine($"Шаг {step.Number}: {rule.Id}, priority={rule.Priority}, повестка=[{string.Join(",", step.Agenda)}]");
            output.AppendLine("  условие: " + DescribeCondition(step.Condition));
            output.AppendLine("  вывод: " + string.Join("; ", step.Conclusions.Select(item => item.FactId + "=" + item.Value.Canonical)));
            output.AppendLine("  пояснение: " + rule.Explanation);
            output.AppendLine("  происхождение правила: " + rule.Provenance);
        }
        if (result.Conflict is InferenceConflict conflict) {
            output.AppendLine($"Противоречие {conflict.FactId}: {conflict.Existing.Value.Canonical} / {conflict.Incoming.Value.Canonical}");
            output.AppendLine("  прежние основания:");
            AppendFact(conflict.FactId, "    ", new HashSet<string>(StringComparer.Ordinal), 0);
            output.AppendLine("  новое основание: правило " + conflict.RuleId + "; " + DescribeCondition(conflict.Condition));
            foreach (string premise in ConditionEvaluator.Premises(conflict.Condition))
                AppendFact(premise, "    ", new HashSet<string>(StringComparer.Ordinal), 0);
            output.AppendLine("Все ранее выведенные значения предварительные; подтвержденной рекомендации нет");
        }
        if (result.MissingInputs.Length > 0)
            output.AppendLine("Недостающие входы: " + string.Join(", ", result.MissingInputs));
        if (result.Status == InferenceStatus.Success) {
            string goal = knowledge.Facts.Values.Single(fact => fact.Role == FactRole.Goal).Id;
            output.AppendLine("Доказательство цели:");
            AppendFact(goal, "  ", new HashSet<string>(StringComparer.Ordinal), 0);
        }
        return output.ToString();

        void AppendFact(string id, string indent, HashSet<string> path, int depth) {
            if (++displayedNodes > 512) {
                if (displayedNodes == 513) output.AppendLine(indent + "предел отображения 512 узлов; полная трасса сохранена в результате");
                return;
            }
            if (!result.Facts.TryGetValue(id, out ProvenFact? fact)) return;
            output.AppendLine(indent + id + "=" + fact.Value.Canonical);
            if (depth >= 32 || !path.Add(id)) {
                output.AppendLine(indent + "  повторная ссылка или предел глубины");
                return;
            }
            // первое основание уже существовало до производных, альтернативные ссылки не раздувают дерево
            FactEvidence primary = fact.Evidence[0];
            output.AppendLine(indent + "  основание: " + primary.Origin);
            foreach (string premise in primary.Premises) AppendFact(premise, indent + "    ", path, depth + 1);
            foreach (FactEvidence alternative in fact.Evidence.Skip(1))
                output.AppendLine(indent + "  дополнительное основание: " + alternative.Origin + " [" + string.Join(",", alternative.Premises) + "]");
            path.Remove(id);
        }
    }

    private static string DescribeCondition(Evaluation evaluation) {
        if (evaluation.Condition is ComparisonCondition comparison)
            return comparison.FactId + "=" + (evaluation.Actual?.Canonical ?? "неизвестно") + " " + comparison.Operator
                + " " + comparison.Expected.Canonical + " -> " + evaluation.State;
        string join = evaluation.Condition is AllCondition ? " И " : " ИЛИ ";
        return "(" + string.Join(join, evaluation.Children.Select(DescribeCondition)) + ") -> " + evaluation.State;
    }
}
