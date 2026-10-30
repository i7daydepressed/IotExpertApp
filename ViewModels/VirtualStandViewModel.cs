using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Live;
using IotExpertApp.Services.Checks;
using IotExpertApp.Services.Knowledge;
using IotExpertApp.Services.Live;

namespace IotExpertApp.ViewModels;

public partial class VirtualStandViewModel : ViewModelBase {
    public LiveMonitorViewModel Live { get; }
    public DeviceProfile? Profile { get; private set; }
    public ProfileEvaluation? Result { get; private set; }
    private ImmutableDictionary<string, InputFact> overrides = ImmutableDictionary<string, InputFact>.Empty;
    private ImmutableDictionary<string, InputFact> calculated = ImmutableDictionary<string, InputFact>.Empty;
    private DeviceProfile? offered;
    public long Revision { get; private set; }
    public ObservableCollection<VirtualModuleViewModel> Modules { get; } = new ObservableCollection<VirtualModuleViewModel>();
    public ObservableCollection<VirtualFactViewModel> Conditions { get; } = new ObservableCollection<VirtualFactViewModel>();
    [ObservableProperty] private string profileSummary = "Профиль еще не загружен";
    [ObservableProperty] private string outcome = "Выберите профиль, затем задайте основания вручную";
    [ObservableProperty] private string message = "Виртуальный сценарий не получает телеметрию и не управляет платой";
    [ObservableProperty] private string missing = "";
    [ObservableProperty] private string missingTarget = "";
    [ObservableProperty] private string profileNotice = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsStandOpen))] private bool isConsultationOpen;
    public bool IsStandOpen => !IsConsultationOpen;
    [ObservableProperty] private ResultExplanationViewModel? diagnosticExplanation;
    [ObservableProperty] private ResultExplanationViewModel? evidenceExplanation;
    [ObservableProperty] private ResultExplanationViewModel? strategyExplanation;
    public event Action<string>? FocusRequested;
    public VirtualStandViewModel(LiveMonitorViewModel live) {
        Live = live;
        foreach (string id in CheckProtocol.Sources) Modules.Add(new VirtualModuleViewModel(id, Recalculate));
    }
    public void OfferProfile(DeviceProfile? profile) {
        if (profile is null || ReferenceEquals(offered, profile)) return;
        offered = profile;
        if (Profile is null) UseProfile(profile);
        else if (!ReferenceEquals(Profile, profile)) ProfileNotice = "Загружен другой комплект; виртуальный сценарий сохранен до явного выбора нового профиля";
    }
    public void UseProfile(DeviceProfile profile) {
        Profile = profile; overrides = ImmutableDictionary<string, InputFact>.Empty; ProfileNotice = "";
        ProfileSummary = profile.Title + " · " + profile.Version + "\nРучной сценарий; значения профиля не являются измерениями";
        Conditions.Clear();
        foreach (string id in profile.StrategyKnowledge.QuestionOrder) Conditions.Add(new VirtualFactViewModel(profile.StrategyKnowledge.Facts[id], SetOverride, ResetOverride));
        foreach (VirtualModuleViewModel module in Modules) module.Reset();
        Recalculate();
    }
    [RelayCommand]
    private async Task LoadProfileAsync() {
        if (IsLoading || Live.IsLoading) return; IsLoading = true;
        try { await Live.ReloadCommand.ExecuteAsync(null); if (Live.Profile is null || Live.LoadMessage.StartsWith("Профиль не заменен:", StringComparison.Ordinal)) Message = Live.LoadMessage; else { offered = Live.Profile; UseProfile(Live.Profile); } }
        finally { IsLoading = false; }
    }
    public void SetOverride(string id, KnowledgeValue? value) {
        if (Profile is null) return;
        overrides = overrides.SetItem(id, new InputFact(id, value, "manual override:" + id + "; задано вручную; at=" + DateTimeOffset.UtcNow.ToString("O"))); Recalculate();
    }
    public void ResetOverride(string id) { overrides = overrides.Remove(id); Recalculate(); }
    public void Recalculate() {
        if (Profile is null) return;
        Revision++; DiagnosticExplanation = EvidenceExplanation = StrategyExplanation = null;
        ImmutableDictionary<string, InputFact> observations = Modules.ToImmutableDictionary(m => m.Source, m => new InputFact(m.Source, new KnowledgeValue(FactType.Enum, m.Observation), m.ObservationOrigin), StringComparer.Ordinal);
        ImmutableArray<ModuleEvidence> witnesses = Modules.Select(m => m.Witness).Where(w => w is not null).Select(w => w!).ToImmutableArray();
        EvidenceAssessmentService chain = new EvidenceAssessmentService();
        ProfileEvaluation baseline = chain.Evaluate(Profile, observations, true, true, witnesses);
        calculated = baseline.Strategy.Inputs;
        Result = overrides.Count == 0 ? baseline : chain.Evaluate(Profile, observations, true, true, witnesses, overrides);
        string status = ResultExplanationViewModel.StatusLabel(Result.Strategy.Status);
        if (Result.Strategy.Status == InferenceStatus.Success) {
            string goal = LiveAssessmentService.Goal(Result.Strategy); ConsultationOutcome result = Profile.StrategyKnowledge.Outcomes[goal];
            Outcome = goal + " · " + result.Title + "\n" + result.Description;
        }
        else Outcome = status;
        Message = "Задано вручную · редакция " + Revision + "; " + Result.Note + (overrides.Count > 0 ? "; ручных переопределений: " + overrides.Count : "");
        MissingTarget = Result.Strategy.MissingInputs.Contains("V4") ? "V4" : Result.Strategy.MissingInputs.FirstOrDefault() ?? "";
        Missing = MissingTarget.Length == 0 ? "" : MissingTarget == "V4" ? "Неизвестно, разрешено ли продолжать работу — уточните V4" : "Неизвестно: " + Profile.StrategyKnowledge.Facts[MissingTarget].Question + " — уточните " + MissingTarget;
        if (MissingTarget.Length == 0 && Result.Diagnostic?.Status == InferenceStatus.Success && LiveAssessmentService.Goal(Result.Diagnostic) == "incomplete") {
            string? source = Profile.Sources.Select(s => s.SourceId).FirstOrDefault(s => Modules.Single(m => m.Source == s).Observation is not ("ok" or "error" or "unavailable"));
            if (source is not null) { MissingTarget = source; Missing = "Сначала задайте обязательное наблюдение " + source + "; выбирать остальные условия наугад не требуется"; }
        }
        foreach (VirtualFactViewModel fact in Conditions) fact.Update(calculated.TryGetValue(fact.Definition.Id, out InputFact? input) ? input : null, overrides.TryGetValue(fact.Definition.Id, out InputFact? manual) ? manual : null, overrides.ContainsKey(fact.Definition.Id), fact.Definition.Id == MissingTarget);
        OnPropertyChanged(nameof(Result));
    }
    [RelayCommand]
    private void Explain() {
        if (Result is null || Profile is null) return;
        string context = "Виртуальный стенд; задано вручную; редакция " + Revision + "\n" + Profile.Title + " / " + Profile.Version;
        DiagnosticExplanation = Result.Diagnostic is null ? null : new ResultExplanationViewModel(Result.Diagnostic, context + "\nНаблюдаемость");
        EvidenceExplanation = Result.Evidence is null ? null : new ResultExplanationViewModel(Result.Evidence, context + "\nСвязь свидетельств");
        StrategyExplanation = new ResultExplanationViewModel(Result.Strategy, context + "\nСтратегия ЛР1");
    }
    [RelayCommand] private void FocusMissing() { if (MissingTarget.Length > 0) FocusRequested?.Invoke(MissingTarget); }
    [RelayCommand] private void OpenConsultation() => IsConsultationOpen = true;
    [RelayCommand] private void CloseConsultation() => IsConsultationOpen = false;
}

public partial class VirtualModuleViewModel : ViewModelBase {
    private readonly Action changed;
    private bool suppress;
    private int revision;
    private ModuleEvidence? lastCompleted;
    public string Source { get; }
    public string Title => Source switch { "esp32" => "ESP32 · RAM", "oled" => "OLED", "dht11" => "DHT11", "light" => "Свет", "ir" => "ИК", "pir" => "PIR", "microsd" => "microSD", "relay_1" => "Реле 1", _ => "Реле 2" };
    public bool NeedsHuman => CheckProtocol.Human(Source) is not null;
    public bool IsDigital => Source is "light" or "ir" or "pir";
    public string Limitation => Source switch { "oled" => "Успех программы — передача и возврат панели; видимость задается отдельно", "relay_1" or "relay_2" => "Успех — импульс и возврат GPIO; контакты не измеряются", "pir" => "Переходы raw не подтверждают питание или движение", "microsd" => "Успех — файловый тест с очисткой, не емкость/наличие карты", _ => "Только основания правил, без выдуманных физических порогов" };
    public string ObservationOrigin { get; private set; } = "Не задано вручную";
    public string Observation => SelectedObservation?.Canonical ?? "unknown";
    public ModuleEvidence? Witness => SelectedMachine?.Canonical is "pending" or "rejected" ? lastCompleted ?? Build(SelectedMachine.Canonical) : lastCompleted;
    public ImmutableArray<ConsultationChoice> ObservationOptions { get; } = ImmutableArray.Create(new ConsultationChoice("Неизвестно", "unknown"), new ConsultationChoice("Успешное наблюдение", "ok"), new ConsultationChoice("Ошибка", "error"), new ConsultationChoice("Недоступен", "unavailable"), new ConsultationChoice("Не опрошен", "not_polled"), new ConsultationChoice("Отключен", "disabled"), new ConsultationChoice("Устарело", "stale"));
    public ImmutableArray<ConsultationChoice> MachineOptions { get; } = ImmutableArray.Create(new ConsultationChoice("Не задан / не проводился", "none"), new ConsultationChoice("Программная проверка успешна", "ok"), new ConsultationChoice("Машинная ошибка", "error"), new ConsultationChoice("Не выполнена", "rejected"), new ConsultationChoice("Выполняется", "pending"), new ConsultationChoice("Исход неизвестен", "unknown"));
    public ImmutableArray<ConsultationChoice> HumanOptions { get; } = ImmutableArray.Create(new ConsultationChoice("Ответ отсутствует", "absent"), new ConsultationChoice("Да", "yes"), new ConsultationChoice("Нет", "no"), new ConsultationChoice("Не знаю", "unknown"), new ConsultationChoice("Неясно", "unclear"), new ConsultationChoice("Не проверял / пропуск", "skipped"), new ConsultationChoice("Срок истек без ответа", "timeout"));
    public ImmutableArray<ConsultationChoice> SignalOptions { get; } = ImmutableArray.Create(new ConsultationChoice("Переходы неизвестны", "unknown"), new ConsultationChoice("Переходы зарегистрированы", "changes"), new ConsultationChoice("Переходов нет", "no_changes"));
    [ObservableProperty] private ConsultationChoice? selectedObservation;
    [ObservableProperty] private ConsultationChoice? selectedMachine;
    [ObservableProperty] private ConsultationChoice? selectedHuman;
    [ObservableProperty] private ConsultationChoice? selectedSignal;
    [ObservableProperty] private string checkSummary = "Выборочная проверка не задана";
    public VirtualModuleViewModel(string source, Action changed) { Source = source; this.changed = changed; Reset(); }
    public void Reset() { suppress = true; SelectedObservation = ObservationOptions[0]; SelectedMachine = MachineOptions[0]; SelectedHuman = HumanOptions[0]; SelectedSignal = SignalOptions[0]; lastCompleted = null; revision = 0; ObservationOrigin = "Не задано вручную; source=" + Source; CheckSummary = "Выборочная проверка не задана"; suppress = false; }
    partial void OnSelectedObservationChanged(ConsultationChoice? value) { if (!suppress) { ObservationOrigin = "manual observation:" + Source + "; значение=" + Observation + "; at=" + DateTimeOffset.UtcNow.ToString("O"); changed(); } }
    partial void OnSelectedMachineChanged(ConsultationChoice? value) => Capture();
    partial void OnSelectedHumanChanged(ConsultationChoice? value) => Capture();
    partial void OnSelectedSignalChanged(ConsultationChoice? value) => Capture();
    private ModuleEvidence Build(string machine) => new ModuleEvidence(Source, machine, NeedsHuman ? SelectedHuman?.Canonical ?? "absent" : "not_required", IsDigital ? SelectedSignal?.Canonical ?? "unknown" : "not_applicable",
        "manual virtual:" + Source + "; Revision=" + revision + "; задано вручную; at=" + DateTimeOffset.UtcNow.ToString("O"), revision, null, DateTimeOffset.UtcNow, "Задан вручную");
    private void Capture() {
        if (suppress) return; revision++; string machine = SelectedMachine?.Canonical ?? "none"; if (machine == "none") lastCompleted = null; else if (machine is not ("pending" or "rejected")) lastCompleted = Build(machine);
        CheckSummary = (machine is "pending" or "rejected") && lastCompleted is not null ? "Новый тест не выполнен или не завершен; учитывается предыдущий завершенный результат" : lastCompleted is null ? "Завершенного теста нет" : "Задан вручную · редакция " + lastCompleted.Revision; changed();
    }
}

public partial class VirtualFactViewModel : ViewModelBase {
    private readonly Action<string, KnowledgeValue?> set;
    private readonly Action<string> reset;
    public FactDefinition Definition { get; }
    public string Title => Definition.Id + " · " + Definition.Name.Replace('_', ' ');
    public string Question => Definition.Question ?? Definition.Name;
    public bool IsNumeric => Definition.Type is FactType.Number or FactType.Integer;
    public ImmutableArray<ConsultationChoice> Choices { get; }
    [ObservableProperty] private string valueText = "Неизвестно";
    [ObservableProperty] private string originText = "Не задано";
    [ObservableProperty] private bool hasOverride;
    [ObservableProperty] private string numericText = "";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool needsAttention;
    public VirtualFactViewModel(FactDefinition definition, Action<string, KnowledgeValue?> set, Action<string> reset) {
        Definition = definition; this.set = set; this.reset = reset;
        Choices = definition.Type == FactType.Boolean ? ImmutableArray.Create(new ConsultationChoice("Да", "true"), new ConsultationChoice("Нет", "false")) : definition.Options.Select(v => new ConsultationChoice(v, v)).ToImmutableArray();
    }
    public void Update(InputFact? calculated, InputFact? manual, bool overridden, bool needed) {
        HasOverride = overridden; NeedsAttention = needed; InputFact? value = overridden ? manual : calculated;
        ValueText = value?.Value is null ? "Неизвестно" : KnowledgeTextFormatter.Value(value.Value);
        OriginText = overridden ? "Переопределено вручную" : value is null ? "Не задано" : value.Origin.StartsWith("profile:", StringComparison.Ordinal) ? "Из профиля" : "Получено выводом";
    }
    [RelayCommand] private void Choose(ConsultationChoice? choice) { if (choice is not null) Accept(choice.Canonical); }
    [RelayCommand] private void Number() => Accept(NumericText);
    private void Accept(string text) { try { string json = Definition.Type == FactType.Enum ? JsonSerializer.Serialize(text) : text; using JsonDocument document = KnowledgeJson.Parse(Encoding.UTF8.GetBytes(json)); KnowledgeValue value = KnowledgeJson.Value(document.RootElement, Definition, Definition.Id); Error = ""; set(Definition.Id, value); } catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException) { Error = e.Message; } }
    [RelayCommand] private void Unknown() { Error = ""; set(Definition.Id, null); }
    [RelayCommand] private void Reset() { Error = ""; reset(Definition.Id); }
}
