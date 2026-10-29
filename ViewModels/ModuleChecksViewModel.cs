using System;
using System.Collections.ObjectModel;
using System.Collections.Immutable;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Knowledge;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Checks;
using IotExpertApp.Services.Telemetry;

namespace IotExpertApp.ViewModels;

public partial class ModuleChecksViewModel : ViewModelBase {
    private readonly TelemetryReceiverService receiver;
    private readonly Func<DeviceProfile?> profile;
    private GuidedCheckResult? shownGuided;
    private bool inspectingSelection;
    public ObservableCollection<ExplanationTreeNode> GuidedTree { get; } = new ObservableCollection<ExplanationTreeNode>();
    public uint ProtocolVersion => receiver.GetChecks().ProtocolVersion;
    public event Action? SelectionRequested;
    public ModuleCardViewModel? SelectedCard { get; private set; }
    [ObservableProperty] private string responseSecondsText = "30";
    [ObservableProperty] private string settingsMessage = "";
    public string SettingsPath => CheckPreferences.DefaultPath;
    [ObservableProperty] private string selectedTitle = "Управляемая проверка";
    [ObservableProperty] private string instruction = "Выберите модуль кнопкой Проверить";
    [ObservableProperty] private string question = "";
    [ObservableProperty] private string phaseText = "Не начата";
    [ObservableProperty] private string remaining = "";
    [ObservableProperty] private string verdict = "";
    [ObservableProperty] private string machineText = "";
    [ObservableProperty] private string humanText = "Ответ пользователя отсутствует";
    [ObservableProperty] private string workflowDetails = "";
    [ObservableProperty] private string summary = "Выборочные проверки еще не проводились";
    [ObservableProperty] private decimal windowDuration = 3000;
    [ObservableProperty] private bool canAnswer;
    [ObservableProperty] private bool canBegin;
    [ObservableProperty] private bool isObserveSelected;
    private long shownRevision = -1;
    private long shownErrors = -1;
    private string? shownMessage;
    public ObservableCollection<ModuleCardViewModel> Cards { get; } = new ObservableCollection<ModuleCardViewModel>();
    [ObservableProperty] private string message = "Команды доступны только после v1 совместимой прошивки и capabilities";
    [ObservableProperty] private string identity = "Нет текущего boot";
    [ObservableProperty] private bool relayReady;
    [ObservableProperty] private bool compatible;
    public ModuleChecksViewModel(TelemetryReceiverService receiver, Func<DeviceProfile?>? profileProvider = null) {
        this.receiver = receiver; profile = profileProvider ?? (() => null);
        (int seconds, string status) = CheckPreferences.Load(SettingsPath);
        ResponseSecondsText = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture); SettingsMessage = status;
        foreach (string source in CheckProtocol.Sources) Cards.Add(new ModuleCardViewModel(source, this));
    }
    partial void OnRelayReadyChanged(bool value) => receiver.AuthorizeRelay(value);
    public void Update(ReceiverState telemetry) {
        CheckSessionState state = receiver.GetChecks();
        Identity = state.Identity.Length == 0 ? "Нет текущего boot" : state.Identity;
        Compatible = state.Compatible;
        RelayReady = state.RelayAuthorized;
        if (shownRevision != state.Revision || shownErrors != telemetry.TestErrors || shownMessage != state.Message) {
            shownRevision = state.Revision; shownErrors = telemetry.TestErrors; shownMessage = state.Message;
            Message = state.Message + "\nTEST_EVENT: " + telemetry.TestEvents + "; ошибок событий: " + telemetry.TestErrors
                + (state.Capabilities is null ? "\nВозможности не получены" : "\nВозможности получены; initialized означает доступный программный путь")
                + "\nЗакрытие порта и тайм-аут не подтверждают остановку действия на плате";
        }
        foreach (ModuleCardViewModel card in Cards) card.Update(telemetry, state);
        UpdateGuided(telemetry);
    }
    public void Select(ModuleCardViewModel card) {
        if (receiver.GetGuided().Current?.Active == true) { Message = "Сначала завершите активную проверку; вопрос не изменен"; return; }
        inspectingSelection = true;
        MachineText = "Проверка выбранного модуля еще не начата"; HumanText = "Ответ человека отсутствует"; Remaining = ""; WorkflowDetails = ""; GuidedTree.Clear();
        SelectedCard = card; SelectedTitle = card.Title; Instruction = card.Limitation;
        IsObserveSelected = card.IsObserve; Question = ""; Verdict = "";
        PhaseText = card.IsObserve ? "Приготовьтесь → Начать окно" : card.IsRelay ? "Подтвердите условия нагрузки, затем начните импульс" : "Нажмите Начать проверку";
        CanBegin = card.CanStart;
        if (card.Source == "oled" && ProtocolVersion != 2) PhaseText = "Для проверки с ожиданием ответа обновите прошивку";
        SelectionRequested?.Invoke();
        if (!receiver.GetState().IsSerial) {
            CanBegin = false; PhaseText = "Запись: новая физическая проверка запрещена"; HumanText = "Ответ пользователя в записи отсутствует";
            MachineText = card.Machine;
            if (card.Selected is not null) {
                CheckEvent e = card.Selected.Result.Event;
                GuidedCheckResult archived = new GuidedCheckResult(e.Device,e.Boot,e.Firmware,e.Config,e.RequestId ?? 0,card.Source,e.Operation!,card.Selected.Result.Origin,
                    e.ProtocolVersion,30,null,profile()?.ModuleKnowledge,"finished","machine",TimeSpan.Zero,TimeSpan.Zero,null,e,null,false,null);
                archived = archived with { Inference = ModuleCheckAssessment.Run(archived) };
                Verdict = "Запись · " + ModuleCheckAssessment.Summary(archived); ShowTree(archived);
            }
        }
    }
    [RelayCommand] private void SavePreferences() {
        try { CheckPreferences.Save(SettingsPath, ResponseSecondsText); SettingsMessage = "Срок ответа сохранен: " + ResponseSecondsText + " с"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { SettingsMessage = e.Message; }
    }
    [RelayCommand] private void Begin() {
        if (SelectedCard is null) return;
        try {
            int seconds = CheckPreferences.Parse(ResponseSecondsText);
            CheckProtocol.Require(WindowDuration is >= 100 and <= 10000 && WindowDuration == decimal.Truncate(WindowDuration), "Окно GPIO: целое 100…10000 мс");
            if (receiver.StartGuided(SelectedCard.Source, seconds, (uint)WindowDuration, profile()?.ModuleKnowledge)) inspectingSelection = false;
            Message = receiver.GetGuided().Message;
        }
        catch (Exception e) when (e is IOException or ArgumentException) { Message = e.Message; }
    }
    [RelayCommand] private void GiveAnswer(string answer) { receiver.AnswerGuided(answer); UpdateGuided(receiver.GetState()); }
    [RelayCommand] private void Recover() { receiver.RecoverGuided(); UpdateGuided(receiver.GetState()); }
    [RelayCommand] private void RetryFinish() { receiver.RetryGuidedFinish(); UpdateGuided(receiver.GetState()); }
    private void UpdateGuided(ReceiverState telemetry) {
        (GuidedCheckResult? current, ImmutableArray<GuidedCheckResult> history, string status, TimeSpan now) = receiver.GetGuided();
        foreach (ModuleCardViewModel card in Cards) {
            GuidedCheckResult? result = history.LastOrDefault(r => r.Source == card.Source);
            card.GuidedSummary = result is null ? card.MachineSummary : (IsHistorical(result, telemetry) ? "История live · " : "") + ModuleCheckAssessment.Summary(result);
        }
        Summary = history.Length == 0 ? "Выборочные проверки еще не проводились" : "Проведено: " + history.Length
            + "; с подтверждением: " + history.Count(IsConfirmed)
            + "; с замечаниями: " + history.Count(r => !IsConfirmed(r))
            + "; без ответа: " + history.Count(r => r.Human is null && CheckProtocol.Human(r.Source) is not null)
            + " · " + ModuleCheckAssessment.Summary(history[^1]);
        CanAnswer = current?.Phase == "answer";
        CanBegin = current?.Active != true && SelectedCard?.CanStart == true && (SelectedCard.Source != "oled" || receiver.GetChecks().ProtocolVersion == 2);
        if (current is null || inspectingSelection && !current.Active) return;
        Remaining = current.Phase == "answer" && current.AnswerDeadline.HasValue ? "Осталось на ответ: " + Math.Max(0, Math.Ceiling((current.AnswerDeadline.Value - now).TotalSeconds)) + " с"
            : current.Phase == "machine" && current.Operation == "observe" ? "Измерительное окно, мс: " + current.WindowMs : "";
        PhaseText = !current.Active && IsHistorical(current, telemetry) ? "История завершенной проверки · " + status : status;
        if (ReferenceEquals(shownGuided, current)) return;
        shownGuided = current;
        SelectedTitle = Cards.Single(c => c.Source == current.Source).Title;
        Question = current.Source switch { "oled" => "Видны рамка, диагонали и OLED TEST?", "relay_1" or "relay_2" => "Щелчок выбранного реле наблюдался?", _ => Cards.Single(c => c.Source == current.Source).Question };
        if (CheckProtocol.Human(current.Source) is null) Question = "Ответ человека не требуется";
        MachineText = current.Machine is null ? "Машинный ответ еще не получен" : current.Machine.Stage + " · " + current.Machine.Reason
            + (current.Source == "oled" ? "\nПередача: " + MachineValue(current.Machine, "transfer_ok") + "; возврат панели: " + MachineValue(current.Machine, "dashboard_restored")
                : current.Operation == "observe" ? "\nПереходов: " + MachineValue(current.Machine, "transition_count") + "; выборок: " + MachineValue(current.Machine, "sample_count")
                    + "; max gap " + MachineValue(current.Machine, "max_sample_gap_ms") + " мс" : "");
        HumanText = current.Human is null ? current.EndReason == "timeout" ? "Пользователь не подтвердил: ответ не получен за " + current.ResponseSeconds + " с (ответ отсутствует)" : "Ответ человека отсутствует"
            : current.Human.Answer + " · " + current.Human.AnsweredAt.ToString("O");
        Verdict = ModuleCheckAssessment.Summary(current);
        WorkflowDetails = current.Device + " | boot " + current.Boot + " | request " + current.RequestId + " | " + current.Origin
            + "\n" + current.Firmware + " | protocol " + current.ProtocolVersion + " | редакция " + current.Revision
            + "\nСрок/окно: " + current.ResponseSeconds + " с / " + current.WindowMs + " мс; host deadline " + current.AnswerDeadline
            + "\nБЗ: " + current.Knowledge?.Id + " / " + current.Knowledge?.Version + " / " + current.Knowledge?.Sha256;
        Instruction = Cards.Single(c => c.Source == current.Source).Limitation;
        ShowTree(current);
    }
    private static bool IsHistorical(GuidedCheckResult result, ReceiverState telemetry) => !telemetry.Running || !telemetry.IsSerial
        || result.Origin != telemetry.Origin || telemetry.Snapshot is not null
        && (result.Device != telemetry.Snapshot.Packet.DeviceId || result.Boot != telemetry.Snapshot.Packet.BootId);
    private static bool IsConfirmed(GuidedCheckResult result) => result.Inference?.Status == InferenceStatus.Success
        && IotExpertApp.Services.Live.LiveAssessmentService.Goal(result.Inference) is "oled_seen" or "relay_heard" or "gpio_observed" or "pir_observed" or "metrics" or "new_read" or "file_test";
    private static string MachineValue(CheckEvent e, string key) => e.Data.TryGetProperty(key, out JsonElement value)
        ? value.ValueKind switch { JsonValueKind.Null => "неизвестно", JsonValueKind.True => "да", JsonValueKind.False => "нет", _ => value.ToString() } : "неизвестно";
    private void ShowTree(GuidedCheckResult result) {
        GuidedTree.Clear();
        if (result.Inference is not null) foreach (ExplanationTreeNode node in new ExplanationTreeBuilder().Build(result.Inference)) GuidedTree.Add(node);
    }
    public void Send(string source, string operation, uint? duration = null, ulong? target = null) {
        if (!receiver.QueueCheck(source, operation, duration, target, out string error)) Message = error;
        else Message = "Запрос поставлен в ограниченную очередь; ожидается ответ";
    }
    public void Query(CheckResult result) {
        CheckEvent e = result.Event;
        if (e.Device + "/" + e.Boot != receiver.GetChecks().Identity) { Message = "Это результат другого boot; status текущей платы его не восстанавливает"; return; }
        Send("esp32", "status", target: e.RequestId);
    }
    public void Answer(CheckResult result, string answer, string method) {
        receiver.AnswerCheck(result, answer, method);
    }
    [RelayCommand] private void Capabilities() => Send("esp32", "capabilities");
    [RelayCommand] private void AllOff() => Send("esp32", "all_off");
    [RelayCommand] private void PendingStatus() {
        ulong? id = receiver.GetChecks().PendingId;
        if (id.HasValue) Send("esp32", "status", target: id); else Message = "Нет локального ожидающего ID; выберите результат и запросите его status";
    }
    public async Task ExportAsync(Stream output) {
        CheckSessionState state = receiver.GetChecks();
        await JsonSerializer.SerializeAsync(output, new {
            schema_version = 1, exported_at = DateTimeOffset.UtcNow,
            scope = "Машинные результаты и ответы человека; не аппаратный PASS и не входы экспертной стратегии",
            results = state.History, human_answers = state.Answers,
            workflow = receiver.GetGuided().History.Select(r => new { result = r, tree = r.Inference is null ? null : (object)new ExplanationTreeBuilder().Build(r.Inference), trace = r.Inference is null ? "" : new ExplanationService().Describe(r.Inference) }).ToArray(),
            active_workflow = receiver.GetGuided().Current,
            selected = Cards.Where(c => c.Pinned).Select(c => c.Selected?.Result).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed record ModuleCheckChoice(CheckResult Result) {
    public override string ToString() => "ID " + Result.Event.RequestId + " · " + Result.Event.Stage + " · boot " + Result.Event.Boot + " · " + Result.Origin;
}

public partial class ModuleCardViewModel : ViewModelBase {
    private readonly ModuleChecksViewModel owner;
    private CheckResult? latest;
    public string Source { get; }
    public string Operation => CheckProtocol.Operation(Source, owner.ProtocolVersion);
    public bool IsObserve => Operation == "observe";
    public bool IsRelay => Operation == "pulse";
    public string Title => Source switch { "esp32" => "ESP32 · оперативная память", "oled" => "OLED", "dht11" => "DHT11 · окружающая среда", "light" => "Свет", "ir" => "ИК", "pir" => "PIR", "microsd" => "microSD", "relay_1" => "Реле 1 · GPIO32", _ => "Реле 2 · GPIO33" };
    public string ActionTitle => Operation switch { "snapshot" => "Снять метрики RAM", "display_test" or "display_hold" => "Показать тестовый рисунок", "read_next" => "Дождаться нового чтения", "observe" => "Наблюдать вход", "run" => "Проверить microSD", _ => "Импульс 900 мс" };
    public string Question => Source switch {
        "oled" => "Ожидаемое изображение видно?",
        "light" => "При освещении или закрытии датчика во время этого окна наблюдалось изменение сигнала?",
        "ir" => "При поднесении и удалении ладони во время этого окна наблюдалось изменение сигнала?",
        "pir" => "Вы выполнили движение перед линзой во время этого окна и наблюдали реакцию?",
        "relay_1" or "relay_2" => "Щелчок реле наблюдался?", _ => "Ответ человека не требуется"
    };
    public string Limitation => Source switch {
        "esp32" => "RAM, не свободное место SD; все ячейки памяти этим не проверяются",
        "oled" => "Передача отдельно от видимости. Рамка, диагонали и OLED TEST, затем штатная панель",
        "dht11" => "Температура воздуха, не кристалла ESP32; точность калибровки не проверяется",
        "light" => "Перед запуском освещайте/закрывайте датчик в течение выбранного окна. Это не люксы; 0 переходов не означает отказ",
        "ir" => "Перед запуском подносите/убирайте ладонь в течение окна. Расстояние не измеряется; быстрые фронты могут пропускаться",
        "pir" => "Перед запуском создайте движение перед линзой в течение окна. Raw и ответ не снимают power_not_verified и не измеряют VCC",
        "microsd" => "Последний тест исторический: старый успех не доказывает текущую доступность. Емкость/свободное место не измеряются",
        _ => "Нужны известные текущие условия нагрузки. ACK/GPIO/щелчок не доказывают контакты. Тайм-аут не выключает реле"
    };
    public ObservableCollection<ModuleCheckChoice> Results { get; } = new ObservableCollection<ModuleCheckChoice>();
    public string[] AnswerOptions { get; } = { "Да", "Нет", "Неясно", "Не проверял" };
    [ObservableProperty] private ModuleCheckChoice? selected;
    [ObservableProperty] private bool pinned;
    [ObservableProperty] private string observation = "Текущее наблюдение v1: еще нет данных";
    [ObservableProperty] private string guidedSummary = "Выборочная проверка еще не проводилась";
    [ObservableProperty] private string tileStatus = "Нет данных";
    public string Brief => Observation.Split('\n').ElementAtOrDefault(1) ?? Observation;
    [ObservableProperty] private string machineSummary = "Выборочная проверка: еще нет результата";
    [ObservableProperty] private string machine = "Выборочная проверка: еще нет результата";
    [ObservableProperty] private string technical = "";
    [ObservableProperty] private string availability = "Нет v1/capabilities";
    [ObservableProperty] private string humanAnswer = "Ответ человека отсутствует";
    [ObservableProperty] private string? answer;
    [ObservableProperty] private string method = "Наблюдение человеком во время выбранной проверки";
    [ObservableProperty] private decimal duration = 3000;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool canStart;
    public ModuleCardViewModel(string source, ModuleChecksViewModel owner) { Source = source; this.owner = owner; }
    partial void OnSelectedChanged(ModuleCheckChoice? value) {
        if (value is null) return;
        ShowResult(value.Result);
        Answer = null;
        HumanAnswer = "Ответ человека отсутствует";
    }
    public void Update(ReceiverState telemetry, CheckSessionState state) {
        TelemetryPacket? packet = telemetry.Snapshot?.Packet;
        Observation? obs = packet?.Observations.FirstOrDefault(o => o.SourceId == Source);
        Observation = obs is null ? "Текущее наблюдение v1: еще нет данных" : DescribeObservation(obs, packet!);
        TileStatus = obs is null ? "Нет данных" : Source == "pir" ? "Питание не подтверждено"
            : !telemetry.IsSerial ? "Запись · " + obs.Status : !telemetry.Running ? "Источник остановлен · " + obs.Status
            : Source is "esp32" or "oled" or "dht11" or "light" or "ir" && telemetry.Snapshot is not null
                && System.Diagnostics.Stopwatch.GetElapsedTime(telemetry.Snapshot.ReceivedTick).TotalSeconds > 10 ? "Данные устарели"
            : obs.Status == "ok" ? "Наблюдение получено" : "Наблюдение: " + obs.Status;
        OnPropertyChanged(nameof(Brief));
        SourceCapability? capability = state.Capabilities?.Sources.FirstOrDefault(s => s.Source == Source);
        CanStart = state.Ready && !state.PendingId.HasValue && state.Capabilities?.Busy == false && capability?.Initialized == true
            && capability.Operations.Contains(Operation) && (!IsRelay || state.RelayAuthorized) && !state.Capabilities.IdExhausted && !state.WorkflowBusy && (Source != "oled" || state.ProtocolVersion == 2);
        Availability = !state.Compatible ? "Команды выключены: replay/Stop или неизвестная версия прошивки"
            : Source == "oled" && state.ProtocolVersion != 2 ? "Для проверки с ожиданием ответа обновите прошивку"
            : !state.Ready ? "Сначала запросите возможности"
            : capability?.Initialized != true ? "Программный путь не инициализирован"
            : state.PendingId.HasValue || state.Capabilities!.Busy ? "Есть незавершенная проверка; запросите status"
            : state.Capabilities!.IdExhausted ? "ID исчерпаны; status/all_off доступны"
            : IsRelay && !state.RelayAuthorized ? "Нужно явное разрешение оператора для этого подключения/boot" : "Операция доступна";
        CheckResult[] results = state.History.Where(r => r.Event.Source == Source).Reverse().ToArray();
        latest = results.FirstOrDefault();
        if (Results.Count != results.Length || Results.Where((r, i) => !ReferenceEquals(r.Result, results[i])).Any()) {
            ModuleCheckChoice? held = Selected;
            Results.Clear(); foreach (CheckResult result in results) Results.Add(new ModuleCheckChoice(result));
            Selected = Pinned && held is not null ? held : Results.FirstOrDefault();
        }
        if (Selected is not null) {
            CheckEvent e = Selected.Result.Event;
            ShowResult(Selected.Result);
            if (e.Device + "/" + e.Boot != state.Identity || Selected.Result.Origin != telemetry.Origin) { Machine = "ИСТОРИЯ · " + Machine; MachineSummary = "ИСТОРИЯ · " + MachineSummary; }
            HumanCheckAnswer? human = state.Answers.LastOrDefault(a => a.Device == e.Device && a.Boot == e.Boot && a.RequestId == e.RequestId && a.Source == Source);
            HumanAnswer = human is null ? "Ответ человека отсутствует (не равен Нет)" : human.Answer + " · " + human.AnsweredAt.ToString("O") + " · " + human.Method;
        }
        if (Selected is null) Technical = "Выборочной проверки нет";
        if (obs is not null) Technical += "\nТекущее наблюдение v1: " + obs.SourceId + " / " + obs.Status
            + "; попыток " + obs.AttemptCount + "; подряд ошибок " + obs.ConsecutiveErrors + "; raw=" + obs.RawLevel
            + "\nvalues=" + (obs.Values?.GetRawText() ?? "null");
    }
    [RelayCommand] private void Start() => owner.Select(this);
    [RelayCommand] private void Freeze() { if (Selected is not null) Pinned = true; }
    [RelayCommand] private void FollowLatest() { Pinned = false; Selected = Results.FirstOrDefault(); }
    [RelayCommand] private void Status() {
        if (Selected?.Result.Event.RequestId is > 0) owner.Query(Selected.Result);
    }
    [RelayCommand] private void SaveAnswer() {
        if (Selected is null || Answer is null) return;
        try { owner.Answer(Selected.Result, Answer, Method); }
        catch (InvalidDataException exception) { HumanAnswer = exception.Message; }
    }
    private void ShowResult(CheckResult result) {
        CheckEvent e = result.Event;
        string stage = e.Stage switch { "accepted" => "принято", "running" => "выполняется", "completed" => "машинная операция завершена", "failed" => "ошибка операции", "cancelled" => "отменена", _ => "запрос отклонен" };
        MachineSummary = "Выборочный тест · ID " + e.RequestId + " · boot " + e.Boot + " · " + stage + (Pinned ? " · закреплен" : "");
        Machine = "Выборочная проверка ID " + e.RequestId + " · boot " + e.Boot + "\n" + e.Stage + " · " + e.Reason
            + (e.Replayed || e.Kind == "status" ? " · сохраненный результат, без нового исполнения" : "")
            + "\nПринятие/dispatch/завершение: " + e.Accepted + " / " + e.Started + " / " + e.Finished + " мс\n" + DescribeData(e.Data);
        Technical = "Источник: " + result.Origin + "; устройство " + e.Device + "; " + e.Firmware + "; " + e.Config
            + "\nreply_to=" + e.ReplyTo + "; target=" + e.Target + "; watermark=" + e.UpperId + "; получено " + result.ReceivedAt.ToString("O")
            + "\nОшибка: " + e.ErrorDomain + " " + e.ErrorCode + "\n" + e.Data.GetRawText();
    }
    private string DescribeData(JsonElement data) {
        string V(string key) => data.TryGetProperty(key, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? Readable(value) : "неизвестно";
        if (!data.EnumerateObject().Any()) return "Машинных величин нет; отсутствие не равно нулю";
        return Operation switch {
            "snapshot" => "RAM свободно " + Bytes(data, "free_heap_bytes") + "; минимум " + Bytes(data, "min_free_heap_bytes") + "; сброс " + V("reset_reason"),
            "read_next" => V("temperature_c") + " °C · влажность " + V("humidity_pct") + " %",
            "observe" => "Уровень " + V("initial_raw_level") + " → " + V("final_raw_level") + "; переходов " + V("transition_count")
                + "; окно " + V("window_start_ms") + "…" + V("window_end_ms") + " мс\nВыборок " + V("sample_count") + "; максимальный промежуток " + V("max_sample_gap_ms") + " мс (0 переходов не означает отказ)",
            "display_test" => "Передача: " + V("transfer_ok") + "; панель восстановлена: " + V("dashboard_restored") + "; показ/возврат " + V("shown_ms") + "/" + V("restored_ms") + " мс. Видимость — ответ человека",
            "run" => "Файловый тест: " + V("test_passed") + "; " + V("duration_ms") + " мс\nФайл создан: " + V("file_created") + "; оставлен: " + V("file_retained") + "; очистка: " + V("cleanup_ok"),
            _ => "Программный импульс/выключение; GPIO IN1=" + V("in1_level") + ", IN2=" + V("in2_level") + "; OFF callback=" + V("off_callback_ms") + "; OFF команда=" + V("off_command_ok") + ". Контакты не измеряются"
        };
    }
    private string DescribeObservation(Observation o, TelemetryPacket p) {
        string age = o.LastSuccessMs.HasValue ? (p.UptimeMs - o.LastSuccessMs.Value) + " мс на момент снимка" : "успешных данных нет";
        string old = o.Status == "ok" ? "" : "Последние успешные данные (старые): ";
        string values = o.Values.HasValue ? DescribeData(o.Values.Value) : "нет успешных величин";
        if (Source is "light" or "ir") values = "Цифровой уровень: " + Field(o.Values, "digital_level");
        if (Source == "pir") values = "Raw: " + o.RawLevel + "; питание не подтверждено";
        if (IsRelay) values = "GPIO: " + Field(o.Values, "gpio_level") + "; импульс активен: " + Field(o.Values, "pulse_active");
        if (Source == "oled") values = "Передача framebuffer: " + Field(o.Values, "transfer_ok");
        if (Source == "microsd") values = "Последний файловый тест: " + Field(o.Values, "test_passed") + "; длительность " + Field(o.Values, "duration_ms") + " мс";
        return "Наблюдение v1 · " + o.Status + " · " + age + "\n" + old + values
            + "\nПоследняя попытка/успех: " + o.LastAttemptMs + "/" + o.LastSuccessMs + " мс; ошибка: " + o.ErrorDomain + " " + o.ErrorCode + " " + o.ErrorDetail;
    }
    private static string Field(JsonElement? data, string key) => data.HasValue && data.Value.TryGetProperty(key, out JsonElement value) ? Readable(value) : "неизвестно";
    private static string Readable(JsonElement value) => value.ValueKind switch {
        JsonValueKind.True => "да", JsonValueKind.False => "нет", JsonValueKind.Null => "неизвестно", _ => value.ToString()
    };
    private static string Bytes(JsonElement data, string key) => data.TryGetProperty(key, out JsonElement value) && value.TryGetUInt32(out uint bytes)
        ? (bytes / 1024m).ToString("F1") + " КиБ (" + bytes + " байт)" : "неизвестно";
}
