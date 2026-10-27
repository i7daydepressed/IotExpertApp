using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public sealed class ConditionEvaluator {
    public Evaluation Evaluate(Condition condition, IReadOnlyDictionary<string, ProvenFact> facts) {
        if (condition is ComparisonCondition comparison) {
            if (!facts.TryGetValue(comparison.FactId, out ProvenFact? fact))
                return new Evaluation(TruthValue.Unknown, condition, null, ImmutableArray<Evaluation>.Empty);
            return new Evaluation(Compare(fact.Value, comparison.Operator, comparison.Expected) ? TruthValue.True : TruthValue.False,
                condition, fact.Value, ImmutableArray<Evaluation>.Empty);
        }
        ImmutableArray<Condition> items = condition switch {
            AllCondition all => all.Items,
            AnyCondition any => any.Items,
            _ => throw new InvalidOperationException("неизвестный узел условия")
        };
        ImmutableArray<Evaluation>.Builder children = ImmutableArray.CreateBuilder<Evaluation>();
        bool conjunction = condition is AllCondition;
        foreach (Condition item in items) {
            Evaluation child = Evaluate(item, facts);
            children.Add(child);
            // в доказательство any попадает только реально выбранная истинная ветвь
            if (!conjunction && child.State == TruthValue.True)
                return new Evaluation(TruthValue.True, condition, null, ImmutableArray.Create(child));
        }
        TruthValue state = conjunction
            ? (children.Any(child => child.State == TruthValue.False) ? TruthValue.False
                : children.All(child => child.State == TruthValue.True) ? TruthValue.True : TruthValue.Unknown)
            : (children.All(child => child.State == TruthValue.False) ? TruthValue.False : TruthValue.Unknown);
        return new Evaluation(state, condition, null, children.ToImmutable());
    }

    public static bool Compare(KnowledgeValue actual, string operation, KnowledgeValue expected) {
        if (actual.Type != expected.Type) throw new InvalidOperationException("сравниваются разные типы");
        int comparison = actual.Type is FactType.Integer or FactType.Number
            ? decimal.Compare(decimal.Parse(actual.Canonical, NumberStyles.Float, CultureInfo.InvariantCulture),
                decimal.Parse(expected.Canonical, NumberStyles.Float, CultureInfo.InvariantCulture))
            : StringComparer.Ordinal.Compare(actual.Canonical, expected.Canonical);
        return operation switch {
            "eq" => comparison == 0,
            "ne" => comparison != 0,
            "gt" => comparison > 0,
            "gte" => comparison >= 0,
            "lt" => comparison < 0,
            "lte" => comparison <= 0,
            _ => throw new InvalidOperationException("неизвестный оператор")
        };
    }

    public static ImmutableArray<string> Premises(Evaluation evaluation) {
        List<string> ids = new List<string>();
        Collect(evaluation, ids);
        return ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray();
    }

    private static void Collect(Evaluation evaluation, List<string> ids) {
        if (evaluation.State != TruthValue.True) return;
        if (evaluation.Condition is ComparisonCondition comparison) ids.Add(comparison.FactId);
        foreach (Evaluation child in evaluation.Children) Collect(child, ids);
    }
}
