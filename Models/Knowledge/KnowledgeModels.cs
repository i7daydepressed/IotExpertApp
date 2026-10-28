using System.Collections.Immutable;

namespace IotExpertApp.Models.Knowledge;

public enum FactType { Boolean, Enum, Integer, Number }
public enum FactRole { Input, Derived, Goal }
public enum TruthValue { Unknown, False, True }
public enum InferenceStatus { Success, InsufficientData, NoMatch, Conflict, StepLimit }
public enum ConsultationOutcomeKind { Strategy, Clarification, Refusal }
public sealed record ConsultationOutcome(string Value, ConsultationOutcomeKind Kind, string Title, string Description);

// значения хранятся в каноническом виде, числа сравниваются как decimal без double
public sealed record KnowledgeValue(FactType Type, string Canonical);

public sealed record FactDefinition(string Id, string Name, FactType Type, FactRole Role,
    ImmutableArray<string> Options, decimal? Minimum, decimal? Maximum, string? Question);

public abstract record Condition;
public sealed record AllCondition(ImmutableArray<Condition> Items) : Condition;
public sealed record AnyCondition(ImmutableArray<Condition> Items) : Condition;
public sealed record ComparisonCondition(string FactId, string Operator, KnowledgeValue Expected) : Condition;
public sealed record Conclusion(string FactId, KnowledgeValue Value);
public sealed record KnowledgeRule(string Id, string Title, int Priority, Condition Condition,
    ImmutableArray<Conclusion> Conclusions, string Explanation, string Provenance);

// коллекции неизменяемы, поэтому последующая загрузка файла не меняет прежний снимок БЗ
public sealed record KnowledgeBase(int SchemaVersion, string Id, string Version, string Sha256,
    bool Synthetic, ImmutableDictionary<string, FactDefinition> Facts, ImmutableArray<KnowledgeRule> Rules) {
    public ImmutableArray<string> QuestionOrder { get; init; } = ImmutableArray<string>.Empty;
    public ImmutableDictionary<string, ConsultationOutcome> Outcomes { get; init; }
        = ImmutableDictionary<string, ConsultationOutcome>.Empty;
}

public sealed record InputFact(string FactId, KnowledgeValue? Value, string Origin);
public sealed record Evaluation(TruthValue State, Condition Condition, KnowledgeValue? Actual,
    ImmutableArray<Evaluation> Children);
public sealed record FactEvidence(string Origin, string? RuleId, ImmutableArray<string> Premises);
public sealed record ProvenFact(KnowledgeValue Value, ImmutableArray<FactEvidence> Evidence);
public sealed record InferenceStep(int Number, string RuleId, ImmutableArray<string> Agenda,
    Evaluation Condition, ImmutableArray<Conclusion> Conclusions);
public sealed record InferenceConflict(string FactId, ProvenFact Existing, Conclusion Incoming,
    string RuleId, Evaluation Condition);
public sealed record InferenceResult(KnowledgeBase KnowledgeBase, InferenceStatus Status,
    ImmutableDictionary<string, InputFact> Inputs, ImmutableDictionary<string, ProvenFact> Facts,
    ImmutableArray<InferenceStep> Steps, ImmutableArray<string> MissingInputs, InferenceConflict? Conflict);
