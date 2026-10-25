using System;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Telemetry;

public sealed class ObservationStore
{
    private ReceivedSnapshot? current;

    public ReceivedSnapshot? Current => current;
    public void Clear() => current = null;

    // вызывается координатором под общей блокировкой состояния приемника
    public bool TryApply(TelemetryPacket packet, string origin, out ulong missing, out string message)
    {
        missing = 0;
        message = "пакет принят";
        TelemetryPacket? previous = current?.Packet;
        // sequence сравниваем только внутри пары устройство/запуск, новый boot начинает отдельную историю
        if (previous is not null && previous.DeviceId == packet.DeviceId && previous.BootId == packet.BootId)
        {
            if (packet.Sequence <= previous.Sequence || packet.UptimeMs < previous.UptimeMs)
            {
                message = "дубликат/откат sequence или uptime";
                return false;
            }
            missing = packet.Sequence - previous.Sequence - 1;
            if (missing > 0) message = "пропуск пакетов: " + missing;
        }
        else if (previous is not null) message = "новое устройство или запуск";
        current = new ReceivedSnapshot(packet, DateTimeOffset.UtcNow, origin);
        return true;
    }
}
