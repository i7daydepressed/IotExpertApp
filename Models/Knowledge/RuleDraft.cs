using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IotExpertApp.Models.Knowledge;

public enum DraftConditionKind { All, Any, Comparison }

// изменяемые объекты принадлежат только редактору, действующая KnowledgeBase остается неизменяемой
public partial class ConditionDraft : ObservableObject {
    public DraftConditionKind Kind { get; }
    public bool IsComparison => Kind == DraftConditionKind.Comparison;
    public ObservableCollection<ConditionDraft> Children { get; } = new ObservableCollection<ConditionDraft>();
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Label))] private string factId = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Label))] private string operation = "eq";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Label))] private string valueText = "";
    public string Label => IsComparison ? FactId + " " + Operation + " " + ValueText
        : (Kind == DraftConditionKind.All ? "И (all)" : "ИЛИ (any)") + " — условий: " + Children.Count;
    public ConditionDraft(DraftConditionKind kind) {
        Kind = kind;
        Children.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }
}

public partial class ConclusionDraft : ObservableObject {
    [ObservableProperty] private string factId = "";
    [ObservableProperty] private string valueText = "";
    public override string ToString() => FactId + " = " + ValueText;
}

public partial class RuleDraft : ObservableObject {
    [ObservableProperty] private string id = "";
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string priorityText = "0";
    [ObservableProperty] private string explanation = "";
    [ObservableProperty] private string provenance = "";
    [ObservableProperty] private ConditionDraft root = new ConditionDraft(DraftConditionKind.All);
    public ObservableCollection<ConclusionDraft> Conclusions { get; } = new ObservableCollection<ConclusionDraft>();
}
