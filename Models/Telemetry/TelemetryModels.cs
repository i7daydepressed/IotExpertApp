using System;
using System.Collections.Generic;
using System.Text.Json;

namespace IotExpertApp.Models.Telemetry;

public sealed record Observation(
    string SourceId, string SourceType, bool Enabled, string Status,
    ulong? LastAttemptMs, ulong? LastSuccessMs, JsonElement? Values,
    int? ErrorCode, string? ErrorDomain, string? ErrorDetail,
    uint ConsecutiveErrors, ulong AttemptCount, int? RawLevel);

public sealed record TelemetryPacket(
    string DeviceId, string BootId, ulong Sequence, ulong UptimeMs,
    string FirmwareVersion, string ConfigVersion, string LocalDiagnostic,
    IReadOnlyList<Observation> Observations);

public sealed record ReceivedSnapshot(TelemetryPacket Packet, DateTimeOffset ReceivedAt, string Origin) {
    // монотонное время приема не зависит от перевода системных часов
    public long ReceivedTick { get; init; } = System.Diagnostics.Stopwatch.GetTimestamp();
}

public sealed record Frame(string? Text, bool Oversized, bool JsonCandidate) {
    public bool TestCandidate { get; init; }
}

public sealed record ReceiverState(
    string Status, string Origin, long Valid, long Logs, long Invalid,
    long RejectedOrder, long Missing, long Oversized, long Tails,
    string LastMessage, ReceivedSnapshot? Snapshot, long TemporalRejected,
    bool Running, bool Completed, bool ReplayPaused, string Playback, string Capture) {
    public bool IsSerial { get; init; }
    public long TestEvents { get; init; }
    public long TestErrors { get; init; }
}
