using System;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Telemetry;

public sealed class ObservationStore
{
    private ReceivedSnapshot? current;
    private readonly ObservationProgressValidator progress = new ObservationProgressValidator();

    public ReceivedSnapshot? Current => current;
    public void Clear() => current = null;

    public bool CanApply(TelemetryPacket packet, out ulong missing, out string message, out bool temporalError)
    {
        missing = 0;
        message = "пакет принят";
        temporalError = false;
        TelemetryPacket? previous = current?.Packet;
        // sequence сравниваем только внутри пары устройство/запуск, новый boot начинает отдельную историю
        if (previous is not null && previous.DeviceId == packet.DeviceId && previous.BootId == packet.BootId)
        {
            if (packet.Sequence <= previous.Sequence || packet.UptimeMs < previous.UptimeMs)
            {
                message = "дубликат/откат sequence или uptime";
                return false;
            }
            if (!progress.TryValidate(previous, packet, out message)) { temporalError = true; return false; }
            missing = packet.Sequence - previous.Sequence - 1;
            message = missing > 0 ? "непринятых номеров между снимками: " + missing : "пакет принят";
        }
        else if (previous is not null) message = "новое устройство или запуск";
        return true;
    }

    // вызывается координатором под общей блокировкой состояния приемника
    public bool TryApply(TelemetryPacket packet, string origin, out ulong missing, out string message)
    {
        if (!CanApply(packet, out missing, out message, out _)) return false;
        current = new ReceivedSnapshot(packet, DateTimeOffset.UtcNow, origin);
        return true;
    }
}
