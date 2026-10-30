using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Checks;
using IotExpertApp.Services.Live;
using IotExpertApp.Services.Telemetry;
using IotExpertApp.ViewModels;

namespace IotExpertApp.ConsoleUI;

public static class EvidenceAndVirtualChecks {
    public static void Run(string syntheticJson) {
        string path = Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "esp32-observability.json");
        DeviceProfile profile = new DeviceProfileService().Load(path);
        Check(new TelemetryParser().TryParse(syntheticJson, out TelemetryPacket? parsed, out string error), error);
        TelemetryPacket packet = parsed! with { DeviceId = profile.DeviceId };
        ReceiverState state = new ReceiverState("прием", "synthetic: step13a fake", 1, 0, 0, 0, 0, 0, 0, "",
            new ReceivedSnapshot(packet, DateTimeOffset.UtcNow, "synthetic: step13a fake"), 0, true, false, false, "", "") { IsSerial = true, ContextId = 41 };
        EvidenceAssessmentService chain = new EvidenceAssessmentService();
        LiveAssessmentService live = new LiveAssessmentService();
        GuidedCheckResult Result(string source, string? answer, string stage = "completed", ulong id = 1, string end = "answered", bool transitions = false) {
            string data = source switch {
                "oled" => "{\"transfer_ok\":true,\"dashboard_restored\":true}",
                "relay_1" or "relay_2" => "{\"in1_level\":1,\"in2_level\":1,\"off_callback_ms\":200}",
                "microsd" => "{\"test_passed\":true,\"cleanup_ok\":true,\"file_retained\":false}",
                _ => "{\"transition_count\":" + (transitions ? "2" : "0") + "}"
            };
            CheckEvent machine = new CheckEvent("result", packet.DeviceId, packet.BootId, packet.FirmwareVersion, packet.ConfigVersion,
                id, null, null, source, CheckProtocol.Operation(source), stage, "machine_check_complete", false, id, null, null, null,
                null, null, CheckProtocol.Human(source), JsonDocument.Parse(data).RootElement.Clone(), null);
            HumanCheckAnswer? human = answer is null ? null : new HumanCheckAnswer(packet.DeviceId, packet.BootId, id, source, answer, DateTimeOffset.UtcNow, "synthetic fake");
            GuidedCheckResult result = new GuidedCheckResult(packet.DeviceId, packet.BootId, packet.FirmwareVersion, packet.ConfigVersion, id, source,
                machine.Operation!, state.Origin, 1, 30, null, profile.ModuleKnowledge, "finished", end, TimeSpan.Zero, TimeSpan.Zero, null, machine, human, false, null) {
                ContextId = state.ContextId,
                CompletedAt = DateTimeOffset.UtcNow.AddDays(-2),
                Revision = 1
            };
            return result with { Inference = ModuleCheckAssessment.Run(result) };
        }
        string Goal(LiveAssessment assessment) => assessment.Strategy?.Status == InferenceStatus.Success ? LiveAssessmentService.Goal(assessment.Strategy) : assessment.Strategy?.Status.ToString() ?? "none";
        GuidedCheckResult negative = Result("oled", "Нет");
        ImmutableArray<GuidedCheckResult> history = ImmutableArray.Create(negative);
        Check(Goal(live.Assess(profile, state, TimeSpan.Zero, false, history)) == "A1", "учет выключен: прежняя A1");
        foreach (GuidedCheckResult adverse in new[] { negative, Result("relay_1", "Нет"), Result("microsd", null, "failed"), Result("pir", "Нет") }) {
            LiveAssessment result = live.Assess(profile, state, TimeSpan.Zero, true, ImmutableArray.Create(adverse));
            Check(Goal(result) == "A2" && result.Evaluation!.Evidence!.Steps.Any(s => s.RuleId == "LINK-NEGATIVE")
                && result.Strategy!.Steps.Any(s => s.RuleId == "R-14"), "отрицательное основание через связь и R-14: " + adverse.Source);
            Check(LiveAssessmentService.Goal(result.Diagnostic!) == "ready", "отрицательный ответ не подменяет OBS");
        }
        DeviceProfile restricted = profile with { Defaults = profile.Defaults.SetItem("V5", new KnowledgeValue(FactType.Boolean, "false")) };
        Check(Goal(live.Assess(restricted, state, TimeSpan.Zero, true, history)) != "A2", "A2 не назначается без разрешенного ограниченного режима");
        foreach ((string? answer, string end, string normalized) in new (string?, string, string)[] {
            ("Не знаю", "answered", "unknown"), ("Неясно", "answered", "unclear"), ("Не проверял", "answered", "skipped"), (null, "timeout", "timeout"), (null, "early_end", "absent") }) {
            LiveAssessment result = live.Assess(profile, state, TimeSpan.Zero, true, ImmutableArray.Create(Result("oled", answer, end: end)));
            Check(result.Evaluation!.Evidence!.Inputs["human_oled"].Value!.Canonical == normalized && Goal(result) == "NEED_DATA", "неподтверждение отдельно от Нет: " + normalized);
        }
        GuidedCheckResult sd = Result("microsd", null, end: "machine");
        LiveAssessment sdResult = live.Assess(profile, state, TimeSpan.Zero, true, ImmutableArray.Create(sd));
        Check(Goal(sdResult) == "A1" && sdResult.Evaluation!.Evidence!.Facts["check_microsd"].Value.Canonical == "positive", "SD без вопроса не требует Human");
        GuidedCheckResult mismatch = Result("pir", "Нет", transitions: true);
        LiveAssessment uncertain = live.Assess(profile, state, TimeSpan.Zero, true, ImmutableArray.Create(mismatch));
        Check(LiveAssessmentService.Goal(uncertain.Evaluation!.Evidence!) == "clarify" && Goal(uncertain) != "A1", "расхождение не является безусловной нормой");
        Check(Goal(live.Assess(profile, state, TimeSpan.Zero, true, ImmutableArray.Create(mismatch, negative))) == "A2", "отрицание другого модуля сильнее расхождения");
        TelemetryPacket missing = packet with { Observations = packet.Observations.Select(o => o.SourceId == "oled" ? o with { Enabled = false } : o).ToImmutableArray() };
        Check(Goal(live.Assess(profile, state with { Snapshot = new ReceivedSnapshot(missing, DateTimeOffset.UtcNow, state.Origin) }, TimeSpan.Zero, true, history)) == "NEED_DATA", "неизвестные обязательные OBS сильнее отрицания");
        foreach (GuidedCheckResult foreign in new[] { negative with { ContextId = 40 }, negative with { Boot = new string('b', 32) }, negative with { Knowledge = profile.ModuleKnowledge! with { Sha256 = "other" } } }) {
            EvidenceSelection excluded = chain.Select(profile, state, ImmutableArray.Create(foreign));
            Check(excluded.Items.Length == 0 && excluded.Excluded.Length == 1, "чужое свидетельство исключено с причиной");
        }
        GuidedCheckResult active = negative with { RequestId = 2, Phase = "starting", Revision = 2 };
        Check(chain.Select(profile, state, history.Add(active)).Items.Single().RequestId == 1, "новый старт не снимает замечание");
        Check(chain.Select(profile, state, history.Add(Result("oled", null, "rejected", id: 2))).Items.Single().RequestId == 1, "отклоненный запрос не снимает прежнее замечание");
        GuidedCheckResult positive = Result("oled", "Да", id: 2);
        Check(chain.Select(profile, state, history.Add(positive)).Items.Single().RequestId == 2, "последний подходящий завершенный результат заменяет старый");
        Check(EvidenceAssessmentService.Describe(chain.Select(profile, state, history)).Contains("возраст", StringComparison.Ordinal), "старый результат имеет видимый возраст, без произвольного expiry");

        LiveMonitorViewModel monitor = new LiveMonitorViewModel { ProfilePath = path };
        monitor.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult(); monitor.Update(state, history);
        Check(!monitor.IncludeCheckAnswers && Goal(monitor.Latest!) == "A1", "галочка выключена по умолчанию");
        monitor.IncludeCheckAnswers = true; Check(Goal(monitor.Latest!) == "A2", "галочка пересчитывает тот же snapshot");
        monitor.FreezeCommand.Execute(null); LiveAssessment pinned = monitor.Frozen!;
        monitor.Update(state, ImmutableArray.Create(positive with { Revision = 3 }));
        Check(Goal(monitor.Latest!) == "A1" && ReferenceEquals(pinned, monitor.Frozen) && pinned.Evaluation!.Witnesses.Single().Human == "no"
            && monitor.FrozenChecksSummary.Contains("редакция 1", StringComparison.Ordinal), "Human/Revision без нового snapshot и закрепление режима/свидетельства");
        monitor.IncludeCheckAnswers = false; Check(Goal(monitor.Latest!) == "A1", "выключение возвращает OBS");
        monitor.Update(state, history); monitor.IncludeCheckAnswers = true;
        Check(Goal(monitor.Latest!) == "A2", "повторное включение не теряет ответы");
        monitor.Update(state with { ContextId = 42 }, history); Check(Goal(monitor.Latest!) == "A1", "новое подключение исключает историю при том же snapshot");

        VirtualStandViewModel stand = new VirtualStandViewModel(monitor); stand.UseProfile(profile);
        Check(stand.Modules.Count == 9 && stand.Conditions.Select(c => c.Definition.Id).ToHashSet().SetEquals(Enumerable.Range(1, 11).Select(i => "V" + i)), "девять модулей и все V1..V11");
        Check(stand.Modules.All(m => m.Observation == "unknown") && stand.Conditions.Single(c => c.Definition.Id == "V5").OriginText == "Из профиля", "старт неизвестен, defaults подписаны");
        foreach (VirtualModuleViewModel module in stand.Modules) module.SelectedObservation = module.ObservationOptions.Single(c => c.Canonical == "ok");
        VirtualModuleViewModel oled = stand.Modules.Single(m => m.Source == "oled");
        oled.SelectedMachine = oled.MachineOptions.Single(c => c.Canonical == "ok"); oled.SelectedHuman = oled.HumanOptions.Single(c => c.Canonical == "no");
        LiveAssessment physicalFake = live.Assess(profile, state, TimeSpan.Zero, true, history);
        Check(LiveAssessmentService.Goal(stand.Result!.Strategy) == Goal(physicalFake)
            && stand.Result.Strategy.Steps.Select(s => s.RuleId).SequenceEqual(physicalFake.Strategy!.Steps.Select(s => s.RuleId))
            && stand.Result.Evidence!.Steps.Select(s => s.RuleId).SequenceEqual(physicalFake.Evaluation!.Evidence!.Steps.Select(s => s.RuleId))
            && stand.Result.Witnesses.Single().Origin != physicalFake.Evaluation.Witnesses.Single().Origin, "одинаковые основания дают одинаковые цели/правила, происхождение разное");
        oled.SelectedMachine = oled.MachineOptions.Single(c => c.Canonical == "rejected");
        Check(LiveAssessmentService.Goal(stand.Result!.Strategy) == "A2", "не выполненный новый тест не снимает виртуальное замечание");
        oled.SelectedMachine = oled.MachineOptions.Single(c => c.Canonical == "ok");
        stand.ExplainCommand.Execute(null); Check(stand.StrategyExplanation is not null, "объяснение сохраненного виртуального результата");
        VirtualFactViewModel v4 = stand.Conditions.Single(c => c.Definition.Id == "V4"); v4.UnknownCommand.Execute(null);
        Check(stand.Result!.Strategy.Inputs["V4"].Value is null && v4.HasOverride && v4.OriginText == "Переопределено вручную"
            && stand.MissingTarget == "V4" && stand.Missing.Contains("разрешено ли продолжать", StringComparison.Ordinal) && stand.StrategyExplanation is null, "неизвестный override V4 имеет приоритет, старое объяснение снято");
        string focus = ""; stand.FocusRequested += id => focus = id; stand.FocusMissingCommand.Execute(null); Check(focus == "V4", "переход к конкретной карточке");
        oled.SelectedHuman = oled.HumanOptions.Single(c => c.Canonical == "yes"); Check(stand.Result!.Strategy.Inputs["V4"].Value is null, "модуль не снимает явный override");
        v4.ResetCommand.Execute(null); Check(!v4.HasOverride && LiveAssessmentService.Goal(stand.Result!.Strategy) == "A1", "сброс снова включает профильную цепочку");
        VirtualFactViewModel v11 = stand.Conditions.Single(c => c.Definition.Id == "V11");
        Check(v11.Choices.Select(c => c.Canonical).SequenceEqual(profile.StrategyKnowledge.Facts["V11"].Options), "варианты взяты из Definition");
        v11.ChooseCommand.Execute(v11.Choices.Single(c => c.Canonical == "не задан")); Check(stand.Result!.Strategy.Inputs["V11"].Value?.Canonical == "не задан", "V11 не задан — значение enum");
        v11.UnknownCommand.Execute(null); Check(stand.Result!.Strategy.Inputs["V11"].Value is null, "неизвестность V11 — null"); v11.ResetCommand.Execute(null);
        long revision = stand.Revision; ProfileEvaluation virtualPinned = stand.Result!;
        stand.OfferProfile(profile); monitor.Update(state); stand.OfferProfile(profile with { Version = "new offered" }); stand.OpenConsultationCommand.Execute(null); stand.CloseConsultationCommand.Execute(null);
        Check(ReferenceEquals(virtualPinned, stand.Result) && stand.Revision == revision && oled.SelectedHuman!.Canonical == "yes", "live/предложение профиля/навигация не заменяют ручной сценарий");
        int opened = 0;
        TelemetryReceiverService receiver = new TelemetryReceiverService(new ObservationStore(), (_, _) => { opened++; throw new InvalidOperationException("UART запрещен в этой fake-проверке"); });
        monitor.IncludeCheckAnswers = !monitor.IncludeCheckAnswers; stand.ExplainCommand.Execute(null); v4.UnknownCommand.Execute(null); v4.ResetCommand.Execute(null);
        Check(opened == 0 && receiver.GetChecks().History.Length == 0 && receiver.GetChecks().PendingId is null, "действия VM не открывают транспорт и не создают команды");
        receiver.DisposeAsync().AsTask().GetAwaiter().GetResult();

        DirectoryInfo temporary = Directory.CreateTempSubdirectory("iot-evidence-check-");
        try {
            JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (string key in new[] { "diagnostic_knowledge", "strategy_knowledge", "module_check_knowledge" }) json[key] = Path.GetRelativePath(temporary.FullName, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, json[key]!.GetValue<string>())));
            json["evidence_policy"]!["knowledge"] = Path.GetRelativePath(temporary.FullName, Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "esp32-evidence.rules.json"));
            json["evidence_policy"]!["modules"]![0]!["kind"] = "wrong";
            string invalid = Path.Combine(temporary.FullName, "invalid.json"); File.WriteAllText(invalid, json.ToJsonString());
            DeviceProfile old = monitor.Profile!; monitor.ProfilePath = invalid; monitor.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ReferenceEquals(old, monitor.Profile) && monitor.LoadMessage.StartsWith("Профиль не заменен:", StringComparison.Ordinal), "невалидная политика не публикует половину комплекта");
            json.Remove("evidence_policy"); File.WriteAllText(invalid, json.ToJsonString()); DeviceProfile legacy = new DeviceProfileService().Load(invalid);
            Check(legacy.EvidencePolicy is null && Goal(live.Assess(legacy, state, TimeSpan.Zero, true, history)) == "A1", "старый профиль без расширения сохраняет прежний расчет");
        }
        finally { temporary.Delete(true); }
        Console.WriteLine("13А: fake OBS/ответы/контекст/Revision, общие правила live и virtual, V1..V11/override/focus, закрепление и старый профиль проверены; физический стенд не проверялся");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("13А: " + message); }
}
