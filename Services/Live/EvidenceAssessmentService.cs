using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Checks;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.Services.Live;

// оба режима передают нормализованные основания; решения и переход к V2/V3/V4 задают внешние БЗ и mapping
public sealed class EvidenceAssessmentService {
    public static readonly string[] Machines = { "none", "pending", "ok", "error", "rejected", "unknown" };
    public static readonly string[] Humans = { "absent", "not_required", "yes", "no", "unknown", "unclear", "skipped", "timeout" };
    public static readonly string[] Signals = { "not_applicable", "changes", "no_changes", "unknown" };
    public static readonly string[] States = { "none", "unfinished", "positive", "machine_error", "negative", "mismatch", "unknown", "unclear", "skipped", "timeout", "absent", "not_executed" };
    // новое подключение с тем же портом и boot все равно имеет другой ContextId; старые ответы не переносим
    public EvidenceSelection Select(DeviceProfile profile, ReceiverState state, ImmutableArray<GuidedCheckResult> history) {
        List<ModuleEvidence> selected = new List<ModuleEvidence>(); List<string> excluded = new List<string>();
        TelemetryPacket? packet = state.Snapshot?.Packet;
        foreach (GuidedCheckResult result in history.IsDefault ? ImmutableArray<GuidedCheckResult>.Empty : history) {
            string? reason = !state.IsSerial || !state.Running || packet is null ? "нет текущего serial-контекста"
                : result.ContextId <= 0 || result.ContextId != state.ContextId || result.Origin != state.Origin ? "предыдущее подключение"
                : result.Device != packet.DeviceId || result.Boot != packet.BootId || result.Config != packet.ConfigVersion || result.Firmware != packet.FirmwareVersion ? "другая identity/boot/config/firmware"
                : profile.DeviceId != packet.DeviceId || profile.ConfigVersion != packet.ConfigVersion ? "профиль не соответствует устройству"
                : result.Phase != "finished" || result.Machine?.Stage is not ("completed" or "failed") ? "проверка не выполнена, не завершена или исход не подтвержден"
                : profile.ModuleKnowledge is null || result.Knowledge?.Sha256 != profile.ModuleKnowledge.Sha256 || result.Inference?.KnowledgeBase.Sha256 != profile.ModuleKnowledge.Sha256 ? "несовместимая или отсутствующая закрепленная БЗ"
                : profile.EvidencePolicy?.Modules.Any(m => m.Source == result.Source && (m.Required || m.InfluencingOutcomes.Count > 0)) != true ? "модуль не входит в политику профиля" : null;
            if (reason is not null) { excluded.Add(result.Source + " / " + result.RequestId + ": " + reason); continue; }
            ModuleEvidence evidence = Normalize(result);
            selected.RemoveAll(e => e.Source == evidence.Source); selected.Add(evidence);
        }
        return new EvidenceSelection(selected.ToImmutableArray(), excluded.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }
    public static ModuleEvidence Normalize(GuidedCheckResult result) {
        bool True(string id) => result.Inference?.Inputs.TryGetValue(id, out InputFact? input) == true && input.Value?.Canonical == "true";
        string machine = result.Machine?.Stage == "failed" ? "error" : result.Machine?.Stage is "rejected" or "cancelled" ? "rejected"
            : result.Active ? "pending" : result.Machine?.Stage != "completed" || result.Inference?.Status != InferenceStatus.Success || !True("reason_known") ? "unknown"
            : True("machine_ok") ? "ok" : result.Inference.Inputs["machine_ok"].Value?.Canonical == "false" ? "error" : "unknown";
        string human = CheckProtocol.Human(result.Source) is null ? "not_required" : result.Human?.Answer switch {
            "Да" => "yes",
            "Нет" => "no",
            "Не знаю" => "unknown",
            "Неясно" => "unclear",
            "Не проверял" => "skipped",
            _ => result.EndReason == "timeout" || result.ResponseOutcome == "timeout" ? "timeout" : "absent"
        };
        string signal = result.Source is "light" or "ir" or "pir" ? result.Inference?.Inputs.TryGetValue("has_transitions", out InputFact? transitions) == true
            ? transitions.Value?.Canonical switch { "true" => "changes", "false" => "no_changes", _ => "unknown" } : "unknown" : "not_applicable";
        string outcome = result.Inference?.Status == InferenceStatus.Success ? LiveAssessmentService.Goal(result.Inference) : result.Inference?.Status.ToString() ?? "не сохранен";
        string origin = result.Origin + "; source=" + result.Source + "; request=" + result.RequestId + "; Revision=" + result.Revision + "; context=" + result.ContextId
            + "; boot=" + result.Boot + "; module_outcome=" + outcome + "; human_at=" + result.Human?.AnsweredAt.ToString("O")
            + "; module_kb=" + result.Knowledge?.Sha256 + "; completed=" + result.CompletedAt?.ToString("O");
        return new ModuleEvidence(result.Source, machine, human, signal, origin, result.Revision, result.RequestId, result.CompletedAt, outcome, result);
    }
    public static string Key(EvidenceSelection selection) => string.Join("|", selection.Items.Select(e => e.Source + "/" + e.Machine + "/" + e.Human + "/" + e.Signal + "/" + e.Origin));
    public static string Describe(EvidenceSelection selection) => (selection.Items.Length == 0 ? "Подходящих результатов проверок пока нет" : string.Join("\n", selection.Items.Select(e => e.Source
        + ": machine=" + e.Machine + ", human=" + e.Human + "; " + e.ModuleOutcome + "; ID " + e.RequestId + "; редакция " + e.Revision
        + (e.CompletedAt.HasValue ? "; завершено " + e.CompletedAt.Value.ToString("O") + "; возраст " + Math.Max(0, (DateTimeOffset.UtcNow - e.CompletedAt.Value).TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + " с" : "; время завершения не сохранено")
        + ". Исторический тест текущего контекста; действует до следующего завершенного теста, не аппаратный PASS")))
        + (selection.Excluded.Length > 0 ? "\nИсключены: " + string.Join("; ", selection.Excluded) : "");

    // на входе статусы наблюдений и отдельно нормализованные Machine/Human/Signal с происхождением
    // OBS доказывает достаточность наблюдений; при включенном учете профильная БЗ сочетает ее с проверками
    // доказанный итог через mapping кладем в единственный словарь V1–V11, затем применяем явные override и прежнюю БЗ ЛР1
    // сохраняем все три результата: приоритеты и A* задают правила, этот код не назначает стратегию или физический диагноз
    public ProfileEvaluation Evaluate(DeviceProfile profile, ImmutableDictionary<string, InputFact> observations, bool matches, bool includeChecks,
        ImmutableArray<ModuleEvidence> evidence, ImmutableDictionary<string, InputFact>? overrides = null) {
        InferenceService engine = new InferenceService();
        InferenceResult? diagnostic = null, bridge = null;
        ImmutableDictionary<string, InputFact>.Builder inputs = ImmutableDictionary.CreateBuilder<string, InputFact>(StringComparer.Ordinal);
        inputs.Add("V1", new InputFact("V1", new KnowledgeValue(FactType.Boolean, matches ? "true" : "false"), "profile:" + profile.Id + ":" + profile.Sha256 + "; device/config match=" + matches));
        bool effective = includeChecks && profile.EvidencePolicy is not null;
        string note = includeChecks && !effective ? "Профиль не содержит правил учета; расчет только по телеметрии" : effective ? "По телеметрии и ответам пользователя" : "По телеметрии";
        if (matches) {
            ImmutableDictionary<string, InputFact>.Builder obs = ImmutableDictionary.CreateBuilder<string, InputFact>(StringComparer.Ordinal);
            foreach (SourceRequirement source in profile.Sources) {
                InputFact raw = observations.TryGetValue(source.SourceId, out InputFact? value) ? value : new InputFact(source.SourceId, new KnowledgeValue(FactType.Enum, "unknown"), "нет наблюдения");
                obs.Add(source.FactId, raw with { FactId = source.FactId });
            }
            diagnostic = engine.Run(profile.DiagnosticKnowledge, obs.ToImmutable());
            foreach (KeyValuePair<string, KnowledgeValue> pair in profile.Defaults) inputs.Add(pair.Key, new InputFact(pair.Key, pair.Value, "profile:" + profile.Id + ":" + profile.Sha256 + "; static_facts." + pair.Key));
            InferenceResult mapped = diagnostic;
            ImmutableDictionary<string, ImmutableDictionary<string, KnowledgeValue>> mappings = profile.Mappings;
            if (effective) {
                EvidencePolicy policy = profile.EvidencePolicy!;
                ImmutableDictionary<string, InputFact>.Builder bridgeInputs = ImmutableDictionary.CreateBuilder<string, InputFact>(StringComparer.Ordinal);
                void Fact(string id, string value, string origin) => bridgeInputs.Add(id, new InputFact(id, new KnowledgeValue(policy.Knowledge.Facts[id].Type, value), origin));
                Fact("obs_state", diagnostic.Status == InferenceStatus.Success ? LiveAssessmentService.Goal(diagnostic) : "unknown", "OBS:" + diagnostic.KnowledgeBase.Id + ":" + diagnostic.KnowledgeBase.Sha256 + "; rules=" + string.Join(",", diagnostic.Steps.Select(s => s.RuleId)));
                foreach (EvidenceModulePolicy module in policy.Modules) {
                    ModuleEvidence? witness = evidence.FirstOrDefault(e => e.Source == module.Source);
                    string origin = witness?.Origin ?? "Нет завершенной выборочной проверки; source=" + module.Source;
                    Fact("required_" + module.Source, module.Required ? "true" : "false", "profile:" + profile.Sha256 + "; required check " + module.Source);
                    Fact("included_" + module.Source, module.Required || module.InfluencingOutcomes.Count > 0 ? "true" : "false", "profile:" + profile.Sha256 + "; включение модуля");
                    foreach (string kind in new[] { "negative", "machine_error" }) Fact("affect_" + kind + "_" + module.Source, module.InfluencingOutcomes.Contains(kind) ? "true" : "false", "profile:" + profile.Sha256 + "; влияние " + kind);
                    Fact("observation_" + module.Source, observations.TryGetValue(module.Source, out InputFact? observation) ? observation.Value?.Canonical ?? "unknown" : "unknown", observation?.Origin ?? "Наблюдение не задано");
                    Fact("machine_" + module.Source, witness?.Machine ?? "none", origin);
                    Fact("human_" + module.Source, witness?.Human ?? (CheckProtocol.Human(module.Source) is null ? "not_required" : "absent"), origin);
                    Fact("signal_" + module.Source, witness?.Signal ?? "unknown", origin);
                }
                bridge = engine.Run(policy.Knowledge, bridgeInputs.ToImmutable()); mapped = bridge; mappings = policy.Mappings;
                if (bridge.Status == InferenceStatus.Success) note += "; " + policy.Knowledge.Outcomes[LiveAssessmentService.Goal(bridge)].Description;
                if (evidence.Length == 0) note += "; подходящих результатов пока нет";
            }
            if (mapped.Status == InferenceStatus.Success) {
                string goal = LiveAssessmentService.Goal(mapped);
                foreach (KeyValuePair<string, KnowledgeValue> pair in mappings[goal]) inputs[pair.Key] = new InputFact(pair.Key, pair.Value,
                    "diagnostic:" + mapped.KnowledgeBase.Id + ":" + mapped.KnowledgeBase.Sha256 + "; goal=" + goal + "; rules=" + string.Join(",", mapped.Steps.Select(s => s.RuleId)) + "; mapping profile:" + profile.Sha256
                    + (effective ? "; witnesses=" + string.Join(" | ", evidence.Select(e => e.Origin)) : ""));
            }
        }
        // одно значение на id: явная неизвестность также перекрывает расчетное, сброс удаляет ключ override
        if (overrides is not null) foreach (KeyValuePair<string, InputFact> pair in overrides) inputs[pair.Key] = pair.Value;
        InferenceResult strategy = engine.Run(profile.StrategyKnowledge, inputs.ToImmutable());
        return new ProfileEvaluation(diagnostic, bridge, strategy, effective, effective ? evidence : ImmutableArray<ModuleEvidence>.Empty, note);
    }
}
