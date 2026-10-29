using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Checks;

public static class ModuleCheckAssessment {
    private static readonly string[] KnownReasons = { "accepted", "running", "ready_for_observation", "machine_check_complete", "raw_only_power_not_verified", "explicit_finish", "watchdog_expired" };
    public static void ValidateKnowledge(KnowledgeBase knowledge) {
        Dictionary<string, string[]> enums = new Dictionary<string, string[]>(StringComparer.Ordinal) {
            ["module"] = CheckProtocol.Sources,
            ["operation"] = new[] { "snapshot", "display_test", "display_hold", "read_next", "observe", "run", "pulse" },
            ["source_kind"] = new[] { "live", "replay", "synthetic" },
            ["machine_stage"] = new[] { "none", "accepted", "running", "completed", "failed", "cancelled", "rejected" },
            ["human"] = new[] { "none", "yes", "no", "unclear", "skipped" },
            ["end_reason"] = new[] { "pending", "answered", "timeout", "interrupted", "uncertain", "early_end", "machine", "replay" }
        };
        string[] flags = "boot_matches reason_known machine_ok human_required oled_transfer oled_restored relay_return has_transitions sd_pass sd_cleanup".Split(' ');
        CheckProtocol.Require(knowledge.Facts.Values.Where(f => f.Role == FactRole.Input).Select(f => f.Id).ToHashSet(StringComparer.Ordinal)
            .SetEquals(enums.Keys.Concat(flags)), "не совпали входы БЗ модулей");
        foreach (string id in flags) CheckProtocol.Require(knowledge.Facts[id].Type == FactType.Boolean, "не bool: " + id);
        foreach (KeyValuePair<string, string[]> pair in enums)
            CheckProtocol.Require(knowledge.Facts[pair.Key].Type == FactType.Enum && pair.Value.All(v => knowledge.Facts[pair.Key].Options.Contains(v)), "не совпал enum: " + pair.Key);
    }
    public static InferenceResult? Run(GuidedCheckResult result) {
        if (result.Knowledge is null) return null;
        ImmutableDictionary<string, InputFact>.Builder inputs = ImmutableDictionary.CreateBuilder<string, InputFact>(StringComparer.Ordinal);
        CheckEvent? machine = result.Machine;
        bool? Bool(string field) => machine?.Data.TryGetProperty(field, out JsonElement v) == true && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        ulong? Number(string field) => machine?.Data.TryGetProperty(field, out JsonElement v) == true && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out ulong n) ? n : null;
        string origin = result.Origin + "; device=" + result.Device + "; boot=" + result.Boot + "; request=" + result.RequestId
            + "; source=" + result.Source + "; workflow=" + result.EndReason;
        void Fact(string id, string? value) {
            FactDefinition definition = result.Knowledge.Facts[id];
            inputs.Add(id, new InputFact(id, value is null ? null : new KnowledgeValue(definition.Type, value), origin + "; fact=" + id));
        }
        void Flag(string id, bool? value) => Fact(id, value.HasValue ? value.Value ? "true" : "false" : null);
        bool? machineOk = null;
        bool? relayReturn = Number("in1_level").HasValue && Number("in2_level").HasValue
            ? Number("in1_level") == 1 && Number("in2_level") == 1 && Number("off_callback_ms").HasValue : null;
        if (machine?.Stage == "failed") machineOk = false;
        else if (machine?.Stage == "completed") machineOk = result.Source switch {
            "esp32" => Number("free_heap_bytes").HasValue ? true : null,
            "dht11" => machine.Data.TryGetProperty("temperature_c", out _) && machine.Data.TryGetProperty("humidity_pct", out _) ? true : null,
            "microsd" => Bool("test_passed").HasValue && Bool("cleanup_ok").HasValue && Bool("file_retained").HasValue
                ? Bool("test_passed") == true && Bool("cleanup_ok") == true && Bool("file_retained") == false : null,
            "oled" => Bool("transfer_ok") == true && Bool("dashboard_restored").HasValue ? Bool("dashboard_restored") : null,
            "relay_1" or "relay_2" => relayReturn,
            _ => Number("transition_count").HasValue ? true : null
        };
        Fact("module", result.Source); Fact("operation", result.Operation);
        Fact("source_kind", result.Origin.StartsWith("synthetic", StringComparison.Ordinal) ? "synthetic" : result.Origin.StartsWith("serial:", StringComparison.Ordinal) ? "live" : "replay");
        Flag("boot_matches", result.EndReason != "interrupted");
        Fact("machine_stage", machine?.Stage ?? "none"); Flag("reason_known", machine is null ? null : KnownReasons.Contains(machine.Reason, StringComparer.Ordinal));
        Flag("machine_ok", machineOk); Flag("human_required", CheckProtocol.Human(result.Source) is not null);
        Fact("human", result.Human?.Answer switch { "Да" => "yes", "Нет" => "no", "Неясно" => "unclear", "Не проверял" => "skipped", _ => "none" });
        Fact("end_reason", result.EndReason);
        Flag("oled_transfer", Bool("transfer_ok")); Flag("oled_restored", Bool("dashboard_restored")); Flag("relay_return", relayReturn);
        Flag("has_transitions", Number("transition_count").HasValue ? Number("transition_count") > 0 : null);
        Flag("sd_pass", Bool("test_passed")); Flag("sd_cleanup", Bool("cleanup_ok"));
        if (result.Human is not null) inputs["human"] = inputs["human"] with { Origin = origin + "; human=" + result.Human.Answer
            + "; answered=" + result.Human.AnsweredAt.ToString("O") + "; method=" + result.Human.Method };
        return new IotExpertApp.Services.Knowledge.InferenceService().Run(result.Knowledge, inputs.ToImmutable());
    }
    public static string Summary(GuidedCheckResult result) {
        InferenceResult? inference = result.Inference;
        if (inference is null) return "БЗ выборочных проверок не загружена";
        if (inference.Status != InferenceStatus.Success) return "Недостаточно оснований для заключения: " + inference.Status;
        string goal = IotExpertApp.Services.Live.LiveAssessmentService.Goal(inference);
        string title = inference.KnowledgeBase.Outcomes[goal].Title;
        return title + (goal == "no_answer" ? " за " + result.ResponseSeconds + " с" : "");
    }
}
