using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Models.Checks;
using IotExpertApp.Services.Checks;

namespace IotExpertApp.Services.Telemetry;

public sealed class TelemetryReceiverService : IAsyncDisposable {
    private readonly SelectiveCheckSession checks = new SelectiveCheckSession();
    private readonly GuidedCheckCoordinator guided;
    private readonly Func<string, bool, ITelemetryByteSource> openSource;
    private long testEvents, testErrors;
    private readonly object gate = new object();
    private readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);
    private readonly ObservationStore store;
    private readonly TelemetryParser parser = new TelemetryParser();
    private readonly RawCaptureWriter capture = new RawCaptureWriter();
    private readonly ReplayController replay = new ReplayController();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string status = "отключено";
    private string origin = "нет источника";
    private string lastMessage = "ожидание запуска";
    private bool serialMode;
    private bool running;
    private bool completed;
    private long temporalRejected;
    private long lastPacketTick;
    private long valid, logs, invalid, rejectedOrder, missing, oversized, tails;

    public TelemetryReceiverService(ObservationStore store, Func<string, bool, ITelemetryByteSource>? sourceFactory = null, Func<TimeSpan>? clock = null) {
        this.store = store;
        guided = new GuidedCheckCoordinator(checks, clock);
        openSource = sourceFactory ?? ((source, serial) => serial ? new SerialTelemetrySource(source) : new ReplayTelemetrySource(source));
    }
    public CheckSessionState GetChecks() { lock (gate) return checks.State(); }
    public (GuidedCheckResult? Current, System.Collections.Immutable.ImmutableArray<GuidedCheckResult> History, string Message, TimeSpan Now) GetGuided() {
        lock (gate) { guided.Tick(GetState()); return (guided.Current, guided.History, guided.Message, guided.Now); }
    }
    public bool StartGuided(string source, int seconds, uint window, IotExpertApp.Models.Knowledge.KnowledgeBase? knowledge) {
        lock (gate) return guided.Start(source, seconds, window, knowledge, GetState());
    }
    public bool AnswerGuided(string answer) { lock (gate) return guided.Answer(answer); }
    public void RecoverGuided() { lock (gate) guided.Recover(GetState()); }
    public void RetryGuidedFinish() { lock (gate) guided.RetryFinish(); }

    public bool QueueCheck(string source, string operation, uint? duration, ulong? target, out string error) {
        lock (gate) return checks.Enqueue(source, operation, duration, target, out error);
    }
    public void AuthorizeRelay(bool ready) { lock (gate) checks.AuthorizeRelay(ready); }
    public void AnswerCheck(CheckResult result, string answer, string method) { lock (gate) checks.Answer(result, answer, method); }
    public string[] GetPorts() => SerialPort.GetPortNames();

    // под той же блокировкой собираем счетчики и снимок, чтобы GUI видел согласованное состояние
    public ReceiverState GetState() {
        lock (gate) {
            string shown = status;
            if (serialMode && running && Stopwatch.GetElapsedTime(lastPacketTick).TotalSeconds > 10)
                shown = "нет свежей телеметрии более 10 с";
            (bool paused, string description) = replay.GetState();
            if (!serialMode && running && paused) shown = "воспроизведение на паузе";
            return new ReceiverState(shown, origin, valid, logs, invalid, rejectedOrder,
                missing, oversized, tails, lastMessage, store.Current, temporalRejected,
                running, completed, !serialMode && paused, serialMode ? "живой поток" : description,
                capture.Description) { IsSerial = serialMode, TestEvents = testEvents, TestErrors = testErrors };
        }
    }

    public Task StartSerialAsync(string port, string? capturePath = null) => StartAsync(port, true, false, capturePath);
    public Task StartFileAsync(string path, bool timed = false) => StartAsync(Path.GetFullPath(path), false, timed, null);
    public void ToggleReplayPause() {
        lock (gate) { if (!serialMode && running) replay.TogglePause(); }
    }
    public async Task StopCaptureAsync() {
        await lifecycle.WaitAsync();
        try { await Task.Run(() => capture.Stop()); }
        finally { lifecycle.Release(); }
    }

    private async Task StartAsync(string source, bool serial, bool timed, string? capturePath) {
        await lifecycle.WaitAsync();
        try {
            await EndGuidedAsync();
            await StopCoreAsync();
            cancellation = new CancellationTokenSource();
            lock (gate) {
                store.Clear();
                checks.Begin(serial);
                testEvents = testErrors = 0;
                replay.Reset(timed);
                completed = false;
                temporalRejected = 0;
                valid = logs = invalid = rejectedOrder = missing = oversized = tails = 0;
                origin = (serial ? "serial: " : "файл: ") + source;
                status = "подключение";
                lastMessage = "новый источник";
                serialMode = serial;
                running = true;
                lastPacketTick = Stopwatch.GetTimestamp();
            }
            CancellationToken token = cancellation.Token;
            // блокирующее чтение UART с коротким тайм-аутом работает вне потока GUI
            TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            worker = Task.Run(() => Run(source, serial, timed, capturePath, token, started));
            // lifecycle не допускает StopCapture раньше открытия источника и инициализации записи
            await started.Task;
        }
        finally { lifecycle.Release(); }
    }

    private void Run(string source, bool serial, bool timed, string? capturePath, CancellationToken token, TaskCompletionSource started) {
        TelemetryLineAssembler assembler = new TelemetryLineAssembler();
        try {
            CommandCompatibility? compatibility = null;
            try { compatibility = CommandCompatibility.Load(Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "selective-checks.json")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or FormatException or System.Collections.Generic.KeyNotFoundException) {
                lock (gate) lastMessage = "Команды отключены: " + exception.Message;
            }
            lock (gate) checks.Configure(compatibility);
            using ITelemetryByteSource input = openSource(source, serial);
            capture.Start(serial ? capturePath ?? "" : "");
            SetStatus(serial ? "порт открыт, ожидание пакета" : "чтение файла");
            started.TrySetResult();
            byte[] buffer = new byte[4096];
            while (!token.IsCancellationRequested) {
                // очередь обслуживает тот же worker: между последней проверкой поколения и Write нет смены boot/Stop
                lock (gate) {
                    guided.Tick(GetState());
                    while (!token.IsCancellationRequested && checks.TryTake(out CheckCommand? command)) {
                        try {
                            if (input is not ITelemetryCommandSource output) throw new IOException("источник не поддерживает запись");
                            output.Write(CheckProtocol.Encode(command!.Request));
                            checks.Sent(command, null);
                        }
                        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException) {
                            checks.Sent(command!, exception.Message);
                        }
                    }
                }
                int count;
                try { count = input.Read(buffer); }
                catch (TimeoutException) { continue; }
                if (count == 0) {
                    if (serial) throw new IOException("последовательный поток закрыт");
                    break;
                }
                // сохраняем полученные байты до сборки строк и проверки, включая поврежденные пакеты
                if (serial) capture.Write(buffer, count);
                Process(assembler, buffer, count, serial, token);
            }
            if (!serial && !token.IsCancellationRequested) {
                lock (gate) {
                    status = timed ? "файл воспроизведен" : "файл прочитан (быстрый режим)";
                    completed = true;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException) {
            lock (gate) {
                status = "ошибка источника";
                lastMessage = exception.Message;
            }
        }
        finally {
            capture.Stop();
            replay.Complete();
            lock (gate) {
                if (assembler.Finish()) tails++;
                running = false;
                checks.Invalidate();
                if (token.IsCancellationRequested && status != "ошибка источника") status = "остановлено";
            }
            // при ошибке открытия тоже освобождаем ожидающий StartAsync и очередь lifecycle
            started.TrySetResult();
        }
    }

    private void Process(TelemetryLineAssembler assembler, byte[] buffer, int count, bool serial, CancellationToken token) {
        foreach (Frame frame in assembler.Feed(buffer, count)) {
            token.ThrowIfCancellationRequested();
            if (!serial) replay.WaitIfPaused(token);
            if (frame.Oversized) {
                lock (gate) {
                    oversized++;
                    if (frame.TestCandidate) testErrors++; else if (frame.JsonCandidate) invalid++; else logs++;
                    lastMessage = "строка больше 8192 байт с LF, пропущена";
                }
                continue;
            }
            if (frame.Text?.StartsWith("TEST_EVENT", StringComparison.Ordinal) == true) {
                while (true) {
                    if (!serial) replay.WaitIfPaused(token);
                    token.ThrowIfCancellationRequested();
                    lock (gate) {
                        if (!serial && replay.GetState().Paused) continue;
                        if (CheckProtocol.TryParse(frame.Text, out CheckEvent? checkEvent, out string eventError)
                            && checks.Accept(checkEvent!, origin, out eventError)) { testEvents++; guided.Event(checkEvent!); }
                        else { testErrors++; lastMessage = eventError; }
                        break;
                    }
                }
                continue;
            }
            if (!frame.JsonCandidate) {
                if (!string.IsNullOrEmpty(frame.Text))
                    lock (gate) {
                        logs++;
                        lastMessage = frame.Text!.Length > 300 ? frame.Text[..300] : frame.Text;
                    }
                continue;
            }
            if (!parser.TryParse(frame.Text!, out TelemetryPacket? packet, out string error)) {
                lock (gate) { invalid++; lastMessage = error; }
                continue;
            }
            lock (gate) {
                if (!store.CanApply(packet!, out _, out string reason, out bool temporal)) {
                    if (temporal) temporalRejected++; else rejectedOrder++;
                    lastMessage = reason;
                    continue;
                }
            }
            // ожидание replay не держит блокировку снимка: GUI и Stop остаются доступны
            if (!serial) replay.WaitForPacket(packet!, token);
            while (true) {
                if (!serial) replay.WaitIfPaused(token);
                token.ThrowIfCancellationRequested();
                lock (gate) {
                    // через ToggleReplayPause берем gate: после принятой паузы новый снимок не применяется
                    if (!serial && replay.GetState().Paused) continue;
                    if (!store.TryApply(packet!, origin, out ulong lost, out string message)) {
                        temporalRejected++;
                        lastMessage = message;
                        break;
                    }
                    checks.Observe(packet!);
                    guided.Tick(GetState());
                    valid++;
                    // счетчик интерфейса насыщается, точное значение пропуска остается в сообщении
                    missing = lost > (ulong)(long.MaxValue - missing) ? long.MaxValue : missing + (long)lost;
                    lastMessage = message;
                    lastPacketTick = Stopwatch.GetTimestamp();
                    status = "прием данных";
                    break;
                }
            }
        }
    }

    private void SetStatus(string text) { lock (gate) status = text; }

    public async Task StopAsync() {
        await lifecycle.WaitAsync();
        try { await EndGuidedAsync(); await StopCoreAsync(); }
        finally { lifecycle.Release(); }
    }

    private async Task EndGuidedAsync() {
        bool wait;
        lock (gate) {
            guided.PrepareStop();
            wait = running && serialMode && guided.Current?.Active == true && guided.Current.Source == "oled";
        }
        long start = Stopwatch.GetTimestamp();
        // прежний worker получает короткое время на адресный finish; второй порт для уборки не открываем
        while (wait && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 600) {
            await Task.Delay(20);
            lock (gate) wait = running && guided.Current?.Active == true;
        }
        lock (gate) guided.Interrupt();
    }

    // сначала просим цикл завершиться, затем ждем using/Dispose источника и только потом допускаем новый запуск
    // у UART короткий ReadTimeout, поэтому пустой поток тоже возвращает управление для проверки отмены
    private async Task StopCoreAsync() {
        lock (gate) { checks.Invalidate(); cancellation?.Cancel(); }
        if (worker is not null) await worker;
        cancellation?.Dispose();
        cancellation = null;
        worker = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
