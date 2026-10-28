using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

public sealed class ExplanationTreeBuilder {
    // из закрепленного InferenceResult строим отдельное дерево представления, не вычисляя правила заново
    // первое основание факта раскрываем до входов без предпосылок, остальные основания оставляем ссылками
    // path содержит признаки текущей ветви: повтор завершает рекурсию, после обхода признак убираем
    // Add собирает дочерние узлы в родительский и ограничивает только отображение глубиной 32 и общим бюджетом 512 узлов
    public ImmutableArray<ExplanationTreeNode> Build(InferenceResult result) => new Projection(result).Build();

    // счетчик принадлежит одному построению; лимит ограничивает отображение, а не сохраненную трассу
    private sealed class Projection {
        private const int MaximumNodes = 512;
        private const int MaximumDepth = 32;
        private readonly InferenceResult result;
        private int count;
        private bool markerAdded;

        public Projection(InferenceResult result) { this.result = result; }

        public ImmutableArray<ExplanationTreeNode> Build() {
            List<ExplanationTreeNode> roots = new List<ExplanationTreeNode>();
            KnowledgeBase knowledge = result.KnowledgeBase;
            Add(roots, "БЗ " + knowledge.Id + " | версия " + knowledge.Version + " | SHA256 " + knowledge.Sha256, 0);
            Add(roots, "Статус: " + result.Status + (result.Status == InferenceStatus.Success
                ? " — исход доказан" : " — подтвержденной рекомендации нет"), 0);
            if (result.Status == InferenceStatus.Success) {
                string goal = knowledge.Facts.Values.Single(fact => fact.Role == FactRole.Goal).Id;
                Add(roots, "Доказательство исхода", 0, children => Fact(children, goal, new HashSet<string>(StringComparer.Ordinal), 1));
            }
            if (result.Conflict is InferenceConflict conflict) {
                Add(roots, "Конфликт " + conflict.FactId + ": " + conflict.Existing.Value.Canonical + " / "
                    + conflict.Incoming.Value.Canonical + "; значения предварительные", 0, children => {
                        Fact(children, conflict.FactId, new HashSet<string>(StringComparer.Ordinal), 1);
                        Add(children, "Новое заключение: " + conflict.Incoming.FactId + "=" + conflict.Incoming.Value.Canonical
                            + " | правило " + conflict.RuleId, 1, incoming => Condition(incoming, conflict.Condition, 2));
                    });
            }
            if (result.MissingInputs.Length > 0) {
                Add(roots, "Недостающие основания", 0, children => {
                    foreach (string id in result.MissingInputs) {
                        Add(children, id + " — " + knowledge.Facts[id].Name + ": "
                            + (result.Inputs.ContainsKey(id) ? "получен неизвестный ответ" : "ответа нет"), 1);
                    }
                });
            }
            Add(roots, "Фактическая трасса: " + result.Steps.Length + " срабатываний", 0, children => {
                foreach (InferenceStep step in result.Steps) {
                    if (markerAdded) break;
                    KnowledgeRule rule = knowledge.Rules.Single(item => item.Id == step.RuleId);
                    Add(children, "Шаг " + step.Number + ": " + rule.Id + " — " + rule.Title + " | priority=" + rule.Priority, 1, detail => {
                        Add(detail, "Повестка: " + string.Join(", ", step.Agenda), 2);
                        Add(detail, "Происхождение: " + rule.Provenance, 2);
                        Condition(detail, step.Condition, 2);
                        foreach (Conclusion conclusion in step.Conclusions)
                            Add(detail, "Выведено: " + conclusion.FactId + "=" + conclusion.Value.Canonical, 2);
                    });
                }
            });
            Add(roots, "Исходные ответы: " + result.Inputs.Count, 0, children => {
                foreach (InputFact input in result.Inputs.Values.OrderBy(item => item.FactId, StringComparer.Ordinal))
                    Add(children, input.FactId + "=" + (input.Value?.Canonical ?? "неизвестно") + " | " + input.Origin, 1);
            });
            return roots.ToImmutableArray();
        }

        private void Fact(List<ExplanationTreeNode> parent, string id, HashSet<string> path, int depth) {
            if (!result.Facts.TryGetValue(id, out ProvenFact? fact)) {
                Add(parent, id + ": основание не сохранено", depth);
                return;
            }
            bool repeated = path.Contains(id);
            Add(parent, id + "=" + fact.Value.Canonical + " — " + result.KnowledgeBase.Facts[id].Name
                + (repeated ? " | повторная ссылка, ветвь не раскрывается" : ""), depth, children => {
                    if (repeated) return;
                    path.Add(id);
                    // первое основание существовало раньше производного; альтернативы показываем как ссылки
                    for (int index = 0; index < fact.Evidence.Length; index++) {
                        FactEvidence evidence = fact.Evidence[index];
                        string title = evidence.Origin;
                        if (evidence.RuleId is string ruleId) {
                            KnowledgeRule? rule = result.KnowledgeBase.Rules.FirstOrDefault(item => item.Id == ruleId);
                            title = "Правило " + ruleId + (rule is null ? "" : " — " + rule.Title + "\n" + rule.Provenance);
                        }
                        if (index == 0) Add(children, title, depth + 1, premises => {
                            foreach (string premise in evidence.Premises) Fact(premises, premise, path, depth + 2);
                        });
                        else Add(children, "Дополнительное основание: " + title + " | ссылки: " + string.Join(", ", evidence.Premises), depth + 1);
                        if (markerAdded) break;
                    }
                    path.Remove(id);
                });
        }

        private void Condition(List<ExplanationTreeNode> parent, Evaluation evaluation, int depth) {
            string title = evaluation.Condition is ComparisonCondition comparison
                ? comparison.FactId + "=" + (evaluation.Actual?.Canonical ?? "неизвестно") + " " + comparison.Operator
                    + " " + comparison.Expected.Canonical + " → " + evaluation.State
                : (evaluation.Condition is AllCondition ? "И (all)" : "ИЛИ (any)") + " → " + evaluation.State;
            Add(parent, title, depth, children => {
                // используем оценки, записанные при срабатывании; текущие факты не переоцениваем
                foreach (Evaluation child in evaluation.Children) Condition(children, child, depth + 1);
            });
        }

        private void Add(List<ExplanationTreeNode> parent, string title, int depth, Action<List<ExplanationTreeNode>>? populate = null) {
            if (markerAdded) return;
            if (count >= MaximumNodes - 1) {
                parent.Add(new ExplanationTreeNode("Предел отображения 512 узлов; полная трасса остается в результате", ImmutableArray<ExplanationTreeNode>.Empty));
                count++;
                markerAdded = true;
                return;
            }
            count++;
            List<ExplanationTreeNode> children = new List<ExplanationTreeNode>();
            if (depth >= MaximumDepth) title += " | предел глубины 32, ветвь сокращена";
            else populate?.Invoke(children);
            parent.Add(new ExplanationTreeNode(title, children.ToImmutableArray()));
        }
    }
}
