using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.Services.Consultation;

// сеанс фиксирует БЗ, а каждый ответ заново выводит производные только из текущих исходных фактов
public sealed class ConsultationService {
    private ImmutableDictionary<string, InputFact> answers;
    private readonly InferenceService inference = new InferenceService();

    public KnowledgeBase Knowledge { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public InferenceResult Result { get; private set; }
    public bool Cancelled { get; private set; }
    public ImmutableDictionary<string, InputFact> Answers => answers;

    public ConsultationService(KnowledgeBase knowledge, ImmutableDictionary<string, InputFact>? initial = null) {
        Knowledge = knowledge;
        answers = initial ?? ImmutableDictionary<string, InputFact>.Empty;
        Result = inference.Run(Knowledge, answers);
    }

    public FactDefinition? NextQuestion {
        get {
            if (Cancelled || Result.Status != InferenceStatus.InsufficientData) return null;
            foreach (string id in Knowledge.QuestionOrder) {
                if (Result.MissingInputs.Contains(id) && !answers.ContainsKey(id)) return Knowledge.Facts[id];
            }
            return null;
        }
    }

    public ConsultationOutcome? Outcome {
        get {
            if (Cancelled || Result.Status != InferenceStatus.Success) return null;
            string goal = Knowledge.Facts.Values.Single(fact => fact.Role == FactRole.Goal).Id;
            return Knowledge.Outcomes.TryGetValue(Result.Facts[goal].Value.Canonical, out ConsultationOutcome? outcome) ? outcome : null;
        }
    }

    public bool TryAnswer(string factId, string text, out string error, bool canonicalEnum = false) {
        error = "";
        try {
            KnowledgeJson.Require(!Cancelled, "сеанс отменен");
            KnowledgeJson.Require(Knowledge.Facts.TryGetValue(factId, out FactDefinition? fact) && fact.Role == FactRole.Input,
                "можно изменить только объявленный input: " + factId);
            KnowledgeJson.Require(text.Length <= 4096, "ответ длиннее 4096 символов");
            KnowledgeValue? value = ParseAnswer(text.Trim(), fact!, canonicalEnum);
            InputFact input = new InputFact(factId, value, "operator:" + factId + "; at=" + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            ImmutableDictionary<string, InputFact> changed = answers.SetItem(factId, input);
            InferenceResult recalculated = inference.Run(Knowledge, changed);
            // невалидный ответ не доходит до присваивания, прежний результат остается целиком
            answers = changed;
            Result = recalculated;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException) {
            error = exception.Message;
            return false;
        }
    }

    public void Cancel() => Cancelled = true;

    public string ExplainHow() => new ExplanationService().Describe(Result);

    // берем текущий вопрос и снимок фактов, от цели идем через правила только по неизвестным условиям
    // сравнение с нужным входом завершает путь, а для производного признака ищем подходящих производителей
    // active хранит признаки текущей ветви и обрывает циклы; готовые цепочки собираем в paths, не меняя вывод
    // это возможные обоснования вопроса, а не сработавшие правила; раскрытие ограничено 8 путями, 1000 посещениями и глубиной 32
    public string ExplainWhy() {
        FactDefinition? question = NextQuestion;
        if (question is null) return "Сейчас нет текущего вопроса; используйте как для объяснения результата";
        List<string> paths = new List<string>();
        ConditionEvaluator evaluator = new ConditionEvaluator();
        int visits = 0;
        bool limited = false;
        string goal = Knowledge.Facts.Values.Single(fact => fact.Role == FactRole.Goal).Id;
        Search(goal, null, new List<string>(), new HashSet<string>(StringComparer.Ordinal), 0);
        string result = "Вопрос " + question.Id + " (" + question.Name + ") нужен для еще не доказанной цели " + goal
            + ". Возможные цепочки от цели к входу:\n" + string.Join("\n", paths.Distinct(StringComparer.Ordinal));
        if (paths.Count == 0) result += "\nВход указан анализом недостающих данных; полная цепочка здесь не раскрыта";
        if (limited) result += "\nПоказаны ограниченные пути: до 8 цепочек, 1000 посещений и глубина 32";
        return result;

        void Search(string factId, ComparisonCondition? desired, List<string> chain, HashSet<string> active, int depth) {
            if (++visits > 1000 || depth >= 32 || paths.Count >= 8) { limited = true; return; }
            if (!active.Add(factId)) return;
            foreach (KnowledgeRule producer in Knowledge.Rules.OrderByDescending(rule => rule.Priority).ThenBy(rule => rule.Id, StringComparer.Ordinal)) {
                if (!producer.Conclusions.Any(item => item.FactId == factId
                    && (desired is null || ConditionEvaluator.Compare(item.Value, desired.Operator, desired.Expected)))) continue;
                Evaluation evaluation = evaluator.Evaluate(producer.Condition, Result.Facts);
                if (evaluation.State != TruthValue.Unknown) continue;
                List<string> next = new List<string>(chain) { producer.Id };
                Walk(evaluation, next, active, depth + 1);
            }
            active.Remove(factId);
        }

        void Walk(Evaluation evaluation, List<string> chain, HashSet<string> active, int depth) {
            if (++visits > 1000 || depth >= 32 || paths.Count >= 8) { limited = true; return; }
            if (evaluation.State != TruthValue.Unknown) return;
            if (evaluation.Condition is ComparisonCondition comparison) {
                if (comparison.FactId == question.Id) paths.Add("  " + string.Join(" -> ", chain) + " -> " + question.Id);
                else if (Knowledge.Facts[comparison.FactId].Role != FactRole.Input)
                    Search(comparison.FactId, comparison, chain, active, depth + 1);
                return;
            }
            foreach (Evaluation child in evaluation.Children) Walk(child, chain, active, depth + 1);
        }
    }

    private static KnowledgeValue? ParseAnswer(string text, FactDefinition fact, bool canonicalEnum) {
        if (text == "?" || text.Equals("не знаю", StringComparison.OrdinalIgnoreCase)
            || text.Equals("неизвестно", StringComparison.OrdinalIgnoreCase)) return null;
        string json;
        if (fact.Type == FactType.Boolean) {
            json = text.ToLowerInvariant() switch {
                "да" or "true" => "true",
                "нет" or "false" => "false",
                _ => throw new InvalidDataException("допустимы да, нет или не знаю")
            };
        }
        else if (fact.Type == FactType.Enum) {
            string value = text;
            // в CLI число может быть номером варианта, а кнопка UI уже передает точное enum-значение
            if (!canonicalEnum && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) {
                KnowledgeJson.Require(index >= 1 && index <= fact.Options.Length, "номер варианта вне списка");
                value = fact.Options[index - 1];
            }
            json = JsonSerializer.Serialize(value);
        }
        else {
            // числовой ввод использует JSON-формат с точкой и общие проверки точности/диапазона
            json = text;
        }
        using JsonDocument document = KnowledgeJson.Parse(Encoding.UTF8.GetBytes(json));
        return KnowledgeJson.Value(document.RootElement, fact, fact.Id);
    }
}
