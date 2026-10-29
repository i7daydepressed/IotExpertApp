using System.Collections.Immutable;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Models.Live;

public sealed record SourceRequirement(string SourceId, string FactId, uint MaximumAgeMs);

// загруженный профиль закрепляет собственные байты и обе неизменяемые БЗ
public sealed record DeviceProfile(string Path, string Id, string Version, string Sha256, string Title,
    string Scope, string DeviceId, string ConfigVersion, uint PacketTimeoutMs,
    ImmutableArray<SourceRequirement> Sources, KnowledgeBase DiagnosticKnowledge, KnowledgeBase StrategyKnowledge,
    ImmutableDictionary<string, KnowledgeValue> Defaults,
    ImmutableDictionary<string, ImmutableDictionary<string, KnowledgeValue>> Mappings) {
    public KnowledgeBase? ModuleKnowledge { get; init; }
}

// старый результат не меняется при приеме следующего пакета или перезагрузке профиля
public sealed record LiveAssessment(DeviceProfile Profile, ReceivedSnapshot? Snapshot, string Title,
    string Recommendation, string Context, InferenceResult? Diagnostic, InferenceResult? Strategy);
