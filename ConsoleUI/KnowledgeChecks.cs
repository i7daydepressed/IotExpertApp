using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ConsoleUI;

public static class KnowledgeChecks {
    public static void Run() {
        string directory = Path.Combine(AppContext.BaseDirectory, "KnowledgeExamples");
        JsonObject source = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "synthetic-engine.json")))!.AsObject();
        JsonObject inputSource = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "synthetic-facts.json")))!.AsObject();
        KnowledgeBase knowledge = Load(source);
        ImmutableDictionary<string, InputFact> inputs = ReadInputs(inputSource, knowledge);
        InferenceService engine = new InferenceService();
        InferenceResult positive = engine.Run(knowledge, inputs);
        Check(positive.Status == InferenceStatus.Success && positive.Facts["outcome"].Value.Canonical == "accepted", "многошаговая цепочка");
        Check(positive.Steps.Select(step => step.RuleId).SequenceEqual(new[] { "SYN-10", "SYN-20" }), "точные ID цепочки");
        Check(positive.Facts["gate"].Evidence[0].Premises.Contains("mode")
            && !positive.Facts["gate"].Evidence[0].Premises.Contains("alternative"), "только доказанная ветвь any");
        string explanation = new ExplanationService().Describe(positive);
        Check(explanation.Contains("ИСКУССТВЕННАЯ") && explanation.Contains("synthetic/manual")
            && explanation.Contains("SYN-10") && explanation.Contains("SYN-20"), "источники и реальные правила в объяснении");
        Console.WriteLine("ядро: цепочка и происхождение пройдены");

        CheckTruthTables();
        JsonObject unknown = Clone(inputSource);
        unknown["facts"]![0]!["value"] = null;
        InferenceResult insufficient = engine.Run(knowledge, ReadInputs(unknown, knowledge));
        Check(insufficient.Status == InferenceStatus.InsufficientData && insufficient.MissingInputs.SequenceEqual(new[] { "operator_ready" }), "неизвестность и релевантный недостающий вход");
        JsonObject rejected = Clone(inputSource);
        rejected["facts"]![0]!["value"] = false;
        InferenceResult noMatch = engine.Run(knowledge, ReadInputs(rejected, knowledge));
        Check(noMatch.Status == InferenceStatus.NoMatch && !noMatch.Facts.ContainsKey("gate") && noMatch.MissingInputs.Length == 0, "известный запрет и отсутствие покрытия");
        Check(positive.Facts.ContainsKey("gate"), "новый расчет не меняет старый");
        JsonObject impossible = Clone(source);
        impossible["rules"]![1]!["condition"] = Compare("gate", false);
        Check(engine.Run(Load(impossible), ReadInputs(unknown, knowledge)).Status == InferenceStatus.NoMatch,
            "производитель другого значения не создает ложный запрос входа");
        JsonObject huge = Clone(inputSource);
        huge["facts"]![1]!["value"] = JsonNode.Parse("18446744073709551615");
        InferenceResult precise = engine.Run(knowledge, ReadInputs(huge, knowledge));
        Check(precise.Facts["sample_count"].Value.Canonical == "18446744073709551615", "UInt64 без double");
        Console.WriteLine("ядро: unknown, отсутствие решения, повторный расчет и числа пройдены");

        JsonObject competing = Clone(source);
        JsonObject early = Rule("SYN-01", "operator_ready", "gate", true, 30);
        JsonObject same = Rule("SYN-11", "operator_ready", "gate", true, 20);
        competing["rules"]!.AsArray().Add(same);
        competing["rules"]!.AsArray().Add(early);
        InferenceResult ordered = engine.Run(Load(competing), inputs);
        Check(ordered.Steps.Select(step => step.RuleId).SequenceEqual(new[] { "SYN-01", "SYN-10", "SYN-11", "SYN-20" }), "priority по убыванию и ordinal ID");
        Check(ordered.Facts["gate"].Evidence.Length == 3 && ordered.Steps.Length == 4, "альтернативные основания без повторного запуска правила");
        Check(engine.Run(knowledge, inputs, 1).Status == InferenceStatus.StepLimit, "лимит не выдает окончательную цель");

        JsonObject conflicting = Clone(source);
        conflicting["facts"]!.AsArray().Add(JsonNode.Parse("""{"id":"witness","name":"свидетель","type":"bool","role":"derived"}"""));
        JsonObject contradiction = Rule("SYN-21", "gate", "outcome", "rejected", 0);
        contradiction["conclusions"]!.AsArray().Insert(0, JsonNode.Parse("""{"fact_id":"witness","value":true}"""));
        conflicting["rules"]!.AsArray().Add(contradiction);
        InferenceResult conflict = engine.Run(Load(conflicting), inputs);
        Check(conflict.Status == InferenceStatus.Conflict && conflict.Conflict?.RuleId == "SYN-21"
            && conflict.Conflict.Existing.Value.Canonical == "accepted" && !conflict.Facts.ContainsKey("witness"), "конфликт и атомарность заключений");
        string conflictText = new ExplanationService().Describe(conflict);
        Check(conflictText.Contains("SYN-20") && conflictText.Contains("SYN-21")
            && !conflictText.Contains("Доказательство цели:"), "две конфликтующие цепочки без подтвержденного результата");
        Console.WriteLine("ядро: порядок, повторные основания, лимит и конфликт пройдены");

        JsonObject cycle = Clone(source);
        cycle["rules"] = new JsonArray(Rule("C-1", "cycle_b", "cycle_a", true, 10), Rule("C-2", "cycle_a", "cycle_b", true, 10),
            Rule("C-3", "cycle_a", "outcome", "accepted", 0));
        InferenceResult withoutSeed = engine.Run(Load(cycle), inputs);
        Check(withoutSeed.Status == InferenceStatus.NoMatch && withoutSeed.Steps.Length == 0, "цикл без опоры ничего не доказывает");
        cycle["rules"]!.AsArray().Add(Rule("C-0", "operator_ready", "cycle_a", true, 20));
        InferenceResult withSeed = engine.Run(Load(cycle), inputs);
        Check(withSeed.Status == InferenceStatus.Success && withSeed.Steps.Length == 4, "цикл с опорой конечен");
        Check(new ExplanationService().Describe(withSeed).Length < 20000, "объяснение цикла ограничено");
        JsonObject deep = Clone(source);
        JsonArray chain = new JsonArray();
        for (int index = 0; index < 40; index++) {
            string id = "deep_" + index;
            deep["facts"]!.AsArray().Add(new JsonObject { ["id"] = id, ["name"] = id, ["type"] = "bool", ["role"] = "derived" });
            chain.Add(Rule("D-" + index, index == 0 ? "operator_ready" : "deep_" + (index - 1), id, true, 10));
        }
        chain.Add(Rule("D-goal", "deep_39", "outcome", "accepted", 0));
        deep["rules"] = chain;
        KnowledgeBase deepKnowledge = Load(deep);
        Check(engine.Run(deepKnowledge, ReadInputs(unknown, deepKnowledge)).Status == InferenceStatus.StepLimit,
            "предел анализа не выдается за отсутствие решения");
        Check(engine.Run(deepKnowledge, inputs).Status == InferenceStatus.Success, "полностью известная длинная цепочка выводится");
        JsonObject changed = Clone(source);
        changed["version"] = "synthetic-2";
        changed["rules"]![1]!["conclusions"]![0]!["value"] = "rejected";
        KnowledgeBase updated = Load(changed);
        InferenceResult secondVersion = engine.Run(updated, inputs);
        Check(secondVersion.Facts["outcome"].Value.Canonical == "rejected" && updated.Sha256 != knowledge.Sha256
            && positive.KnowledgeBase.Version == "synthetic-1" && positive.Facts["outcome"].Value.Canonical == "accepted", "перезагрузка и неизменяемость старого снимка");
        Console.WriteLine("ядро: циклы и смена версии БЗ пройдены");

        Invalid(() => new KnowledgeBaseService().Parse(Encoding.UTF8.GetBytes("{\"schema_version\":1,\"schema_version\":1}")), "повтор ключа");
        Invalid(() => new KnowledgeBaseService().Parse(new byte[] { 0xFF }), "невалидный UTF-8");
        Invalid(() => new KnowledgeBaseService().Parse(new byte[KnowledgeJson.MaximumBytes + 1]), "лимит файла");
        InvalidKnowledge(source, node => node["schema_version"] = 2, "версия схемы");
        InvalidKnowledge(source, node => node["unknown"] = true, "неизвестное поле");
        InvalidKnowledge(source, node => node["rules"]![1]!["id"] = "SYN-10", "повтор ID");
        InvalidKnowledge(source, node => node["rules"]![0]!["condition"] = Compare("absent", true), "битая ссылка");
        InvalidKnowledge(source, node => node["rules"]![0]!["condition"] = JsonNode.Parse("""{"all":[]}"""), "пустая группа");
        InvalidKnowledge(source, node => node["rules"]![0]!["condition"] = JsonNode.Parse("""{"fact_id":"operator_ready","op":"gt","value":true}"""), "оператор bool");
        InvalidKnowledge(source, node => node["rules"]![0]!["conclusions"]![0]!["fact_id"] = "operator_ready", "перезапись входа");
        InvalidKnowledge(source, node => node["facts"]![0]!.AsObject().Remove("question"), "нет вопроса");
        InvalidKnowledge(source, node => node["facts"]![3]!["options"] = new JsonArray("demo", "demo"), "повтор enum");
        InvalidKnowledge(source, node => node["facts"]![1]!["min"] = 100000000000000000000m, "диапазон");
        InvalidKnowledge(source, node => node["rules"]![0]!["priority"] = "high", "тип priority");
        InvalidKnowledge(source, node => {
            JsonNode condition = Compare("operator_ready", true);
            for (int index = 0; index < 17; index++) condition = new JsonObject { ["all"] = new JsonArray(condition) };
            node["rules"]![0]!["condition"] = condition;
        }, "глубина выражений");
        InvalidInputs(inputSource, knowledge, node => node["facts"]![0]!["value"] = "true", "строка вместо bool");
        InvalidInputs(inputSource, knowledge, node => node["facts"]![1]!["value"] = 2.5m, "дробное integer");
        InvalidInputs(inputSource, knowledge, node => node["facts"]![2]!["value"] = 101, "выход из диапазона");
        InvalidInputs(inputSource, knowledge, node => node["facts"]![3]!["value"] = "unknown-mode", "неизвестный enum");
        InvalidInputs(inputSource, knowledge, node => node["facts"]![2]!["value"] = JsonNode.Parse("0.100000000000000000000000000001"), "скрытое округление decimal");
        InvalidInputs(inputSource, knowledge, node => node["facts"]![2]!["value"] = JsonNode.Parse("1e-1000"), "исчезновение числа в ноль");
        InvalidInputs(inputSource, knowledge, node => node["facts"]!.AsArray().Add(node["facts"]![0]!.DeepClone()), "дублирование входа");
        InvalidInputs(inputSource, knowledge, node => node["facts"]![0]!["fact_id"] = "gate", "подмена производного факта");
        JsonObject asDecimal = Clone(inputSource);
        asDecimal["facts"]![2]!["value"] = JsonNode.Parse("24.0");
        Check(ReadInputs(asDecimal, knowledge)["ambient"].Value == inputs["ambient"].Value, "канонизация равных чисел");
        Console.WriteLine("ядро: валидация формата, типов, диапазонов и точности пройдена");
    }

    private static void CheckTruthTables() {
        ConditionEvaluator evaluator = new ConditionEvaluator();
        TruthValue[] values = { TruthValue.False, TruthValue.Unknown, TruthValue.True };
        TruthValue[,] allTable =
        {
            { TruthValue.False, TruthValue.False, TruthValue.False },
            { TruthValue.False, TruthValue.Unknown, TruthValue.Unknown },
            { TruthValue.False, TruthValue.Unknown, TruthValue.True }
        };
        TruthValue[,] anyTable =
        {
            { TruthValue.False, TruthValue.Unknown, TruthValue.True },
            { TruthValue.Unknown, TruthValue.Unknown, TruthValue.True },
            { TruthValue.True, TruthValue.True, TruthValue.True }
        };
        for (int leftIndex = 0; leftIndex < values.Length; leftIndex++)
            for (int rightIndex = 0; rightIndex < values.Length; rightIndex++) {
                TruthValue left = values[leftIndex];
                TruthValue right = values[rightIndex];
                Dictionary<string, ProvenFact> facts = new Dictionary<string, ProvenFact>(StringComparer.Ordinal);
                Add("left", left); Add("right", right);
                Condition l = new ComparisonCondition("left", "eq", new KnowledgeValue(FactType.Boolean, "true"));
                Condition r = new ComparisonCondition("right", "eq", new KnowledgeValue(FactType.Boolean, "true"));
                Check(evaluator.Evaluate(new AllCondition(ImmutableArray.Create(l, r)), facts).State == allTable[leftIndex, rightIndex], "таблица all");
                Check(evaluator.Evaluate(new AnyCondition(ImmutableArray.Create(l, r)), facts).State == anyTable[leftIndex, rightIndex], "таблица any");
                void Add(string id, TruthValue state) {
                    if (state != TruthValue.Unknown)
                        facts.Add(id, new ProvenFact(new KnowledgeValue(FactType.Boolean, state == TruthValue.True ? "true" : "false"), ImmutableArray<FactEvidence>.Empty));
                }
            }
        Check(evaluator.Evaluate(new ComparisonCondition("missing", "ne", new KnowledgeValue(FactType.Boolean, "true")),
            new Dictionary<string, ProvenFact>()).State == TruthValue.Unknown, "ne неизвестного остается unknown");
    }

    private static JsonObject Rule(string id, string input, string output, object value, int priority)
        => new JsonObject {
            ["id"] = id,
            ["title"] = "искусственная проверка " + id,
            ["priority"] = priority,
            ["condition"] = Compare(input, true),
            ["conclusions"] = new JsonArray(new JsonObject { ["fact_id"] = output, ["value"] = JsonSerializer.SerializeToNode(value) }),
            ["explanation"] = "проверка алгоритма",
            ["provenance"] = "synthetic test only"
        };
    private static JsonObject Compare(string id, object value)
        => new JsonObject { ["fact_id"] = id, ["op"] = "eq", ["value"] = JsonSerializer.SerializeToNode(value) };
    private static JsonObject Clone(JsonObject source) => source.DeepClone().AsObject();
    private static KnowledgeBase Load(JsonObject source) => new KnowledgeBaseService().Parse(Encoding.UTF8.GetBytes(source.ToJsonString()));
    private static ImmutableDictionary<string, InputFact> ReadInputs(JsonObject source, KnowledgeBase knowledge)
        => new InputFactService().Parse(Encoding.UTF8.GetBytes(source.ToJsonString()), knowledge);
    private static void InvalidKnowledge(JsonObject source, Action<JsonObject> change, string label) {
        JsonObject copy = Clone(source); change(copy); Invalid(() => Load(copy), label);
    }
    private static void InvalidInputs(JsonObject source, KnowledgeBase knowledge, Action<JsonObject> change, string label) {
        JsonObject copy = Clone(source); change(copy); Invalid(() => ReadInputs(copy, knowledge), label);
    }
    private static void Invalid(Action action, string label) {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or DecoderFallbackException) { return; }
        throw new InvalidOperationException("ожидалось отклонение: " + label);
    }
    private static void Check(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
