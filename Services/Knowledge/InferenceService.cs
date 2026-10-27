using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public sealed class InferenceService {
    // из неизменяемой БЗ и входов собираем отдельный словарь доказанных фактов для этого расчета
    // на каждом ходе заново выбираем первое доказанное правило по priority/ID, весь его вывод проверяем на конфликт
    // правило срабатывает только раз, затем возвращаем неподвижную точку или явно незавершенный исход
    // недостающие входы ищем отдельно, не превращая отсутствие доказательства в отрицательный факт
    public InferenceResult Run(KnowledgeBase knowledgeBase, ImmutableDictionary<string, InputFact> inputs, int maximumSteps = 1000) {
        KnowledgeJson.Require(maximumSteps is >= 1 and <= 1000, "лимит вывода должен быть 1..1000");
        Dictionary<string, ProvenFact> facts = new Dictionary<string, ProvenFact>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, InputFact> pair in inputs) {
            InputFact input = pair.Value;
            KnowledgeJson.Require(input.FactId == pair.Key && knowledgeBase.Facts.TryGetValue(pair.Key, out FactDefinition? definition)
                && definition.Role == FactRole.Input && !string.IsNullOrWhiteSpace(input.Origin), "некорректный исходный факт " + pair.Key);
            if (input.Value is null) continue;
            FactDefinition declared = knowledgeBase.Facts[pair.Key];
            KnowledgeJson.Require(input.Value.Type == declared.Type, "неверный тип исходного факта " + pair.Key);
            string json = input.Value.Type == FactType.Enum ? JsonSerializer.Serialize(input.Value.Canonical) : input.Value.Canonical;
            using JsonDocument valueDocument = KnowledgeJson.Parse(Encoding.UTF8.GetBytes(json));
            KnowledgeValue checkedValue = KnowledgeJson.Value(valueDocument.RootElement, declared, pair.Key);
            facts.Add(pair.Key, new ProvenFact(checkedValue, ImmutableArray.Create(
                new FactEvidence(input.Origin, null, ImmutableArray<string>.Empty))));
        }
        List<InferenceStep> steps = new List<InferenceStep>();
        HashSet<string> fired = new HashSet<string>(StringComparer.Ordinal);
        ConditionEvaluator evaluator = new ConditionEvaluator();
        while (true) {
            KnowledgeRule[] agenda = knowledgeBase.Rules.Where(rule => !fired.Contains(rule.Id)
                && evaluator.Evaluate(rule.Condition, facts).State == TruthValue.True)
                .OrderByDescending(rule => rule.Priority).ThenBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
            if (agenda.Length == 0) break;
            if (steps.Count >= maximumSteps) return Result(InferenceStatus.StepLimit, ImmutableArray<string>.Empty, null);
            KnowledgeRule rule = agenda[0];
            Evaluation condition = evaluator.Evaluate(rule.Condition, facts);
            foreach (Conclusion conclusion in rule.Conclusions) {
                // весь набор проверяется до изменения словаря, частичного применения правила нет
                if (facts.TryGetValue(conclusion.FactId, out ProvenFact? existing)
                    && !ConditionEvaluator.Compare(existing.Value, "eq", conclusion.Value))
                    return Result(InferenceStatus.Conflict, ImmutableArray<string>.Empty,
                        new InferenceConflict(conclusion.FactId, existing, conclusion, rule.Id, condition));
            }
            FactEvidence evidence = new FactEvidence("rule:" + rule.Id, rule.Id, ConditionEvaluator.Premises(condition));
            foreach (Conclusion conclusion in rule.Conclusions) {
                if (facts.TryGetValue(conclusion.FactId, out ProvenFact? existing))
                    facts[conclusion.FactId] = existing with { Evidence = existing.Evidence.Add(evidence) };
                else facts.Add(conclusion.FactId, new ProvenFact(conclusion.Value, ImmutableArray.Create(evidence)));
            }
            fired.Add(rule.Id);
            steps.Add(new InferenceStep(steps.Count + 1, rule.Id, agenda.Select(item => item.Id).ToImmutableArray(), condition, rule.Conclusions));
        }
        string goal = knowledgeBase.Facts.Values.Single(fact => fact.Role == FactRole.Goal).Id;
        if (facts.ContainsKey(goal)) return Result(InferenceStatus.Success, ImmutableArray<string>.Empty, null);
        HashSet<string> missing = new HashSet<string>(StringComparer.Ordinal);
        int analysisVisits = 0;
        bool analysisLimit = false;
        CanProduce(goal, null, new HashSet<string>(StringComparer.Ordinal), missing);
        if (analysisLimit) return Result(InferenceStatus.StepLimit, ImmutableArray<string>.Empty, null);
        return Result(missing.Count > 0 ? InferenceStatus.InsufficientData : InferenceStatus.NoMatch,
            missing.OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray(), null);

        InferenceResult Result(InferenceStatus status, ImmutableArray<string> missingInputs, InferenceConflict? conflict)
            => new InferenceResult(knowledgeBase, status, inputs, facts.ToImmutableDictionary(StringComparer.Ordinal),
                steps.ToImmutableArray(), missingInputs, conflict);

        // анализ ищет недостающие входы только у еще достижимых путей к цели
        bool CanProduce(string factId, ComparisonCondition? desired, HashSet<string> path, HashSet<string> needed, int depth = 0) {
            if (analysisLimit || ++analysisVisits > 10000 || depth >= 64) { analysisLimit = true; return false; }
            // повтор признака в текущем пути означает цикл без новой опоры, другие производители еще могут помочь
            if (!path.Add(factId)) return false;
            bool possible = false;
            foreach (KnowledgeRule producer in knowledgeBase.Rules) {
                if (!producer.Conclusions.Any(conclusion => conclusion.FactId == factId
                    && (desired is null || ConditionEvaluator.Compare(conclusion.Value, desired.Operator, desired.Expected)))) continue;
                HashSet<string> branch = new HashSet<string>(StringComparer.Ordinal);
                if (CanSatisfy(evaluator.Evaluate(producer.Condition, facts), path, branch, depth + 1)) {
                    possible = true;
                    needed.UnionWith(branch);
                }
            }
            path.Remove(factId);
            return possible;
        }

        bool CanSatisfy(Evaluation evaluation, HashSet<string> path, HashSet<string> needed, int depth) {
            if (analysisLimit || ++analysisVisits > 10000 || depth >= 64) { analysisLimit = true; return false; }
            // известная истина уже обеспечена, известную ложь дополнительным вопросом не исправить
            if (evaluation.State == TruthValue.True) return true;
            if (evaluation.State == TruthValue.False) return false;
            if (evaluation.Condition is ComparisonCondition comparison) {
                if (knowledgeBase.Facts[comparison.FactId].Role == FactRole.Input) {
                    needed.Add(comparison.FactId);
                    return true;
                }
                return CanProduce(comparison.FactId, comparison, path, needed, depth + 1);
            }
            bool conjunction = evaluation.Condition is AllCondition;
            bool possible = conjunction;
            HashSet<string> combined = new HashSet<string>(StringComparer.Ordinal);
            foreach (Evaluation child in evaluation.Children) {
                HashSet<string> branch = new HashSet<string>(StringComparer.Ordinal);
                bool childPossible = CanSatisfy(child, path, branch, depth + 1);
                if (conjunction && !childPossible) return false;
                if (childPossible) { possible = true; combined.UnionWith(branch); }
            }
            if (possible) needed.UnionWith(combined);
            return possible;
        }
    }
}
