using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ViewModels;

public partial class KnowledgeEditorViewModel : ViewModelBase {
    private KnowledgeWorkspace? workspace;
    private readonly Action<string>? usePath;
    private readonly List<INotifyPropertyChanged> observed = new List<INotifyPropertyChanged>();
    private bool suppress;
    private string? selectedRuleId;
    private string? editedId;
    [ObservableProperty] private string sourcePath = Path.Combine(AppContext.BaseDirectory, "Knowledge", "lr1-strategies.json");
    [ObservableProperty] private string workingCopyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IotExpertApp", "knowledge-base.json");
    [ObservableProperty] private string versionText = "";
    [ObservableProperty] private string information = "Файл не загружен";
    [ObservableProperty] private string message = "Создайте рабочую копию или загрузите существующий файл";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(PendingDescription))] private bool hasPendingRuleChanges;
    [ObservableProperty] private RuleDraft? draft;
    [ObservableProperty] private ConditionDraft? selectedNode;
    [ObservableProperty] private ConclusionDraft? selectedConclusion;
    [ObservableProperty] private ImmutableArray<string> ruleIds = ImmutableArray<string>.Empty;
    [ObservableProperty] private ImmutableArray<string> conditionFactIds = ImmutableArray<string>.Empty;
    [ObservableProperty] private ImmutableArray<string> conclusionFactIds = ImmutableArray<string>.Empty;
    [ObservableProperty] private ImmutableArray<ConditionDraft> conditionRoots = ImmutableArray<ConditionDraft>.Empty;
    public ImmutableArray<string> Operations { get; } = ImmutableArray.Create("eq", "ne", "gt", "gte", "lt", "lte");
    public bool IsExistingRule => editedId is not null;
    public bool CanEdit => workspace?.IsWorkingLocation == true;
    public bool IsComparisonSelected => SelectedNode?.IsComparison == true;
    public string ValueHint => Hint(SelectedNode?.FactId);
    public string ConclusionHint => Hint(SelectedConclusion?.FactId);
    public string PendingDescription => HasPendingRuleChanges ? "Есть непримененные поля правила" : "Непримененных полей правила нет";
    public KnowledgeBase? WorkingKnowledge => workspace?.Knowledge;

    public string? SelectedRuleId {
        get => selectedRuleId;
        set {
            if (suppress || value == selectedRuleId) return;
            if (HasPendingRuleChanges) {
                Message = "Сначала примените поля к черновику или сбросьте поля правила";
                OnPropertyChanged(nameof(SelectedRuleId));
                return;
            }
            Select(value);
        }
    }

    public KnowledgeEditorViewModel(Action<string>? usePath = null) { this.usePath = usePath; }

    [RelayCommand]
    private void Load() => Attempt(() => {
        RequireClean();
        KnowledgeWorkspace replacement = KnowledgeWorkspace.Load(SourcePath);
        workspace = replacement;
        Refresh(replacement.Knowledge.Rules[0].Id);
        Message = "Файл загружен; консультация продолжает использовать прежнюю БЗ";
    });

    [RelayCommand]
    private void CreateCopy() => Attempt(() => {
        RequireClean();
        KnowledgeWorkspace.CreateWorkingCopy(SourcePath, WorkingCopyPath);
        workspace = KnowledgeWorkspace.Load(WorkingCopyPath);
        SourcePath = workspace!.PathName;
        Refresh(workspace!.Knowledge.Rules[0].Id);
        Message = "Рабочая копия создана без перезаписи существующих файлов";
    });

    [RelayCommand]
    private void NewRule() => Attempt(() => {
        RequireLoaded(); RequireApplied();
        Unobserve();
        editedId = null;
        suppress = true;
        selectedRuleId = null;
        OnPropertyChanged(nameof(SelectedRuleId));
        Draft = new RuleDraft {
            Root = Comparison(),
            Provenance = "author/operator draft; предметное основание укажите перед сохранением"
        };
        FactDefinition target = workspace!.Knowledge.Facts[ConclusionFactIds[0]];
        Draft!.Conclusions.Add(new ConclusionDraft { FactId = target.Id, ValueText = RuleDraftCodec.DefaultValue(target) });
        suppress = false;
        Observe(); UpdateTree(); SelectedConclusion = Draft!.Conclusions[0];
        HasPendingRuleChanges = true;
        OnPropertyChanged(nameof(IsExistingRule));
        Message = "Новое правило: заполните ID, название, объяснение и происхождение; затем Применить поля";
    });

    [RelayCommand]
    private void Apply() => Attempt(() => { ApplyCore(); Message = "Правило проверено и применено к черновику; файл еще не сохранен"; });

    private void ApplyCore() {
        RequireLoaded();
        KnowledgeJson.Require(Draft is not null, "сначала выберите или создайте правило");
        string id = Draft!.Id;
        workspace!.Apply(editedId, Draft, VersionText ?? "");
        Refresh(id);
    }

    [RelayCommand]
    private void DeleteRule() => Attempt(() => {
        RequireLoaded(); RequireApplied();
        KnowledgeJson.Require(selectedRuleId is not null, "выберите существующее правило");
        workspace!.Delete(selectedRuleId!);
        Refresh(workspace!.Knowledge.Rules[0].Id);
        Message = "Правило удалено из черновика; Отменить изменение вернет его до сохранения";
    });

    [RelayCommand]
    private void Undo() => Attempt(() => {
        RequireLoaded(); RequireApplied();
        workspace!.Undo(); Refresh(selectedRuleId);
        Message = "Последнее примененное изменение отменено; файл не менялся";
    });

    [RelayCommand]
    private void ResetRule() {
        if (workspace is null) return;
        Select(editedId);
        Message = "Непримененные поля сброшены, черновик файла сохранен";
    }

    [RelayCommand]
    private void Discard() {
        if (workspace is null) return;
        workspace!.Discard(); Refresh(selectedRuleId);
        Message = "Весь черновик сброшен к последней загруженной/сохраненной версии; файл не менялся";
    }

    [RelayCommand]
    private void Save() => Attempt(() => {
        RequireLoaded();
        if (HasPendingRuleChanges || VersionText != workspace!.Knowledge.Version) ApplyCore();
        string backup = workspace!.Save();
        Refresh(selectedRuleId);
        Message = backup.Length == 0 ? "Примененных изменений нет" : "Файл сохранен. Резервная копия: " + backup;
    });

    [RelayCommand]
    private void UseForConsultation() => Attempt(() => {
        RequireLoaded(); RequireApplied();
        KnowledgeJson.Require(!workspace!.IsDirty && VersionText == workspace!.Knowledge.Version, "сначала сохраните или сбросьте черновик");
        workspace!.EnsureDiskUnchanged();
        usePath?.Invoke(workspace!.PathName);
        Message = usePath is null ? "Путь готов: " + workspace!.PathName : "Путь выбран для следующих консультаций; активный сеанс не изменен. Нажмите Новая консультация для загрузки";
    });

    [RelayCommand] private void AddComparison() => AddNode(DraftConditionKind.Comparison);
    [RelayCommand] private void AddAll() => AddNode(DraftConditionKind.All);
    [RelayCommand] private void AddAny() => AddNode(DraftConditionKind.Any);
    [RelayCommand] private void WrapAll() => Wrap(DraftConditionKind.All);
    [RelayCommand] private void WrapAny() => Wrap(DraftConditionKind.Any);

    private void AddNode(DraftConditionKind kind) => Attempt(() => {
        KnowledgeJson.Require(Draft is not null && SelectedNode is not null && !SelectedNode.IsComparison, "выберите группу И/ИЛИ; сравнение можно обернуть кнопкой Корень И/ИЛИ");
        KnowledgeJson.Require(Nodes(Draft!.Root).Count() < 4096 && Depth(Draft!.Root) < 16 && SelectedNode!.Children.Count < 128, "достигнут предел дерева редактора");
        ConditionDraft node = kind == DraftConditionKind.Comparison ? Comparison() : new ConditionDraft(kind);
        SelectedNode!.Children.Add(node); Observe(); SelectedNode = node; MarkChanged();
    });

    private void Wrap(DraftConditionKind kind) => Attempt(() => {
        KnowledgeJson.Require(Draft is not null && Depth(Draft!.Root) < 16 && Nodes(Draft!.Root).Count() < 4096, "нет правила или достигнут предел дерева");
        ConditionDraft group = new ConditionDraft(kind);
        group.Children.Add(Draft!.Root); Draft!.Root = group;
        Observe(); UpdateTree(); MarkChanged();
    });

    [RelayCommand]
    private void RemoveNode() => Attempt(() => {
        KnowledgeJson.Require(Draft is not null && SelectedNode is not null && SelectedNode != Draft!.Root, "корень не удаляется; выберите дочерний узел");
        ConditionDraft? parent = Nodes(Draft!.Root).FirstOrDefault(node => node.Children.Contains(SelectedNode!));
        KnowledgeJson.Require(parent is not null, "родитель узла не найден");
        parent!.Children.Remove(SelectedNode!); Observe(); SelectedNode = parent; MarkChanged();
    });

    [RelayCommand]
    private void AddConclusion() => Attempt(() => {
        RequireLoaded(); KnowledgeJson.Require(Draft is not null && Draft!.Conclusions.Count < 128, "нужно выбранное правило с менее 128 заключениями");
        FactDefinition fact = workspace!.Knowledge.Facts[ConclusionFactIds[0]];
        ConclusionDraft item = new ConclusionDraft { FactId = fact.Id, ValueText = RuleDraftCodec.DefaultValue(fact) };
        Draft!.Conclusions.Add(item); Observe(); SelectedConclusion = item; MarkChanged();
    });

    [RelayCommand]
    private void RemoveConclusion() {
        if (Draft is null || SelectedConclusion is null) return;
        Draft!.Conclusions.Remove(SelectedConclusion); Observe(); SelectedConclusion = Draft!.Conclusions.FirstOrDefault(); MarkChanged();
    }

    partial void OnSelectedNodeChanged(ConditionDraft? value) { OnPropertyChanged(nameof(IsComparisonSelected)); OnPropertyChanged(nameof(ValueHint)); }
    partial void OnSelectedConclusionChanged(ConclusionDraft? value) => OnPropertyChanged(nameof(ConclusionHint));

    private void Refresh(string? id) {
        suppress = true;
        RuleIds = workspace!.Knowledge.Rules.Select(rule => rule.Id).OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        ConditionFactIds = workspace!.Knowledge.Facts.Keys.OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        ConclusionFactIds = workspace!.Knowledge.Facts.Values.Where(fact => fact.Role != FactRole.Input).Select(fact => fact.Id).OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        VersionText = workspace!.Knowledge.Version;
        suppress = false;
        Select(id is not null && RuleIds.Contains(id) ? id : RuleIds.FirstOrDefault());
        Information = workspace!.PathName + "\nБЗ " + workspace!.Knowledge.Id + " | правил: " + RuleIds.Length
            + " | черновик изменен: " + workspace!.IsDirty + " | Undo: " + workspace!.CanUndo + "\nSHA256 черновика: " + workspace!.Knowledge.Sha256;
        OnPropertyChanged(nameof(WorkingKnowledge));
        OnPropertyChanged(nameof(CanEdit));
    }

    private void Select(string? id) {
        Unobserve(); suppress = true;
        selectedRuleId = id; editedId = id;
        OnPropertyChanged(nameof(SelectedRuleId));
        Draft = id is null || workspace is null ? null : RuleDraftCodec.FromRule(workspace!.Knowledge.Rules.Single(rule => rule.Id == id));
        HasPendingRuleChanges = false;
        Observe(); UpdateTree(); SelectedConclusion = Draft?.Conclusions.FirstOrDefault();
        OnPropertyChanged(nameof(IsExistingRule));
        suppress = false;
    }

    private void UpdateTree() { ConditionRoots = Draft is null ? ImmutableArray<ConditionDraft>.Empty : ImmutableArray.Create(Draft!.Root); SelectedNode = Draft?.Root; }
    private ConditionDraft Comparison() {
        FactDefinition fact = workspace!.Knowledge.Facts[ConditionFactIds[0]];
        return new ConditionDraft(DraftConditionKind.Comparison) { FactId = fact.Id, ValueText = RuleDraftCodec.DefaultValue(fact) };
    }

    // переподписываемся на текущие поля и листья, чтобы замененные поддеревья больше не меняли признак несохраненных полей
    private void Observe() {
        Unobserve();
        if (Draft is null) return;
        observed.Add(Draft); observed.AddRange(Nodes(Draft!.Root).Where(node => node.IsComparison)); observed.AddRange(Draft!.Conclusions);
        foreach (INotifyPropertyChanged item in observed) item.PropertyChanged += Changed;
    }
    private void Unobserve() { foreach (INotifyPropertyChanged item in observed) item.PropertyChanged -= Changed; observed.Clear(); }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (!suppress) MarkChanged(); }
    private void MarkChanged() { HasPendingRuleChanges = true; Message = "Поля изменены; Применить поля проверит полный документ без записи файла"; OnPropertyChanged(nameof(ValueHint)); OnPropertyChanged(nameof(ConclusionHint)); }
    private string Hint(string? id) {
        if (id is null || workspace is null || !workspace!.Knowledge.Facts.TryGetValue(id, out FactDefinition? fact)) return "";
        return fact.Id + " — " + fact.Name + " | " + fact.Type + (fact.Type == FactType.Boolean ? " | true / false"
            : fact.Type == FactType.Enum ? " | " + string.Join(" / ", fact.Options) : " | JSON-число с точкой, границы " + fact.Minimum + " .. " + fact.Maximum);
    }
    private void RequireLoaded() => KnowledgeJson.Require(workspace is not null && CanEdit, "сначала загрузите рабочую копию вне папки программы");
    private void RequireApplied() => KnowledgeJson.Require(!HasPendingRuleChanges && (workspace is null || VersionText == workspace!.Knowledge.Version), "примените или сбросьте поля текущего правила и версии");
    private void RequireClean() => KnowledgeJson.Require(workspace is null || (!workspace!.IsDirty && !HasPendingRuleChanges && VersionText == workspace!.Knowledge.Version), "есть несохраненные изменения; сохраните их или сбросьте весь черновик");
    private void Attempt(Action action) {
        try { action(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or NotSupportedException) { Message = "Операция не выполнена: " + exception.Message; }
    }
    private static IEnumerable<ConditionDraft> Nodes(ConditionDraft node) { yield return node; foreach (ConditionDraft child in node.Children) foreach (ConditionDraft descendant in Nodes(child)) yield return descendant; }
    private static int Depth(ConditionDraft node) => 1 + (node.Children.Count == 0 ? 0 : node.Children.Max(Depth));
}
