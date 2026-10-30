using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Linq;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Checks;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Knowledge;
using IotExpertApp.Services.Live;

namespace IotExpertApp.ViewModels;

public partial class LiveMonitorViewModel : ViewModelBase {
    private readonly LiveAssessmentService assessor = new LiveAssessmentService();
    private ReceivedSnapshot? lastSnapshot;
    private ReceivedSnapshot? signalSnapshot;
    private string? lastKey;
    private ReceiverState? lastState;
    private ImmutableArray<GuidedCheckResult> lastChecks = ImmutableArray<GuidedCheckResult>.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(AssessmentMode))] private bool includeCheckAnswers;
    [ObservableProperty] private string checkEvidenceSummary = "";
    [ObservableProperty] private ResultExplanationViewModel? frozenEvidenceExplanation;
    [ObservableProperty] private string frozenChecksSummary = "";
    public bool CanUseCheckAnswers => Profile?.EvidencePolicy is not null;
    public string AssessmentMode => IncludeCheckAnswers && CanUseCheckAnswers ? "По телеметрии и ответам пользователя" : "По телеметрии";
    partial void OnIncludeCheckAnswersChanged(bool value) {
        lastKey = null;
        if (lastState is not null) Update(lastState,lastChecks);
    }
    public ObservableCollection<ExplanationTreeNode> DiagnosticRoots { get; } = new ObservableCollection<ExplanationTreeNode>();
    public ObservableCollection<ExplanationTreeNode> StrategyRoots { get; } = new ObservableCollection<ExplanationTreeNode>();
    [ObservableProperty] private string selectedOutcome = "Объяснение еще не закреплено";
    [ObservableProperty] private string selectedSnapshot = "Нажмите Зафиксировать объяснение для принятого снимка";
    [ObservableProperty] private string selectedObservations = "";
    [ObservableProperty] private string strategyBridge = "";
    [ObservableProperty] private string knowledgeSummary = "Профиль и базы знаний не загружены";
    [ObservableProperty] private bool explanationOpen;
    [ObservableProperty] private string explanationHistory = "Еще нет закрепленного объяснения";
    [ObservableProperty] private ResultExplanationViewModel? frozenDiagnosticExplanation;
    [ObservableProperty] private ResultExplanationViewModel? frozenStrategyExplanation;
    [ObservableProperty] private string missingExplanation = "";
    public event Action? ExplanationRequested;
    public DeviceProfile? Profile { get; private set; }
    public LiveAssessment? Latest { get; private set; }
    public LiveAssessment? Frozen { get; private set; }
    public bool CanFreeze => Latest is not null;
    public bool CanReload => !IsLoading;
    [ObservableProperty] private string profilePath = Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "esp32-observability.json");
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanReload))][NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private bool isLoading;
    [ObservableProperty] private string title = "Экспертная оценка еще не начата";
    [ObservableProperty] private string recommendation = "Выберите порт и подключитесь; профиль загрузится автоматически";
    [ObservableProperty] private string context = "";
    [ObservableProperty] private string loadMessage = "";
    [ObservableProperty] private string profileLoadStatus = "Профиль не загружен";
    [ObservableProperty] private string signalText = "Показания еще не получены";
    [ObservableProperty] private string explanationText = "Нажмите Зафиксировать объяснение: новые пакеты не будут менять выбранное объяснение";

    public async Task EnsureLoadedAsync() {
        if (Profile is null && !IsLoading) await ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync() {
        if (IsLoading) return;
        IsLoading = true;
        string path = ProfilePath;
        LoadMessage = "Загрузка профиля и правил";
        ProfileLoadStatus = LoadMessage;
        try {
            // на рабочем потоке создаем целый неизменяемый комплект, UI меняем после await
            DeviceProfile replacement = await Task.Run(() => new DeviceProfileService().Load(path));
            Profile = replacement;
            if (replacement.EvidencePolicy is null) IncludeCheckAnswers = false;
            KnowledgeSummary = "Профиль: " + replacement.Title + " · " + replacement.Version
                + "\nДиагностика: " + replacement.DiagnosticKnowledge.Id + " · " + replacement.DiagnosticKnowledge.Version
                + "\nСтратегия: " + replacement.StrategyKnowledge.Id + " · " + replacement.StrategyKnowledge.Version
                + "\nВыборочные проверки: " + (replacement.ModuleKnowledge is null ? "расширение отсутствует" : replacement.ModuleKnowledge.Id + " · " + replacement.ModuleKnowledge.Version)
                + "\nСвязь ответов: " + (replacement.EvidencePolicy is null ? "расширение отсутствует" : replacement.EvidencePolicy.Knowledge.Id + " · " + replacement.EvidencePolicy.Knowledge.Version);
            Latest = null;
            lastSnapshot = null;
            lastKey = null;
            Title = "Профиль загружен, ожидание оценки";
            Recommendation = "Результат появится по принятой телеметрии";
            Context = replacement.Title + "\n" + replacement.Scope;
            LoadMessage = "Активный профиль: " + replacement.Path;
            ProfileLoadStatus = "Профиль и правила загружены";
            NotifyFreeze();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or NotSupportedException) {
            LoadMessage = "Профиль не заменен: " + exception.Message;
            ProfileLoadStatus = Profile is null ? "Ошибка загрузки профиля; подробности ниже" : "Ошибка загрузки; сохранен прежний комплект правил";
            if (Profile is null) {
                Title = "Профиль не загружен";
                Recommendation = "Проверьте путь к профилю и разрешите доступ к его файлам, затем нажмите Загрузить профиль";
            }
        }
        finally { IsLoading = false; }
    }

    public void Update(ReceiverState state, ImmutableArray<GuidedCheckResult> checks = default) {
        lastState = state; lastChecks = checks.IsDefault ? ImmutableArray<GuidedCheckResult>.Empty : checks;
        if (!ReferenceEquals(signalSnapshot, state.Snapshot)) {
            signalSnapshot = state.Snapshot;
            SignalText = "Показания последнего принятого снимка";
            if (signalSnapshot is null) SignalText += ": нет пакета";
            else foreach (Observation item in signalSnapshot.Packet.Observations) {
                if (item.SourceId is "dht11" or "light" or "ir")
                    SignalText += "\n" + item.SourceId + ": " + item.Status + "; последний успех="
                        + item.LastSuccessMs + " мс; values=" + (item.Values?.GetRawText() ?? "нет") + "; " + item.ErrorDetail;
                if (item.SourceId == "pir") SignalText += "\npir: " + item.Status + "; raw=" + item.RawLevel + " — не подтвержденное движение";
            }
        }
        DeviceProfile? profile = Profile;
        if (profile is null) return;
        TimeSpan age = state.Snapshot is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(state.Snapshot.ReceivedTick);
        EvidenceSelection selected = new EvidenceAssessmentService().Select(profile,state,lastChecks);
        CheckEvidenceSummary = IncludeCheckAnswers ? EvidenceAssessmentService.Describe(selected) : "Ответы выборочных проверок не учитываются";
        string key = assessor.RefreshKey(profile, state, age) + "/checks=" + IncludeCheckAnswers + (IncludeCheckAnswers ? EvidenceAssessmentService.Key(selected) : "");
        if (ReferenceEquals(lastSnapshot, state.Snapshot) && key == lastKey) return;
        lastSnapshot = state.Snapshot;
        lastKey = key;
        Latest = assessor.Assess(profile, state, age, IncludeCheckAnswers, lastChecks);
        Title = Latest.Title;
        Recommendation = Latest.Recommendation;
        Context = Latest.Context;
        NotifyFreeze();
    }

    [RelayCommand(CanExecute = nameof(CanFreeze))]
    private void Freeze() {
        LiveAssessment? selected = Latest;
        if (selected is null) return;
        string text = "ЗАФИКСИРОВАННЫЙ РЕЗУЛЬТАТ\n" + selected.Title + "\n" + selected.Recommendation
            + "\n" + selected.Context;
        ExplanationService explanation = new ExplanationService();
        if (selected.Diagnostic is not null) text += "\n\n1. Диагностические правила профиля\n" + explanation.Describe(selected.Diagnostic);
        if (selected.Strategy is not null) text += "\n\n2. Правила выбора стратегии ЛР1\n" + explanation.Describe(selected.Strategy);
        if (selected.Diagnostic is null && selected.Strategy is null) text += "\nРасчет не выполнялся: оснований для текущей рекомендации нет";
        Frozen = selected;
        SelectedOutcome = selected.Title + "\n" + selected.Recommendation;
        ReceivedSnapshot? snapshot = selected.Snapshot;
        SelectedSnapshot = snapshot is null ? "Принятого снимка нет" : snapshot.Origin + "\n" + snapshot.Packet.DeviceId
            + " | boot " + snapshot.Packet.BootId + " | sequence " + snapshot.Packet.Sequence
            + "\nПринято " + snapshot.ReceivedAt.ToString("O") + " | uptime " + snapshot.Packet.UptimeMs + " мс";
        SelectedObservations = selected.Diagnostic is null ? "Диагностический расчет не выполнялся"
            : string.Join("\n", selected.Diagnostic.Inputs.Values.OrderBy(i => i.FactId, StringComparer.Ordinal)
                .Select(i => selected.Diagnostic.KnowledgeBase.Facts[i.FactId].Name + ": " + (i.Value?.Canonical ?? "неизвестно")));
        StrategyBridge = selected.Strategy is null ? "Текущая стратегия не рассчитывалась" : string.Join("\n",
            selected.Strategy.Inputs.Values.OrderBy(i => i.FactId, StringComparer.Ordinal)
                .Select(i => i.FactId + " · " + selected.Strategy.KnowledgeBase.Facts[i.FactId].Name + " = " + (i.Value?.Canonical ?? "неизвестно") + "\n" + i.Origin));
        DiagnosticRoots.Clear(); StrategyRoots.Clear();
        ExplanationTreeBuilder builder = new ExplanationTreeBuilder();
        if (selected.Diagnostic is not null) foreach (ExplanationTreeNode node in builder.Build(selected.Diagnostic)) DiagnosticRoots.Add(node);
        if (selected.Strategy is not null) foreach (ExplanationTreeNode node in builder.Build(selected.Strategy)) StrategyRoots.Add(node);
        ExplanationText = text;
        string compactContext = selected.Title + "\n" + (snapshot is null ? "Принятого пакета нет" : snapshot.Packet.DeviceId + "; sequence " + snapshot.Packet.Sequence + "\nПакет принят " + snapshot.ReceivedAt.ToString("O"));
        FrozenDiagnosticExplanation = selected.Diagnostic is null ? null : new ResultExplanationViewModel(selected.Diagnostic, "Наблюдаемость стенда (OBS)\n" + compactContext, SelectedSnapshot + "\n" + selected.Context);
        FrozenChecksSummary = selected.Evaluation?.Note + "\n" + (selected.Evaluation is null ? "" : EvidenceAssessmentService.Describe(new EvidenceSelection(selected.Evaluation.Witnesses,ImmutableArray<string>.Empty)));
        FrozenEvidenceExplanation = selected.Evaluation?.Evidence is null ? null : new ResultExplanationViewModel(selected.Evaluation.Evidence,"Связь свидетельств\n" + compactContext + "\n" + selected.Evaluation.Note, selected.Context);
        FrozenStrategyExplanation = selected.Strategy is null ? null : new ResultExplanationViewModel(selected.Strategy, "Выбор стратегии ЛР1; входы переданы мостом, не ручным сеансом\n" + compactContext, SelectedSnapshot + "\n" + selected.Context);
        MissingExplanation = selected.Diagnostic is null || selected.Strategy is null ? "Отсутствующий расчет: " + selected.Title + "\n" + selected.Recommendation : "";
        ExplanationHistory = "Объяснение закреплено для выбранного результата; новые данные обновляют текущую оценку отдельно";
        ExplanationOpen = true; ExplanationRequested?.Invoke();
    }

    private void NotifyFreeze() {
        if (Frozen is not null && !ReferenceEquals(Frozen, Latest)) ExplanationHistory = "История: текущая оценка изменилась; открытый расчет и его БЗ сохранены";
        OnPropertyChanged(nameof(CanFreeze));
        OnPropertyChanged(nameof(CanUseCheckAnswers)); OnPropertyChanged(nameof(AssessmentMode));
        FreezeCommand.NotifyCanExecuteChanged();
    }
}
