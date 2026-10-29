using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.Services.Live;

public sealed class LiveAssessmentService {
    // возраст хоста передается явно: тесты не ждут часы, replay не превращается в живой поток
    public string RefreshKey(DeviceProfile profile, ReceiverState state, TimeSpan packetAge) {
        string key = state.IsSerial + "/" + state.Running + "/" + state.Completed + "/" + state.ReplayPaused
            + "/" + Expired(profile, state, packetAge);
        if (state.Snapshot is not null)
            key += "/" + string.Join("/", profile.Sources.Select(source => Normalize(source, state, packetAge)));
        return key;
    }

    // берем один принятый снимок и закрепленный профиль; для live добавляем монотонный возраст хоста к возрасту источников
    // нормализованные статусы идут в OBS-правила, их доказанный итог через внешний mapping задает входы общей БЗ ЛР1
    // оба расчета создаются заново и сохраняют происхождение; Stop и просроченный пакет возвращают состояние без рекомендации
    public LiveAssessment Assess(DeviceProfile profile, ReceiverState state, TimeSpan packetAge) {
        ReceivedSnapshot? snapshot = state.Snapshot;
        string mode = state.IsSerial ? "Живой поток" : "Запись" + (state.ReplayPaused ? ", пауза" : state.Completed ? ", конец файла" : "");
        string context = mode + "\nПрофиль " + profile.Id + " / " + profile.Version + " / SHA256 " + profile.Sha256
            + "\n" + profile.Scope;
        if (snapshot is null) return Empty("Ожидание телеметрии", "Выберите порт и подключитесь либо откройте запись");
        TelemetryPacket packet = snapshot.Packet;
        context += "\n" + snapshot.Origin + " | " + packet.DeviceId + " | boot " + packet.BootId
            + " | sequence " + packet.Sequence.ToString(CultureInfo.InvariantCulture)
            + " | uptime " + packet.UptimeMs.ToString(CultureInfo.InvariantCulture) + " мс"
            + "\nПринято " + snapshot.ReceivedAt.ToString("O", CultureInfo.InvariantCulture)
            + " | " + packet.FirmwareVersion + " | " + packet.ConfigVersion;
        if (!state.Running && !state.Completed) return Empty("Источник остановлен", "Текущая рекомендация снята; прежнее объяснение доступно отдельно");
        if (Expired(profile, state, packetAge)) return Empty("Нет свежей телеметрии", "Текущая рекомендация снята; проверьте соединение с выбранным портом");
        bool matches = packet.DeviceId == profile.DeviceId && packet.ConfigVersion == profile.ConfigVersion;
        ImmutableDictionary<string, InputFact>.Builder strategyInputs = ImmutableDictionary.CreateBuilder<string, InputFact>(StringComparer.Ordinal);
        strategyInputs.Add("V1", new InputFact("V1", new KnowledgeValue(FactType.Boolean, matches ? "true" : "false"),
            "profile:" + profile.Id + ":" + profile.Version + ":" + profile.Sha256 + "; device/config match=" + matches));
        InferenceService engine = new InferenceService();
        InferenceResult? diagnostic = null;
        if (matches) {
            ImmutableDictionary<string, InputFact>.Builder observations = ImmutableDictionary.CreateBuilder<string, InputFact>(StringComparer.Ordinal);
            foreach (SourceRequirement source in profile.Sources) {
                Observation? observation = packet.Observations.FirstOrDefault(item => item.SourceId == source.SourceId);
                string status = Normalize(source, state, packetAge);
                string origin = "telemetry:" + packet.DeviceId + "/" + packet.BootId + "/" + packet.Sequence
                    + "; " + snapshot.Origin + "; source=" + source.SourceId + "; normalized=" + status
                    + "; status=" + observation?.Status + "; last_attempt_ms=" + observation?.LastAttemptMs
                    + "; last_success_ms=" + observation?.LastSuccessMs + "; reason=" + observation?.ErrorDetail
                    + "; max_age_ms=" + source.MaximumAgeMs + "; host_age_ms="
                    + (state.IsSerial ? Math.Max(0, packetAge.TotalMilliseconds).ToString("F0", CultureInfo.InvariantCulture) : "не применяется к записи");
                observations.Add(source.FactId, new InputFact(source.FactId, new KnowledgeValue(FactType.Enum, status), origin));
            }
            diagnostic = engine.Run(profile.DiagnosticKnowledge, observations.ToImmutable());
            foreach (KeyValuePair<string, KnowledgeValue> pair in profile.Defaults)
                strategyInputs.Add(pair.Key, new InputFact(pair.Key, pair.Value, "profile:" + profile.Id + ":" + profile.Sha256 + "; static_facts." + pair.Key));
            if (diagnostic.Status == InferenceStatus.Success) {
                string diagnosticGoal = Goal(diagnostic);
                foreach (KeyValuePair<string, KnowledgeValue> pair in profile.Mappings[diagnosticGoal])
                    strategyInputs.Add(pair.Key, new InputFact(pair.Key, pair.Value,
                        "diagnostic:" + diagnostic.KnowledgeBase.Id + ":" + diagnostic.KnowledgeBase.Sha256
                        + "; goal=" + diagnosticGoal + "; rules=" + string.Join(",", diagnostic.Steps.Select(step => step.RuleId))
                        + "; mapping profile:" + profile.Sha256));
            }
        }
        InferenceResult strategy = engine.Run(profile.StrategyKnowledge, strategyInputs.ToImmutable());
        string title = "Стратегия не доказана: " + strategy.Status;
        string recommendation = "Не хватает оснований для рекомендации";
        if (strategy.Status == InferenceStatus.Success) {
            string goal = Goal(strategy);
            ConsultationOutcome outcome = profile.StrategyKnowledge.Outcomes[goal];
            title = goal + " — " + outcome.Title;
            recommendation = outcome.Description;
        }
        if (diagnostic is not null && diagnostic.Status != InferenceStatus.Success) {
            title = "Диагностика профиля не завершена: " + diagnostic.Status;
            recommendation = "Стратегия не подтверждена; откройте объяснение диагностических правил";
        }
        if (!matches) recommendation += "; ожидаются " + profile.DeviceId + " / " + profile.ConfigVersion;
        return new LiveAssessment(profile, snapshot, mode + " · " + title, recommendation, context, diagnostic, strategy);

        LiveAssessment Empty(string title, string recommendation)
            => new LiveAssessment(profile, snapshot, title, recommendation, context, null, null);
    }

    public static string Goal(InferenceResult result) {
        string id = result.KnowledgeBase.Facts.Values.Single(item => item.Role == FactRole.Goal).Id;
        return result.Facts[id].Value.Canonical;
    }

    private static bool Expired(DeviceProfile profile, ReceiverState state, TimeSpan packetAge)
        => state.IsSerial && packetAge.TotalMilliseconds > profile.PacketTimeoutMs;

    // ошибка после прежнего успеха остается ошибкой; старые values не превращаются в новый успех
    internal static string Normalize(SourceRequirement source, ReceiverState state, TimeSpan packetAge) {
        TelemetryPacket? packet = state.Snapshot?.Packet;
        Observation? observation = packet?.Observations.FirstOrDefault(item => item.SourceId == source.SourceId);
        if (packet is null || observation is null) return "unknown";
        if (!observation.Enabled || observation.Status == "disabled") return "disabled";
        if (!observation.LastAttemptMs.HasValue) return "not_polled";
        if (TooOld(observation.LastAttemptMs.Value)) return "stale";
        if (observation.Status is "error" or "unavailable") return observation.Status;
        if (observation.Status != "ok" || !observation.LastSuccessMs.HasValue || !observation.Values.HasValue) return "unknown";
        return TooOld(observation.LastSuccessMs.Value) ? "stale" : "ok";

        bool TooOld(ulong time) {
            if (time > packet.UptimeMs) return true;
            ulong age = packet.UptimeMs - time;
            if (age > source.MaximumAgeMs) return true;
            // UInt64 не переводим в double: сначала ограничиваем разность небольшим лимитом профиля
            return state.IsSerial && Math.Max(0, packetAge.TotalMilliseconds) > source.MaximumAgeMs - age;
        }
    }
}
