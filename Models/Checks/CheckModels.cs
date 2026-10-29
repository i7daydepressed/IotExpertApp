using System;
using System.Collections.Immutable;
using System.Text.Json;

namespace IotExpertApp.Models.Checks;

public sealed record CheckRequest(ulong Id, string Boot, string Source, string Operation, uint? Duration = null, ulong? Target = null, uint ProtocolVersion = 1, uint? MaxHold = null);
public sealed record SourceCapability(string Source, bool Initialized, string? Limitation,
    ImmutableArray<string> Operations, string? HumanConfirmation);
public sealed record CheckCapabilities(bool Busy, bool IdExhausted, ImmutableArray<SourceCapability> Sources);
public sealed record CheckEvent(string Kind, string Device, string Boot, string Firmware, string Config,
    ulong? RequestId, ulong? ReplyTo, ulong? Target, string? Source, string? Operation, string Stage,
    string Reason, bool Replayed, ulong UpperId, ulong? Accepted, ulong? Started, ulong? Finished,
    int? ErrorCode, string? ErrorDomain, string? HumanConfirmation, JsonElement Data, CheckCapabilities? Capabilities) {
    public uint ProtocolVersion { get; init; } = 1;
    public bool Terminal => Stage is "completed" or "failed" or "cancelled" or "rejected";
    public string Key => Device + "/" + Boot + "/" + RequestId;
}

// ответ хранится отдельно от машинного события и не изменяет телеметрию или входы правил
public sealed record HumanCheckAnswer(string Device, string Boot, ulong RequestId, string Source,
    string Answer, DateTimeOffset AnsweredAt, string Method);
public sealed record CheckResult(CheckEvent Event, string Origin, DateTimeOffset ReceivedAt);
public sealed record CheckCommand(CheckRequest Request, long Generation, string Device, string Firmware, string Config);
public sealed record CheckSessionState(string Message, bool Compatible, bool Ready, bool RelayAuthorized,
    ulong? PendingId, ImmutableArray<CheckResult> History, ImmutableArray<HumanCheckAnswer> Answers,
    CheckCapabilities? Capabilities, long Revision, string Identity) {
    public uint ProtocolVersion { get; init; } = 1;
    public ulong? UncertainId { get; init; }
    public ulong? UncertainFinishTarget { get; init; }
    public bool WorkflowBusy { get; init; }
}
