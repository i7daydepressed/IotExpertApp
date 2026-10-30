using System;
using System.Collections.Immutable;
using IotExpertApp.Models.Checks;
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
    public EvidencePolicy? EvidencePolicy { get; init; }
}

// старый результат не меняется при приеме следующего пакета или перезагрузке профиля
public sealed record LiveAssessment(DeviceProfile Profile, ReceivedSnapshot? Snapshot, string Title,
    string Recommendation, string Context, InferenceResult? Diagnostic, InferenceResult? Strategy) {
    public ProfileEvaluation? Evaluation { get; init; }
}

public sealed record EvidenceModulePolicy(string Source, string Kind, bool Required, ImmutableHashSet<string> InfluencingOutcomes);
public sealed record EvidencePolicy(KnowledgeBase Knowledge, ImmutableArray<EvidenceModulePolicy> Modules,
    ImmutableDictionary<string, ImmutableDictionary<string, KnowledgeValue>> Mappings);

// это нормализованные основания, а не новые TEST_EVENT; Check сохраняет исходную редакцию реального результата
public sealed record ModuleEvidence(string Source, string Machine, string Human, string Signal, string Origin,
    int Revision, ulong? RequestId, DateTimeOffset? CompletedAt, string ModuleOutcome, GuidedCheckResult? Check = null);
public sealed record EvidenceSelection(ImmutableArray<ModuleEvidence> Items, ImmutableArray<string> Excluded);
public sealed record ProfileEvaluation(InferenceResult? Diagnostic, InferenceResult? Evidence, InferenceResult Strategy,
    bool IncludeChecks, ImmutableArray<ModuleEvidence> Witnesses, string Note);
