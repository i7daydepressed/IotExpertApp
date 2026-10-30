using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Checks;
using IotExpertApp.Services.Telemetry;
using IotExpertApp.Services.Live;
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
                model.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
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
            dialogue.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
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
            dialogue.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ReferenceEquals(successful, dialogue.ResultSnapshot) && header == dialogue.SessionHeader, "ошибка нового сеанса сохраняет прежнюю БЗ и результат");
            dialogue.KnowledgePath = Path.Combine(AppContext.BaseDirectory, "KnowledgeExamples", "synthetic-engine.json");
            dialogue.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ReferenceEquals(successful, dialogue.ResultSnapshot) && dialogue.Message.Contains("schema_version 2"), "техническая схема без каталога не подменяет предметный сеанс GUI");
            dialogue.SelectedFact = dialogue.Answers.Single(row => row.Id == "V3");
            dialogue.EditText = "подтверждено"; dialogue.CorrectCommand.Execute(null);
            Check(!dialogue.ResultSnapshot!.Facts.ContainsKey("outcome") && !dialogue.OutcomeText.StartsWith("Рекомендация:", StringComparison.Ordinal), "исправление удаляет прежнюю рекомендацию GUI");
            dialogue.CancelCommand.Execute(null);
            Check(!dialogue.CanAnswer && !dialogue.CanCorrect && !dialogue.CanCancel && dialogue.TraceText.StartsWith("ИСТОРИЯ", StringComparison.Ordinal), "отмена отключает действия и маркирует историю");
            dialogue.KnowledgePath = path; dialogue.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
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
            CheckTypedForms(temp.FullName, path);
            CheckPinnedPresentations(path);
        }
        finally { temp.Delete(true); }
        Console.WriteLine("представление консультации: 12 сценариев VM, диалог, ошибка загрузки, коррекция, отмена и границы дерева пройдены; нативный GUI этим не проверен");
    }

    public const string SyntheticPresentation = """
    {"schema_version":2,"knowledge_base_id":"synthetic-step13","version":"test1","synthetic":true,
     "facts":[
       {"id":"B","name":"Тестовый bool","question":"Включить тестовое условие?","type":"bool","role":"input"},
       {"id":"E","name":"Тестовый enum","question":"Выберите тестовый вариант","type":"enum","role":"input","options":["0","обычный"]},
       {"id":"I","name":"Тестовое целое","question":"Введите целое 1…3","type":"integer","role":"input","min":1,"max":3},
       {"id":"N","name":"Тестовое число","question":"Введите число 0…1","type":"number","role":"input","min":0,"max":1},
       {"id":"G","name":"Искусственный исход","type":"enum","role":"goal","options":["DEMO"]}],
     "question_order":["B","E","I","N"],
     "rules":[{"id":"SYN13","title":"Искусственная проверка формы","priority":1,"condition":{"all":[
       {"fact_id":"B","op":"eq","value":true},{"fact_id":"E","op":"eq","value":"0"},
       {"fact_id":"I","op":"eq","value":2},{"fact_id":"N","op":"eq","value":0.5}]},
       "conclusions":[{"fact_id":"G","value":"DEMO"}],"explanation":"Только проверка UI","provenance":"synthetic STEP13, не измерение ESP32"}],
     "outcomes":[{"value":"DEMO","kind":"strategy","title":"Искусственный результат","description":"Только проверка форм, не рекомендация оборудованию"}]}
    """;
    private static void CheckTypedForms(string folder, string realPath) {
        string path = Path.Combine(folder, "synthetic-step13.json"); File.WriteAllText(path, SyntheticPresentation);
        ConsultationViewModel model = new ConsultationViewModel { KnowledgePath = path };
        model.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        ConsultationFormViewModel first = model.QuestionForm!;
        Check(first.Definition.Type == FactType.Boolean && first.Choices.Select(c => c.Canonical).SequenceEqual(new[] { "true", "false" }), "типизированный bool");
        first.Submit("false"); Check(model.ResultSnapshot!.Inputs["B"].Value!.Canonical == "false", "Нет не unknown");
        model.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        InferenceResult before = model.ResultSnapshot!;
        Check(!first.Submit("true") && ReferenceEquals(before, model.ResultSnapshot), "форма прежнего сеанса не отвечает новому");
        first = model.QuestionForm!; first.Submit("true"); before = model.ResultSnapshot!;
        Check(!first.Submit("false") && ReferenceEquals(before, model.ResultSnapshot), "дубль старой формы не отвечает следующему факту");
        Check(model.Answers.Count == 1 && !model.Answers.Any(r => r.Id == "E"), "неотвеченные факты не в полученных ответах");
        ConsultationFormViewModel enumeration = model.QuestionForm!;
        Check(enumeration.Choices.Select(c => c.Canonical).SequenceEqual(new[] { "0", "обычный" }), "enum из Definition, не индексы");
        enumeration.Submit("0"); Check(model.ResultSnapshot!.Inputs["E"].Value!.Canonical == "0", "числовой canonical enum не принят за номер кнопки");
        ConsultationFormViewModel integer = model.QuestionForm!; before = model.ResultSnapshot!;
        Check(integer.Definition.Type == FactType.Integer && !integer.Submit("") && !integer.Submit("1.5") && !integer.Submit("4") && ReferenceEquals(before, model.ResultSnapshot), "пустой/неверный integer сохраняет тот же результат");
        integer.Submit("2"); ConsultationFormViewModel number = model.QuestionForm!; before = model.ResultSnapshot!;
        Check(number.Definition.Type == FactType.Number && !number.Submit("1,5") && !number.Submit("1.1") && ReferenceEquals(before, model.ResultSnapshot), "number/точность/диапазон");
        number.Submit("0.5"); Check(model.ResultSnapshot!.Status == InferenceStatus.Success && model.OutcomeText.Contains("DEMO"), "все типы доходят до общего сервиса");
        model.ExplainResultCommand.Execute(null); ResultExplanationViewModel pinned = model.Explanation!;
        Check(ReferenceEquals(pinned.ResultSnapshot, model.ResultSnapshot) && pinned.Rules.Contains("SYN13") && !pinned.Rules.Contains("R-01"), "объяснение содержит фактические Steps, не prospective why");
        Check(!pinned.Context.Contains("SHA256", StringComparison.Ordinal) && pinned.Technical.Contains(pinned.ResultSnapshot.KnowledgeBase.Sha256, StringComparison.Ordinal), "полный хеш сохранен в техническом раскрытии");
        model.SelectedFact = model.Answers.Single(r => r.Id == "E"); ConsultationFormViewModel edit = model.EditForm!;
        Check(!model.CanAnswer && edit.Editing && edit.Definition.Type == FactType.Enum, "исправление отдельной типизированной формой");
        edit.Submit("обычный"); Check(model.Explanation is null && model.ResultSnapshot!.Status != InferenceStatus.Success && !model.ResultSnapshot.Facts.ContainsKey("G"), "исправление снимает старый итог и объяснение");
        before = model.ResultSnapshot!; Check(!edit.Submit("0") && ReferenceEquals(before, model.ResultSnapshot), "старое исправление погашено");
        ConsultationAnswerRow oldRow = model.Answers.First();
        model.KnowledgePath = realPath; model.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult(); before = model.ResultSnapshot!;
        oldRow.EditCommand!.Execute(null); model.SelectedFact = oldRow;
        Check(model.EditForm is null && ReferenceEquals(before,model.ResultSnapshot),"устаревшая строка исправления не попадает в новый сеанс");
        model.InitialFactsPath = Path.Combine(folder,"bad-facts.json"); File.WriteAllText(model.InitialFactsPath,"{broken}");
        model.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult(); Check(ReferenceEquals(before,model.ResultSnapshot) && !model.IsLoading,"ошибка начальных фактов не заменяет сеанс");
        model.InitialFactsPath=""; model.UnknownCommand.Execute(null);
        Check(model.Answers.Single().Value=="Не знаю" && model.ResultSnapshot!.Inputs["V1"].Value is null,"unknown есть в ответах отдельно от отсутствия");
        foreach (InferenceStatus status in new[] {InferenceStatus.NoMatch,InferenceStatus.Conflict,InferenceStatus.StepLimit,InferenceStatus.InsufficientData}) {
            ResultExplanationViewModel display = new ResultExplanationViewModel(before with {Status=status},"synthetic status");
            Check(display.Status!=ResultExplanationViewModel.StatusLabel(InferenceStatus.Success) && !Titles(display.TreeRoots).Contains("Доказательство исхода"),"неуспех не имеет доказанного конечного исхода");
        }
    }
    private static void CheckPinnedPresentations(string path) {
        LiveMonitorViewModel live = new LiveMonitorViewModel {ProfilePath=Path.Combine(AppContext.BaseDirectory,"DeviceProfiles","esp32-observability.json")};
        live.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        TelemetryParser parser = new TelemetryParser(); Check(parser.TryParse(ReceiverChecks.CreateSyntheticPacket(),out TelemetryPacket? parsed,out string error),error);
        TelemetryPacket packet = parsed! with {DeviceId=live.Profile!.DeviceId};
        ReceiverState state = new ReceiverState("данные","synthetic-step13",1,0,0,0,0,0,0,"",new ReceivedSnapshot(packet,DateTimeOffset.UtcNow,"synthetic-step13"),0,true,false,false,"","") {IsSerial=true};
        live.Update(state); live.FreezeCommand.Execute(null);
        LiveAssessment frozen = live.Frozen!; ResultExplanationViewModel explanation = live.FrozenStrategyExplanation!;
        live.Update(state with {Running=false});
        live.Update(state with {Snapshot=new ReceivedSnapshot(packet with {BootId=new string('c',32),Sequence=2},DateTimeOffset.UtcNow,state.Origin)});
        live.ProfilePath=path; live.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(ReferenceEquals(live.Frozen,frozen) && ReferenceEquals(explanation,live.FrozenStrategyExplanation) && live.ExplanationHistory.StartsWith("История"),"live Stop/boot/ошибка профиля не меняют pinned snapshot/дерево");
        live.FreezeCommand.Execute(null); Check(!ReferenceEquals(explanation,live.FrozenStrategyExplanation),"явное открытие нового live результата");
        CheckEvent terminal = File.ReadLines(Path.Combine(AppContext.BaseDirectory,"CheckExamples","interactive-oled-synthetic.jsonl"))
            .Where(l=>l.StartsWith("TEST_EVENT ",StringComparison.Ordinal)).Select(l=>{Check(CheckProtocol.TryParse(l,out CheckEvent? e,out string why),why);return e!;})
            .First(e=>e.Source=="oled"&&e.Operation=="display_hold"&&e.Stage=="completed");
        int opened=0;
        TelemetryReceiverService receiver = new TelemetryReceiverService(new ObservationStore(),(_,_)=>{opened++;throw new InvalidOperationException("UI не должен открывать UART");});
        ModuleChecksViewModel modules = new ModuleChecksViewModel(receiver);
        foreach(string? answer in new string?[]{"Да","Нет",null}) {
            GuidedCheckResult result = new GuidedCheckResult(terminal.Device,terminal.Boot,terminal.Firmware,terminal.Config,terminal.RequestId!.Value,"oled","display_hold","synthetic-step13",2,5,null,live.Profile!.ModuleKnowledge,
                "finished",answer is null?"timeout":"answered",TimeSpan.Zero,TimeSpan.Zero,null,terminal,
                answer is null?null:new HumanCheckAnswer(terminal.Device,terminal.Boot,terminal.RequestId.Value,"oled",answer,DateTimeOffset.UtcNow,"synthetic"),true,null);
            result=result with {Inference=ModuleCheckAssessment.Run(result)};modules.PinExplanation(result);
            ResultExplanationViewModel proof=modules.FrozenModuleExplanation!;GuidedCheckResult selected=modules.FrozenGuided!;
            modules.Update(receiver.GetState());
            Check(ReferenceEquals(selected,modules.FrozenGuided)&&ReferenceEquals(proof,modules.FrozenModuleExplanation),"текущий Update не подменяет выбранную редакцию модуля");
            string expected=answer=="Да"?"oled_seen":answer=="Нет"?"oled_not_seen":"no_answer";
            Check(proof.Outcome.StartsWith(expected,StringComparison.Ordinal)&&ReferenceEquals(proof.ResultSnapshot,result.Inference),"модуль объясняет сохраненный Inference "+expected);
            if(answer is null)Check(result.Human is null&&modules.ModuleExplanationContext.Contains("Ответ не получен за 5"),"timeout не человеческое Нет/не PASS");
        }
        Check(opened==0 && receiver.GetChecks().PendingId is null,"открытие объяснений не открывает UART/не ставит команды");
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
