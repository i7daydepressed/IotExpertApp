using System;
using System.Linq;
using System.Text.Json;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Telemetry;

public sealed class ObservationProgressValidator {
    // сравниваем только два принятых снимка одного запуска, наблюдения сопоставляем по source_id
    // delta показывает число новых попыток, а серия ошибок позволяет понять, был ли между ними успех
    // при насыщении attempt_count точное delta уже неизвестно, поэтому сохраняем лишь проверки без этой разности
    public bool TryValidate(TelemetryPacket previous, TelemetryPacket next, out string error) {
        error = "";
        if (previous.DeviceId != next.DeviceId || previous.BootId != next.BootId) return true;
        if (previous.ConfigVersion != next.ConfigVersion || previous.FirmwareVersion != next.FirmwareVersion)
            return Fail("версия изменилась внутри запуска", out error);
        foreach (Observation current in next.Observations) {
            Observation? old = previous.Observations.FirstOrDefault(item => item.SourceId == current.SourceId);
            if (old is null) return Fail("изменился состав источников", out error);
            string reason = Check(old, current);
            if (reason.Length > 0) return Fail(current.SourceId + ": " + reason, out error);
        }
        return true;
    }

    private static string Check(Observation old, Observation current) {
        if (old.Enabled != current.Enabled || old.SourceType != current.SourceType) return "изменился профиль источника";
        if (current.AttemptCount < old.AttemptCount) return "уменьшился attempt_count";
        if (Regressed(old.LastAttemptMs, current.LastAttemptMs)) return "откат/исчезновение last_attempt_ms";
        if (Regressed(old.LastSuccessMs, current.LastSuccessMs)) return "откат/исчезновение last_success_ms";
        if (old.LastSuccessMs == current.LastSuccessMs && !SameValues(old.Values, current.Values))
            return "values изменились без нового времени успеха";
        // достижение насыщения тоже скрывает число попыток: delta уже только нижняя граница
        if (current.AttemptCount == ulong.MaxValue) return "";
        ulong delta = current.AttemptCount - old.AttemptCount;
        if (delta == 0) {
            if (old.Status != current.Status || old.LastAttemptMs != current.LastAttemptMs ||
                old.LastSuccessMs != current.LastSuccessMs || old.ConsecutiveErrors != current.ConsecutiveErrors ||
                old.ErrorCode != current.ErrorCode || old.ErrorDomain != current.ErrorDomain ||
                old.ErrorDetail != current.ErrorDetail || old.RawLevel != current.RawLevel)
                return "источник изменился без завершенной попытки";
            return "";
        }
        ulong maximumErrors = Math.Min((ulong)uint.MaxValue,
            (ulong)old.ConsecutiveErrors + Math.Min(delta, (ulong)uint.MaxValue));
        if (current.ConsecutiveErrors > maximumErrors) return "ошибок больше, чем возможных попыток";
        if (current.ConsecutiveErrors >= delta) {
            // вся разность попыток покрыта серией ошибок, нового успеха в этом промежутке быть не могло
            uint expected = (uint)maximumErrors;
            if (current.ConsecutiveErrors != expected) return "неверный прирост последовательных ошибок";
            if (old.LastSuccessMs != current.LastSuccessMs || !SameValues(old.Values, current.Values))
                return "неуспешные попытки изменили последний успех";
        }
        // насыщенная серия при delta > UINT32_MAX не доказывает, что между ошибками был успех
        else if (current.ConsecutiveErrors < uint.MaxValue && (!current.LastSuccessMs.HasValue ||
            (old.LastAttemptMs.HasValue && current.LastSuccessMs.Value < old.LastAttemptMs.Value)))
            return "сброс серии ошибок без нового успеха";
        // между двумя пакетами могут пройти и успех, и ошибка: не требуем только одного перехода
        return "";
    }

    private static bool Regressed(ulong? old, ulong? current) => old.HasValue && (!current.HasValue || current.Value < old.Value);
    private static bool SameValues(JsonElement? left, JsonElement? right) =>
        left.HasValue == right.HasValue && (!left.HasValue || JsonElement.DeepEquals(left.Value, right!.Value));
    private static bool Fail(string message, out string error) { error = message; return false; }
}
