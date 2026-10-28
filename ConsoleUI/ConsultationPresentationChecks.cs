using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Consultation;
using IotExpertApp.Services.Knowledge;
using IotExpertApp.ViewModels;

namespace IotExpertApp.ConsoleUI;

public static class ConsultationPresentationChecks {
    public static void Run() {
        string path = Path.Combine(AppContext.BaseDirectory, "Knowledge", "lr1-strategies.json");
        KnowledgeBase knowledge = new KnowledgeBaseService().Load(path);
        using JsonDocument scenarios = KnowledgeJson.Parse(KnowledgeJson.ReadBounded(Path.Combine(AppContext.BaseDirectory, "Scenarios", "lr1-cases.json")));
        DirectoryInfo temp = Directory.CreateTempSubdirectory("iot-presentation-");
        try {
            string factsPath = Path.Combine(temp.FullName, "facts.json");
            int checkedCases = 0;
            foreach (JsonElement scenario in scenarios.RootElement.GetProperty("cases").EnumerateArray()) {
                string raw = scenario.GetProperty("input").GetRawText();
                File.WriteAllText(factsPath, raw);
                ConsultationViewModel model = new ConsultationViewModel { KnowledgePath = path, InitialFactsPath = factsPath };
                model.StartCommand.Execute(null);
                ConsultationService direct = new ConsultationService(knowledge, new InputFactService().Parse(Encoding.UTF8.GetBytes(raw), knowledge));
                InferenceResult actual = model.ResultSnapshot ?? throw new InvalidOperationException("GUI VM не начала сеанс");
                Check(actual.Status == direct.Result.Status && actual.Facts["outcome"].Value == direct.Result.Facts["outcome"].Value, "исход VM совпадает с общим сервисом");
                Check(actual.Steps.Select(step => step.RuleId).SequenceEqual(direct.Result.Steps.Select(step => step.RuleId)), "трасса VM совпадает с общим сервисом");
                Check(model.TraceText == direct.ExplainHow(), "текст GUI использует тот же закрепленный результат");
                Check(model.OutcomeText.Contains(scenario.GetProperty("expected_outcome").GetString()!, StringComparison.Ordinal), "в GUI виден предметный исход");
                Check(Titles(model.TreeRoots).Contains("Доказательство исхода"), "дерево успешного исхода");
                checkedCases++;
            }
            Check(checkedCases == 12, "проверены двенадцать сценариев в VM");

            ConsultationViewModel dialogue = new ConsultationViewModel { KnowledgePath = path };
            dialogue.StartCommand.Execute(null);
            Check(dialogue.QuestionText.StartsWith("V1:", StringComparison.Ordinal), "первый вопрос VM");
            InferenceResult before = dialogue.ResultSnapshot!;
            dialogue.AnswerText = "ошибка"; dialogue.AnswerCommand.Execute(null);
            Check(ReferenceEquals(before, dialogue.ResultSnapshot) && dialogue.Message.Contains("не принят"), "невалидный ввод не меняет снимок");
            dialogue.WhyCommand.Execute(null);
            Check(dialogue.WhyText.Contains("R-01"), "почему использует сервис");
            foreach (string answer in new[] { "да", "достаточные", "отсутствует", "без ограничений" }) {
                dialogue.AnswerText = answer; dialogue.AnswerCommand.Execute(null);
            }
            Check(dialogue.OutcomeText.StartsWith("Рекомендация: A1", StringComparison.Ordinal), "диалог VM получает A1");
            Check(Titles(dialogue.TreeRoots).Any(title => title.Contains("operator:V1", StringComparison.Ordinal)), "в дереве сохранено происхождение ответа");
            InferenceResult successful = dialogue.ResultSnapshot!;
            string header = dialogue.SessionHeader;
            dialogue.KnowledgePath = Path.Combine(temp.FullName, "broken.json");
            File.WriteAllText(dialogue.KnowledgePath, "{broken}");
            dialogue.StartCommand.Execute(null);
            Check(ReferenceEquals(successful, dialogue.ResultSnapshot) && header == dialogue.SessionHeader, "ошибка нового сеанса сохраняет прежнюю БЗ и результат");
            dialogue.KnowledgePath = Path.Combine(AppContext.BaseDirectory, "KnowledgeExamples", "synthetic-engine.json");
            dialogue.StartCommand.Execute(null);
            Check(ReferenceEquals(successful, dialogue.ResultSnapshot) && dialogue.Message.Contains("schema_version 2"), "техническая схема без каталога не подменяет предметный сеанс GUI");
            dialogue.SelectedFact = dialogue.Answers.Single(row => row.Id == "V3");
            dialogue.EditText = "подтверждено"; dialogue.CorrectCommand.Execute(null);
            Check(!dialogue.ResultSnapshot!.Facts.ContainsKey("outcome") && !dialogue.OutcomeText.StartsWith("Рекомендация:", StringComparison.Ordinal), "исправление удаляет прежнюю рекомендацию GUI");
            dialogue.CancelCommand.Execute(null);
            Check(!dialogue.CanAnswer && !dialogue.CanCorrect && !dialogue.CanCancel && dialogue.TraceText.StartsWith("ИСТОРИЯ", StringComparison.Ordinal), "отмена отключает действия и маркирует историю");
            dialogue.KnowledgePath = path; dialogue.StartCommand.Execute(null);
            int unknownCount = 0;
            while (dialogue.CanAnswer) {
                dialogue.UnknownCommand.Execute(null);
                Check(++unknownCount <= 11, "unknown не зацикливает вопросы VM");
            }
            Check(dialogue.ResultSnapshot!.Status == InferenceStatus.InsufficientData && !dialogue.ResultSnapshot.Facts.ContainsKey("outcome"), "unknown не превращается в стратегию");

            ExplanationTreeBuilder builder = new ExplanationTreeBuilder();
            InferenceConflict conflict = new InferenceConflict("outcome", successful.Facts["outcome"],
                new Conclusion("outcome", new KnowledgeValue(FactType.Enum, "A2")), "R-16", successful.Steps[0].Condition);
            ImmutableArray<ExplanationTreeNode> conflictTree = builder.Build(successful with { Status = InferenceStatus.Conflict, Conflict = conflict });
            Check(!Titles(conflictTree).Contains("Доказательство исхода") && Titles(conflictTree).Any(title => title.StartsWith("Конфликт outcome:", StringComparison.Ordinal)), "конфликт не представлен как доказанный исход");
            ProvenFact cyclic = successful.Facts["allow_A1"] with {
                Evidence = ImmutableArray.Create(new FactEvidence("cycle-check", "R-09", ImmutableArray.Create("allow_A1")))
            };
            ImmutableArray<ExplanationTreeNode> cyclicTree = builder.Build(successful with { Facts = successful.Facts.SetItem("allow_A1", cyclic) });
            Check(Titles(cyclicTree).Any(title => title.Contains("повторная ссылка", StringComparison.Ordinal)), "цикл представления обрывается");
            ImmutableArray<ExplanationTreeNode> limited = builder.Build(successful with { Steps = Enumerable.Repeat(successful.Steps[0], 1000).ToImmutableArray() });
            List<string> limitedTitles = Titles(limited).ToList();
            Check(limitedTitles.Count <= 512 && limitedTitles.Any(title => title.StartsWith("Предел отображения", StringComparison.Ordinal)), "большая трасса ограничена с явным признаком сокращения");
        }
        finally { temp.Delete(true); }
        Console.WriteLine("представление консультации: 12 сценариев VM, диалог, ошибка загрузки, коррекция, отмена и границы дерева пройдены; нативный GUI этим не проверен");
    }

    private static IEnumerable<string> Titles(IEnumerable<ExplanationTreeNode> nodes) {
        foreach (ExplanationTreeNode node in nodes) {
            yield return node.Title;
            foreach (string title in Titles(node.Children)) yield return title;
        }
    }

    private static void Check(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
