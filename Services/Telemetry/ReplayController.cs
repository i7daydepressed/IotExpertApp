using System;
using System.Diagnostics;
using System.Threading;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Telemetry;

public sealed class ReplayController {
    private readonly object gate = new object();
    private bool timed, paused, waiting;
    private string device = "", boot = "";
    private decimal position;
    private ulong target;
    private long tick;

    public void Reset(bool useTiming) {
        lock (gate) {
            timed = useTiming;
            paused = waiting = false;
            device = boot = "";
            position = 0;
            target = 0;
            tick = Stopwatch.GetTimestamp();
        }
    }

    public void TogglePause() {
        lock (gate) { Update(); paused = !paused; tick = Stopwatch.GetTimestamp(); }
    }

    public void WaitIfPaused(CancellationToken token) {
        while (true) {
            token.ThrowIfCancellationRequested();
            lock (gate) { if (!paused) return; }
            token.WaitHandle.WaitOne(20);
        }
    }

    // отсчитываем интервал от прошлого принятого uptime, первый пакет нового boot показываем сразу
    // пауза запрещает и ход часов, и смену шкалы запуска, а ожидание отпускает gate между проверками
    public void WaitForPacket(TelemetryPacket packet, CancellationToken token) {
        bool prepared = false;
        while (true) {
            token.ThrowIfCancellationRequested();
            lock (gate) {
                if (!paused) {
                    if (!prepared) {
                        if (device != packet.DeviceId || boot != packet.BootId) {
                            device = packet.DeviceId;
                            boot = packet.BootId;
                            position = packet.UptimeMs;
                        }
                        target = packet.UptimeMs;
                        waiting = true;
                        tick = Stopwatch.GetTimestamp();
                        prepared = true;
                    }
                    Update();
                    if (position >= target) { waiting = false; return; }
                }
            }
            token.WaitHandle.WaitOne(20);
        }
    }

    public void Complete() { lock (gate) { Update(); waiting = false; paused = false; } }
    public (bool Paused, string Description) GetState() {
        lock (gate) {
            Update();
            return (paused, $"{(timed ? "по времени" : "быстро")}; {(paused ? "пауза" : "воспроизведение")}; uptime записи {(ulong)position} мс");
        }
    }

    private void Update() {
        long now = Stopwatch.GetTimestamp();
        if (waiting && !paused) {
            // decimal вмещает весь UInt64: часы записи не проходят через double или TimeSpan.FromMilliseconds
            decimal elapsedMs = Stopwatch.GetElapsedTime(tick, now).Ticks / (decimal)TimeSpan.TicksPerMillisecond;
            position = timed ? Math.Min((decimal)target, position + elapsedMs) : target;
        }
        tick = now;
    }
}
