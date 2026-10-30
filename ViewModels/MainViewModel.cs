using System;
using System.Collections.Immutable;
using IotExpertApp.Models.Checks;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Telemetry;

namespace IotExpertApp.ViewModels;

public sealed record ObservationRow(string Title, string State, string Values, string Evidence);

public partial class MainViewModel : ViewModelBase {
    private readonly TelemetryReceiverService receiver;
    private readonly DispatcherTimer timer;
    private ReceivedSnapshot? shownSnapshot;
    private string? shownReceiverMessage;

    public ConsultationViewModel Consultation { get; } = new ConsultationViewModel();
    public KnowledgeEditorViewModel KnowledgeEditor { get; }
    public LiveMonitorViewModel Live { get; } = new LiveMonitorViewModel();
    public VirtualStandViewModel VirtualStand { get; }
    public ModuleChecksViewModel Modules { get; }
    public OnlineDataViewModel Online { get; } = new OnlineDataViewModel();
    public ProfileKnowledgeViewModel ProfileKnowledge { get; }

    public ObservableCollection<string> Ports { get; } = new ObservableCollection<string>();
    public ObservableCollection<ObservationRow> Observations { get; } = new ObservableCollection<ObservationRow>();
    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private string replayPath = "";
    [ObservableProperty] private string capturePath = "";
    [ObservableProperty] private bool timedReplay = true;
    [ObservableProperty] private string playback = "";
    [ObservableProperty] private string capture = "запись выключена";
    [ObservableProperty] private string pauseText = "Пауза / продолжить";
    [ObservableProperty] private string status = "отключено";
    [ObservableProperty] private string identity = "нет принятого пакета";
    [ObservableProperty] private string statistics = "";
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string standIdentity = "Стенд еще не подключен";
    [ObservableProperty] private string localDiagnostic = "";

    public MainViewModel(TelemetryReceiverService receiver) {
        this.receiver = receiver;
        VirtualStand = new VirtualStandViewModel(Live);
        ProfileKnowledge = new ProfileKnowledgeViewModel(Live);
        Modules = new ModuleChecksViewModel(receiver, () => Live.Profile);
        KnowledgeEditor = new KnowledgeEditorViewModel(path => Consultation.KnowledgePath = path);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        RefreshPorts();
    }

    [RelayCommand]
    private void RefreshPorts() {
        try {
            Ports.Clear();
            foreach (string port in receiver.GetPorts()) Ports.Add(port);
            SelectedPort = PortSelectionService.Choose(SelectedPort, Ports);
        }
        catch (Exception exception) { Message = exception.Message; }
    }

    [RelayCommand]
    private async Task ConnectAsync() {
        if (string.IsNullOrWhiteSpace(SelectedPort)) { Message = "выбери последовательный порт"; return; }
        string port = SelectedPort;
        string captureFile = CapturePath;
        await Live.EnsureLoadedAsync();
        await receiver.StartSerialAsync(port, captureFile);
        ShowReceiverMessage();
    }

    [RelayCommand]
    private async Task ReadFileAsync() {
        try {
            if (!File.Exists(ReplayPath)) { Message = "файл не найден"; return; }
            string replayFile = ReplayPath;
            bool timed = TimedReplay;
            await Live.EnsureLoadedAsync();
            await receiver.StartFileAsync(replayFile, timed);
            ShowReceiverMessage();
        }
        catch (Exception exception) { Message = exception.Message; }
    }

    [RelayCommand]
    private void TogglePause() => receiver.ToggleReplayPause();

    [RelayCommand]
    private async Task StopCaptureAsync() => await receiver.StopCaptureAsync();

    [RelayCommand]
    private async Task StopAsync() => await receiver.StopAsync();

    // завершившаяся команда обновляет сообщение даже при прежнем пустом LastMessage
    // так локальная ошибка файла не остается после успешного запуска другого источника
    private void ShowReceiverMessage() {
        shownReceiverMessage = receiver.GetState().LastMessage;
        Message = shownReceiverMessage;
    }

    private void Refresh() {
        (ReceiverState state, ImmutableArray<GuidedCheckResult> checks) = receiver.GetAssessmentState();
        Live.Update(state, checks);
        VirtualStand.OfferProfile(Live.Profile);
        Modules.Update(state);
        TimeSpan packetAge = state.Snapshot is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(state.Snapshot.ReceivedTick);
        Online.Update(state, Live.Profile, packetAge);
        ProfileKnowledge.Synchronize(Live.Profile, Live.IsLoading, Live.ProfileLoadStatus, Live.LoadMessage);
        Status = state.Status + (state.IsSerial ? " | " + state.Origin : state.Origin.Length == 0 ? "" : " | Запись");
        StandIdentity = state.Snapshot is null ? "Стенд еще не подключен" : state.Snapshot.Packet.DeviceId + " · " + state.Snapshot.Packet.FirmwareVersion;
        Playback = state.Playback;
        Capture = state.Capture;
        PauseText = state.ReplayPaused ? "Продолжить" : "Пауза";
        Statistics = $"принято: {state.Valid} | логи: {state.Logs} | ошибки: {state.Invalid} | последовательность наблюдений: {state.TemporalRejected} | порядок: {state.RejectedOrder} | пропуски: {state.Missing} | длинные: {state.Oversized} | хвосты: {state.Tails}";
        // локальная ошибка команды остается видимой до следующего сообщения приемника
        if (shownReceiverMessage != state.LastMessage) {
            shownReceiverMessage = state.LastMessage;
            Message = state.LastMessage;
        }
        // таймер обновляет статус каждые 250 мс, карточки пересобираем только для нового снимка
        if (ReferenceEquals(shownSnapshot, state.Snapshot)) return;
        shownSnapshot = state.Snapshot;
        Observations.Clear();
        if (state.Snapshot is null) {
            Identity = "нет принятого пакета";
            LocalDiagnostic = "";
            return;
        }
        TelemetryPacket packet = state.Snapshot.Packet;
        Identity = $"{packet.DeviceId} | boot {packet.BootId}\nsequence {packet.Sequence} | uptime {packet.UptimeMs} мс | принято {state.Snapshot.ReceivedAt:O}\n{packet.FirmwareVersion} | {packet.ConfigVersion}";
        LocalDiagnostic = "локальная диагностика прошивки (не заключение ЭС): " + packet.LocalDiagnostic;
        foreach (Observation item in packet.Observations) {
            string age = item.LastSuccessMs.HasValue ? (packet.UptimeMs - item.LastSuccessMs.Value) + " мс на момент снимка" : "нет успешного измерения";
            string note = item.SourceId switch {
                "pir" => "питание не подтверждено; raw GPIO не является движением",
                "microsd" => "результат последнего стартового или выборочного теста, не текущая доступность карты",
                "oled" => "передача framebuffer, не проверка картинки",
                "relay_1" or "relay_2" => "GPIO/импульс, без подтверждения контактов",
                "dht11" => "температура окружающей среды, не кристалла ESP32",
                _ => "порог/системное наблюдение, не диагноз"
            };
            Observations.Add(new ObservationRow(item.SourceId + " / " + item.SourceType,
                $"{item.Status} | ошибок подряд: {item.ConsecutiveErrors} | попыток: {item.AttemptCount}",
                item.Values?.GetRawText() ?? "нет успешного измерения",
                $"{age}; последняя попытка {item.LastAttemptMs}; последний успех {item.LastSuccessMs}\n{item.ErrorDomain} {item.ErrorCode} {item.ErrorDetail}; raw={item.RawLevel}\n{note}"));
        }
    }

    public async Task ShutdownAsync() {
        timer.Stop();
        await receiver.DisposeAsync();
    }
}
