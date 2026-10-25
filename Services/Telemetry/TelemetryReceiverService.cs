using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Telemetry;

public sealed class TelemetryReceiverService : IAsyncDisposable
{
    private readonly object gate = new object();
    private readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);
    private readonly ObservationStore store;
    private readonly TelemetryParser parser = new TelemetryParser();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string status = "отключено";
    private string origin = "нет источника";
    private string lastMessage = "ожидание запуска";
    private bool serialMode;
    private bool running;
    private long lastPacketTick;
    private long valid, logs, invalid, rejectedOrder, missing, oversized, tails;

    public TelemetryReceiverService(ObservationStore store) => this.store = store;
    public string[] GetPorts() => SerialPort.GetPortNames();

    // под той же блокировкой собираем счетчики и снимок, чтобы GUI видел согласованное состояние
    public ReceiverState GetState()
    {
        lock (gate)
        {
            string shown = status;
            if (serialMode && running && Stopwatch.GetElapsedTime(lastPacketTick).TotalSeconds > 10)
                shown = "нет свежей телеметрии более 10 с";
            return new ReceiverState(shown, origin, valid, logs, invalid, rejectedOrder,
                missing, oversized, tails, lastMessage, store.Current);
        }
    }

    public Task StartSerialAsync(string port) => StartAsync(port, true);
    public Task StartFileAsync(string path) => StartAsync(Path.GetFullPath(path), false);

    private async Task StartAsync(string source, bool serial)
    {
        await lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
            cancellation = new CancellationTokenSource();
            lock (gate)
            {
                store.Clear();
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
            worker = Task.Run(() => Run(source, serial, token));
        }
        finally { lifecycle.Release(); }
    }

    private void Run(string source, bool serial, CancellationToken token)
    {
        TelemetryLineAssembler assembler = new TelemetryLineAssembler();
        try
        {
            using ITelemetryByteSource input = serial
                ? new SerialTelemetrySource(source)
                : new ReplayTelemetrySource(source);
            SetStatus(serial ? "порт открыт, ожидание пакета" : "чтение файла");
            byte[] buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                int count;
                try { count = input.Read(buffer); }
                catch (TimeoutException) { continue; }
                if (count == 0)
                {
                    if (serial) throw new IOException("последовательный поток закрыт");
                    break;
                }
                Process(assembler, buffer, count);
            }
            if (!serial && !token.IsCancellationRequested) SetStatus("файл прочитан (быстрый режим)");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            lock (gate)
            {
                status = "ошибка источника";
                lastMessage = exception.Message;
            }
        }
        finally
        {
            lock (gate)
            {
                if (assembler.Finish()) tails++;
                running = false;
                if (token.IsCancellationRequested && status != "ошибка источника") status = "остановлено";
            }
        }
    }

    private void Process(TelemetryLineAssembler assembler, byte[] buffer, int count)
    {
        foreach (Frame frame in assembler.Feed(buffer, count))
        {
            lock (gate)
            {
                if (frame.Oversized)
                {
                    oversized++;
                    if (frame.JsonCandidate) invalid++; else logs++;
                    lastMessage = "строка больше 8192 байт с LF, пропущена";
                }
                else if (!frame.JsonCandidate)
                {
                    if (!string.IsNullOrEmpty(frame.Text))
                    {
                        logs++;
                        lastMessage = frame.Text!.Length > 300 ? frame.Text[..300] : frame.Text;
                    }
                }
                else if (!parser.TryParse(frame.Text!, out TelemetryPacket? packet, out string error))
                {
                    invalid++;
                    lastMessage = error;
                }
                else if (!store.TryApply(packet!, origin, out ulong lost, out string message))
                {
                    rejectedOrder++;
                    lastMessage = message;
                }
                else
                {
                    valid++;
                    // счетчик интерфейса насыщается, точное значение пропуска остается в сообщении
                    missing = lost > (ulong)(long.MaxValue - missing) ? long.MaxValue : missing + (long)lost;
                    lastMessage = message;
                    lastPacketTick = Stopwatch.GetTimestamp();
                    status = "прием данных";
                }
            }
        }
    }

    private void SetStatus(string text) { lock (gate) status = text; }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { lifecycle.Release(); }
    }

    // сначала просим цикл завершиться, затем ждем using/Dispose источника и только потом допускаем новый запуск
    // у UART короткий ReadTimeout, поэтому пустой поток тоже возвращает управление для проверки отмены
    private async Task StopCoreAsync()
    {
        cancellation?.Cancel();
        if (worker is not null) await worker;
        cancellation?.Dispose();
        cancellation = null;
        worker = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
