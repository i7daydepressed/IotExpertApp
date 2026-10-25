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

public sealed record ReceivedSnapshot(TelemetryPacket Packet, DateTimeOffset ReceivedAt, string Origin);

public sealed record Frame(string? Text, bool Oversized, bool JsonCandidate);

public sealed record ReceiverState(
    string Status, string Origin, long Valid, long Logs, long Invalid,
    long RejectedOrder, long Missing, long Oversized, long Tails,
    string LastMessage, ReceivedSnapshot? Snapshot);
