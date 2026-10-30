using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ViewModels;

// представление держит выбранный результат целиком; ни текущий профиль, ни новые ответы не участвуют в доказательстве
public sealed class ResultExplanationViewModel {
    public InferenceResult ResultSnapshot { get; }
    public string Context { get; }
    public string Outcome { get; }
    public string Status { get; }
    public string Basis { get; }
    public string Inputs { get; }
    public string Rules { get; }
    public string Technical { get; }
    public ImmutableArray<ExplanationTreeNode> TreeRoots { get; }
    public ResultExplanationViewModel(InferenceResult result, string context, string? technicalContext = null) {
        ResultSnapshot = result;
        Context = context + "\nОбъяснение закреплено " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        KnowledgeBase knowledge = result.KnowledgeBase;
        Status = StatusLabel(result.Status);
        FactDefinition? goal = knowledge.Facts.Values.SingleOrDefault(f => f.Role == FactRole.Goal);
        Outcome = result.Status == InferenceStatus.Success && goal is not null && result.Facts.TryGetValue(goal.Id, out ProvenFact? fact)
            ? knowledge.Outcomes.TryGetValue(fact.Value.Canonical, out ConsultationOutcome? outcome)
                ? fact.Value.Canonical + " · " + outcome.Title + "\n" + outcome.Description : KnowledgeTextFormatter.Name(knowledge, goal.Id) + " = " + KnowledgeTextFormatter.Value(fact.Value)
            : Status;
        // идем только по сохраненным Premises доказанной цели, не называем все переданные входы использованными
        HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        Stack<string> pending = new Stack<string>();
        if (result.Status == InferenceStatus.Success && goal is not null) pending.Push(goal.Id);
        while (pending.Count > 0) {
            string id = pending.Pop();
            if (!visited.Add(id) || !result.Facts.TryGetValue(id, out ProvenFact? proof)) continue;
            if (knowledge.Facts[id].Role == FactRole.Input) used.Add(id);
            foreach (FactEvidence evidence in proof.Evidence) foreach (string premise in evidence.Premises) pending.Push(premise);
        }
        string Input(InputFact input, bool origin) => KnowledgeTextFormatter.Name(knowledge, input.FactId) + " = "
            + (input.Value is null ? "Не знаю (ответ получен)" : KnowledgeTextFormatter.Value(input.Value)) + (origin ? "\nИсточник: " + input.Origin : "");
        Basis = used.Count > 0 ? string.Join("\n", used.OrderBy(id => id, StringComparer.Ordinal).Where(result.Inputs.ContainsKey).Select(id => Input(result.Inputs[id], false)))
            + "\nПроисхождение — в раскрытии входных данных и в дереве доказательства"
            : result.Status == InferenceStatus.Success ? "Цель получена без входных предпосылок; основания — в дереве доказательства"
            : "Доказанного окончательного исхода нет; входные данные и недостающие основания показаны отдельно";
        if (result.MissingInputs.Length > 0) Basis += "\nНедостающие основания:\n" + string.Join("\n", result.MissingInputs.Select(id => KnowledgeTextFormatter.Name(knowledge, id)
            + (result.Inputs.ContainsKey(id) ? ": получен неизвестный ответ" : ": ответа нет")));
        Inputs = result.Inputs.Count == 0 ? "Входных данных расчета нет" : string.Join("\n", result.Inputs.Values.OrderBy(i => i.FactId, StringComparer.Ordinal).Select(input => Input(input, true)));
        Rules = result.Steps.Length == 0 ? "Срабатываний правил в этом расчете нет" : string.Join("\n", result.Steps.Select(step => {
            KnowledgeRule rule = knowledge.Rules.Single(r => r.Id == step.RuleId);
            return step.Number + ". " + rule.Id + " · " + rule.Title + " → " + string.Join("; ", step.Conclusions.Select(c => KnowledgeTextFormatter.Name(knowledge, c.FactId) + " = " + KnowledgeTextFormatter.Value(c.Value)));
        }));
        TreeRoots = new ExplanationTreeBuilder().Build(result);
        Technical = "БЗ " + knowledge.Id + " / " + knowledge.Version + " / SHA256 " + knowledge.Sha256 + "\n" + Context + "\n" + technicalContext + "\n" + new ExplanationService().Describe(result);
    }
    public static string StatusLabel(InferenceStatus status) => KnowledgeTextFormatter.Status(status);
}
