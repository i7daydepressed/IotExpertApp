using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Consultation;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ConsoleUI;

public static class Lr1ConsultationChecks {
    public static void Run() {
        string knowledgePath = Path.Combine(AppContext.BaseDirectory, "Knowledge", "lr1-strategies.json");
        KnowledgeBase knowledge = new KnowledgeBaseService().Load(knowledgePath);
        Check(knowledge.SchemaVersion == 2 && !knowledge.Synthetic && knowledge.Outcomes.Count == 10, "предметная схема и каталог исходов");
        Check(knowledge.Rules.Count(rule => rule.Id.StartsWith("R-", StringComparison.Ordinal)) == 20, "ровно двадцать перенесенных правил");
        using JsonDocument scenarios = KnowledgeJson.Parse(KnowledgeJson.ReadBounded(Path.Combine(AppContext.BaseDirectory, "Scenarios", "lr1-cases.json")));
        HashSet<string> coverage = new HashSet<string>(StringComparer.Ordinal);
        JsonElement firstInput = default;
        foreach (JsonElement scenario in scenarios.RootElement.GetProperty("cases").EnumerateArray()) {
            ImmutableDictionary<string, InputFact> inputs = new InputFactService().Parse(Encoding.UTF8.GetBytes(scenario.GetProperty("input").GetRawText()), knowledge);
            ConsultationService session = new ConsultationService(knowledge, inputs);
            string id = scenario.GetProperty("id").GetString()!;
            if (id == "T-01") firstInput = scenario.GetProperty("input").Clone();
            Check(session.Result.Status == InferenceStatus.Success && session.Outcome?.Value == scenario.GetProperty("expected_outcome").GetString(), id + ": исход");
            Check(session.Outcome!.Kind.ToString().ToLowerInvariant() == scenario.GetProperty("expected_kind").GetString(), id + ": вид исхода");
            HashSet<string> fired = session.Result.Steps.Select(step => step.RuleId).ToHashSet(StringComparer.Ordinal);
            foreach (JsonElement expected in scenario.GetProperty("expected_rules").EnumerateArray()) Check(fired.Contains(expected.GetString()!), id + ": обязательное правило");
            coverage.UnionWith(fired.Where(value => value.StartsWith("R-", StringComparison.Ordinal)));
        }
        Check(coverage.SetEquals(Enumerable.Range(1, 20).Select(value => "R-" + value.ToString("D2"))), "покрытие R-01–R-20 исходными сценариями");

        ConsultationService dialogue = new ConsultationService(knowledge);
        Check(dialogue.NextQuestion?.Id == "V1" && dialogue.ExplainWhy().Contains("R-01"), "первый вопрос и перспективное почему");
        InferenceResult before = dialogue.Result;
        Check(!dialogue.TryAnswer("V1", "ошибка", out _) && ReferenceEquals(before, dialogue.Result), "неверный ответ не меняет сеанс");
        Answer(dialogue, "V1", "да"); Answer(dialogue, "V2", "достаточные");
        Answer(dialogue, "V3", "отсутствует"); Answer(dialogue, "V4", "без ограничений");
        Check(dialogue.Outcome?.Value == "A1" && dialogue.NextQuestion is null, "штатный диалог не спрашивает несущественные входы");
        Check(dialogue.ExplainHow().Contains("R-13") && dialogue.ExplainHow().Contains("operator:V1"), "действительное объяснение с источником ответа");
        Answer(dialogue, "V3", "подтверждено");
        Check(!dialogue.Result.Facts.ContainsKey("outcome") && dialogue.Outcome is null, "исправление ответа удаляет старую рекомендацию");

        ConsultationService unknown = new ConsultationService(knowledge);
        int questions = 0;
        while (unknown.NextQuestion is FactDefinition question) {
            Answer(unknown, question.Id, "не знаю");
            Check(++questions <= 11, "неизвестные ответы не зацикливаются");
        }
        Check(unknown.Result.Status == InferenceStatus.InsufficientData && unknown.Outcome is null, "unknown не выбирает стратегию");
        unknown.Cancel();
        Check(!unknown.TryAnswer("V1", "да", out _) && unknown.NextQuestion is null, "отмена блокирует изменения сеанса");

        ConsultationService limited = new ConsultationService(knowledge);
        foreach (KeyValuePair<string, string> item in new Dictionary<string, string> {
            ["V1"] = "да",
            ["V2"] = "достаточные",
            ["V3"] = "подтверждено",
            ["V4"] = "только ограниченный режим",
            ["V5"] = "нет",
            ["V7"] = "нет"
        }) Answer(limited, item.Key, item.Value);
        Check(limited.Outcome?.Value == "A4" && !limited.Answers.ContainsKey("V6"), "доказанный запрет не требует неприменимого разрешения");

        ConsultationService recovery = new ConsultationService(knowledge);
        foreach (KeyValuePair<string, string> item in new Dictionary<string, string> {
            ["V1"] = "да",
            ["V2"] = "достаточные",
            ["V3"] = "подтверждено",
            ["V4"] = "запрещено",
            ["V7"] = "да",
            ["V8"] = "не знаю",
            ["V9"] = "да",
            ["V10"] = "да",
            ["V11"] = "сохранить доступную функцию"
        }) Answer(recovery, item.Key, item.Value);
        Check(recovery.Outcome is null && !recovery.Result.Facts.ContainsKey("PF-04"), "unknown разрешения не доказывает запрет восстановления");
        Answer(recovery, "V8", "да");
        Check(recovery.Outcome?.Value == "A3", "предпочтение не разрешает запрещенную A2");
        Answer(recovery, "V8", "нет");
        Check(recovery.Outcome?.Value == "A4", "после явного запрета восстановления применяется A4");
        ConsultationService priority = new ConsultationService(knowledge);
        Answer(priority, "V1", "нет"); Answer(priority, "V2", "неполные");
        Check(priority.Outcome?.Value == "NO_PROFILE" && priority.Result.Status != InferenceStatus.Conflict, "терминальная проверка профиля выражена условиями");

        JsonObject valid = JsonNode.Parse(File.ReadAllText(knowledgePath))!.AsObject();
        InvalidMetadata(valid, node => node["outcomes"]![0]!["kind"] = "automatic_action");
        InvalidMetadata(valid, node => node["outcomes"]!.AsArray().RemoveAt(0));
        InvalidMetadata(valid, node => node["question_order"]![1] = "V1");
        InvalidMetadata(valid, node => node["question_order"]![0] = "PF-01");

        DirectoryInfo temp = Directory.CreateTempSubdirectory("iot-lr1-dialog-");
        try {
            string mutable = Path.Combine(temp.FullName, "knowledge.json");
            string facts = Path.Combine(temp.FullName, "facts.json");
            File.WriteAllText(mutable, valid.ToJsonString());
            File.WriteAllText(facts, firstInput.GetRawText());
            ConsultationService pinned = new ConsultationService(new KnowledgeBaseService().Load(mutable));
            string oldHash = pinned.Knowledge.Sha256;
            valid["version"] = "lr1-port-2-test";
            File.WriteAllText(mutable, valid.ToJsonString());
            ConsultationService fresh = new ConsultationService(new KnowledgeBaseService().Load(mutable));
            Check(pinned.Knowledge.Sha256 == oldHash && fresh.Knowledge.Sha256 != oldHash, "новая загрузка не меняет прежнюю БЗ");
            StringWriter output = new StringWriter(); StringWriter errors = new StringWriter();
            int code = ConsultationConsole.Run(new[] { "--consult", "--knowledge", mutable },
                new StringReader("почему\nошибка\nда\nдостаточные\nотсутствует\nбез ограничений\nкак\nизменить V3 подтверждено\nфакты\nотмена\n"), output, errors);
            Check(code == 0 && output.ToString().Contains("Рекомендация: A1") && output.ToString().Contains("R-13")
                && output.ToString().Contains("Консультация отменена") && errors.ToString().Contains("ответ не принят"), "проверка диалога как оболочки");
            Check(ConsultationConsole.Run(new[] { "--consult", "--bad", "x" }, new StringReader(""), new StringWriter(), new StringWriter()) == 2, "неверные аргументы");
            Check(ConsultationConsole.Run(new[] { "--consult", "--knowledge", mutable + ".absent" }, new StringReader(""), new StringWriter(), new StringWriter()) == 1, "нет БЗ");
            StringWriter pinnedOutput = new StringWriter(); StringWriter pinnedError = new StringWriter();
            CallbackReader failingReload = new CallbackReader(index => {
                if (index == 0) { File.WriteAllText(mutable, "{broken}"); return "новая"; }
                return index == 1 ? "как" : "выход";
            });
            Check(ConsultationConsole.Run(new[] { "--consult", "--knowledge", mutable, "--facts", facts }, failingReload, pinnedOutput, pinnedError) == 0
                && pinnedError.ToString().Contains("прежний сеанс сохранен") && pinnedOutput.ToString().Contains("R-13"), "ошибка перезагрузки сохраняет прежний сеанс");
            File.WriteAllText(mutable, valid.ToJsonString());
            StringWriter reloadOutput = new StringWriter();
            CallbackReader successfulReload = new CallbackReader(index => {
                if (index == 0) { valid["version"] = "lr1-port-3-test"; File.WriteAllText(mutable, valid.ToJsonString()); return "новая"; }
                return "выход";
            });
            Check(ConsultationConsole.Run(new[] { "--consult", "--knowledge", mutable }, successfulReload, reloadOutput, new StringWriter()) == 0
                && reloadOutput.ToString().Contains("lr1-port-3-test"), "новая версия загружается без пересборки");
        }
        finally { temp.Delete(true); }
        Console.WriteLine("ЛР1: 12 исходных сценариев, покрытие 20 правил, unknown, диалог, коррекция и версия БЗ проверены; входы ручные");
    }

    private static void InvalidMetadata(JsonObject source, Action<JsonObject> change) {
        JsonObject copy = source.DeepClone().AsObject(); change(copy);
        try { new KnowledgeBaseService().Parse(Encoding.UTF8.GetBytes(copy.ToJsonString())); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("невалидные метаданные БЗ приняты");
    }
    private static void Answer(ConsultationService session, string id, string text) {
        Check(session.TryAnswer(id, text, out string error), id + ": " + error);
    }
    private sealed class CallbackReader : TextReader {
        private readonly Func<int, string?> next;
        private int index;
        public CallbackReader(Func<int, string?> next) { this.next = next; }
        public override string? ReadLine() => next(index++);
    }
    private static void Check(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
