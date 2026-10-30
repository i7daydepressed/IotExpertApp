using System;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Models.Checks;

// снимок проверки удерживает параметры, ответ и свою БЗ независимо от новых пакетов и перезагрузки профиля
public sealed record GuidedCheckResult(string Device, string Boot, string Firmware, string Config, ulong RequestId, string Source, string Operation,
    string Origin, uint ProtocolVersion, int ResponseSeconds, uint? WindowMs, KnowledgeBase? Knowledge,
    string Phase, string EndReason, TimeSpan StartedAt, TimeSpan TransportDeadline, TimeSpan? AnswerDeadline,
    CheckEvent? Machine, HumanCheckAnswer? Human, bool FinishRequested, InferenceResult? Inference) {
    public string ResponseOutcome { get; init; } = "pending";
    public int Revision { get; init; }
    public long ContextId { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public bool Active => Phase is "starting" or "machine" or "answer" or "finishing";
}
