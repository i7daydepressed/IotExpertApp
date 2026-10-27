using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Telemetry;

namespace IotExpertApp.ConsoleUI;

public static class ReceiverStep02Checks {
    public static void Run() {
        CheckProgress();
        CheckCapture();
        CheckReplayAsync().GetAwaiter().GetResult();
        CheckReplayBoundaryAsync().GetAwaiter().GetResult();
        Console.WriteLine("проверки шага 02: искусственные переходы, запись байтов, replay/пауза/Stop");
    }

    private static void CheckProgress() {
        TelemetryParser parser = new TelemetryParser();
        Require(parser.TryParse(ReceiverChecks.CreateSyntheticPacket(), out TelemetryPacket? parsed, out string error), error);
        TelemetryPacket first = parsed! with { Sequence = 1 };
        ObservationProgressValidator validator = new ObservationProgressValidator();
        TelemetryPacket same = first with { Sequence = 2, UptimeMs = 300 };
        Require(validator.TryValidate(first, same, out _), "повторное сохраненное измерение должно быть допустимо");
        Observation dht = first.Observations.First(item => item.SourceId == "dht11");
        Require(!validator.TryValidate(first, Replace(same, dht with { LastAttemptMs = 90 }), out _), "откат времени");
        Require(!validator.TryValidate(first, Replace(same, dht with { AttemptCount = 0 }), out _), "откат счетчика");
        Require(!validator.TryValidate(first, Replace(same, dht with { LastAttemptMs = 200 }), out _), "изменение без попытки");
        Observation failed = dht with {
            LastAttemptMs = 200,
            AttemptCount = 2,
            ConsecutiveErrors = 1,
            Status = "error",
            ErrorCode = 263,
            ErrorDomain = "esp_err",
            ErrorDetail = "synthetic_timeout"
        };
        Require(validator.TryValidate(first, Replace(same, failed), out error), error);
        Observation impossible = failed with { LastSuccessMs = 200 };
        Require(!validator.TryValidate(first, Replace(same, impossible), out _), "одна ошибка не создает успех");
        Observation hiddenSuccess = failed with { AttemptCount = 3, LastSuccessMs = 250, LastAttemptMs = 300 };
        Require(validator.TryValidate(first, Replace(same, hiddenSuccess), out _), "между пакетами возможны успех и ошибка");
        Require(!validator.TryValidate(first, Replace(same, failed with { ConsecutiveErrors = 3 }), out _), "лишние последовательные ошибки");
        Require(!validator.TryValidate(first, Replace(same, hiddenSuccess with { ConsecutiveErrors = 2 }), out _),
            "непрерывная серия ошибок не создает промежуточный успех");
        TelemetryPacket oldFailure = Replace(first with { UptimeMs = 200 }, failed);
        Require(!validator.TryValidate(oldFailure, Replace(same, failed with { AttemptCount = 4, LastAttemptMs = 300, ConsecutiveErrors = 2 }), out _),
            "после двух новых ошибок серия не может вырасти только на одну");
        Require(!validator.TryValidate(oldFailure, Replace(same, failed with { AttemptCount = 4, LastAttemptMs = 300, ConsecutiveErrors = 1 }), out _),
            "сброс серии ошибок требует нового успеха");
        Observation recovered = dht with { AttemptCount = 3, LastAttemptMs = 300, LastSuccessMs = 300 };
        Require(validator.TryValidate(oldFailure, Replace(same, recovered), out error), error);
        using JsonDocument reordered = JsonDocument.Parse("{\"humidity_pct\":50.0,\"temperature_c\":24}");
        Require(validator.TryValidate(first, Replace(same, dht with { Values = reordered.RootElement.Clone() }), out _),
            "порядок JSON-свойств и эквивалентное число не меняют values");
        using JsonDocument changed = JsonDocument.Parse("{\"temperature_c\":25,\"humidity_pct\":50}");
        Require(!validator.TryValidate(first, Replace(same, dht with { Values = changed.RootElement.Clone() }), out _),
            "values без нового времени успеха");
        ObservationStore store = new ObservationStore();
        Require(store.TryApply(first, "synthetic", out _, out _), "начальный снимок для проверки отклонения");
        ReceivedSnapshot? accepted = store.Current;
        Require(!store.TryApply(Replace(same, hiddenSuccess with { ConsecutiveErrors = 2 }), "synthetic", out _, out _) &&
            ReferenceEquals(store.Current, accepted), "противоречивый пакет не освежает снимок");
        TelemetryPacket nearLimit = Replace(first, dht with { AttemptCount = ulong.MaxValue - 1 });
        Require(validator.TryValidate(nearLimit, Replace(same, hiddenSuccess with { AttemptCount = ulong.MaxValue }), out _),
            "при насыщении нельзя предполагать единственную попытку");
        TelemetryPacket saturatedErrors = Replace(first with { UptimeMs = 200 }, failed with { AttemptCount = (ulong)uint.MaxValue + 1, ConsecutiveErrors = uint.MaxValue });
        Require(validator.TryValidate(saturatedErrors, Replace(same, failed with { AttemptCount = (ulong)uint.MaxValue + 3, ConsecutiveErrors = uint.MaxValue, LastAttemptMs = 300 }), out _),
            "серия ошибок тоже насыщается, не переполняясь в ноль");
        Observation neverSucceeded = failed with { LastAttemptMs = 100, LastSuccessMs = null, Values = null, AttemptCount = 1 };
        Require(validator.TryValidate(Replace(first, neverSucceeded), Replace(same, neverSucceeded with { AttemptCount = (ulong)uint.MaxValue + 2, ConsecutiveErrors = uint.MaxValue, LastAttemptMs = 300 }), out _),
            "большая разность попыток с насыщенной серией ошибок не доказывает успех");
        Require(validator.TryValidate(first, Replace(same with { BootId = new string('c', 32) }, dht with { AttemptCount = 0 }), out _), "новый boot сбрасывает историю");
    }

    private static TelemetryPacket Replace(TelemetryPacket packet, Observation item) => packet with {
        Observations = Array.AsReadOnly(packet.Observations.Select(old => old.SourceId == item.SourceId ? item : old).ToArray())
    };

    private static void CheckCapture() {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("iotexpert-capture-check-");
        RawCaptureWriter writer = new RawCaptureWriter(64);
        try {
            string path = Path.Combine(directory.FullName, "raw.log");
            byte[] bytes = Encoding.UTF8.GetBytes("boot\r\n{broken}\n{tail");
            writer.Start(path);
            writer.Write(bytes, bytes.Length);
            writer.Stop();
            Require(File.ReadAllBytes(path).SequenceEqual(bytes), "запись должна сохранять исходные байты");
            writer.Start(path);
            writer.Write(new byte[] { 0 }, 1);
            Require(File.ReadAllBytes(path).SequenceEqual(bytes), "существующий файл нельзя затирать");
            Require(writer.Description.StartsWith("запись не начата", StringComparison.Ordinal), "ошибка повторного имени видима");
            string limited = Path.Combine(directory.FullName, "limited.log");
            writer.Start(limited);
            writer.Write(new byte[60], 60);
            writer.Write(new byte[10], 10);
            Require(new FileInfo(limited).Length == 60, "лимит не должен превышаться или сохранять часть блока");
            Require(writer.Description.Contains("по лимиту", StringComparison.Ordinal), "лимит виден");
            writer.Start(Path.Combine(directory.FullName, "absent", "raw.log"));
            Require(writer.Description.StartsWith("запись не начата", StringComparison.Ordinal), "неверный путь записи виден");
        }
        finally { writer.Stop(); directory.Delete(true); }
    }

    private static string PacketLine(ulong sequence, ulong uptime) {
        JsonObject root = JsonNode.Parse(ReceiverChecks.CreateSyntheticPacket())!.AsObject();
        root["sequence"] = JsonValue.Create(sequence);
        root["uptime_ms"] = JsonValue.Create(uptime);
        return root.ToJsonString();
    }

    private static async Task CheckReplayAsync() {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("iotexpert-replay-check-");
        try {
            string path = Path.Combine(directory.FullName, "synthetic-timed.log");
            await File.WriteAllTextAsync(path, PacketLine(1, 100) + "\n" + PacketLine(2, 700) + "\n");
            await using TelemetryReceiverService receiver = new TelemetryReceiverService(new ObservationStore());
            await receiver.StartFileAsync(path, true);
            await WaitAsync(() => receiver.GetState().Valid == 1);
            receiver.ToggleReplayPause();
            await Task.Delay(150);
            ReceiverState paused = receiver.GetState();
            Require(paused.ReplayPaused && paused.Valid == 1, "пауза не должна применять следующий пакет");
            string position = paused.Playback;
            await Task.Delay(100);
            Require(receiver.GetState().Playback == position, "часы записи должны замереть на паузе");
            receiver.ToggleReplayPause();
            await WaitAsync(() => !receiver.GetState().Running);
            Require(receiver.GetState().Valid == 2 && receiver.GetState().Completed, "EOF после двух пакетов");
            string eofPosition = receiver.GetState().Playback;
            await Task.Delay(80);
            Require(receiver.GetState().Playback == eofPosition, "часы не продолжаются после EOF");
            await receiver.StopAsync();
            await receiver.StartFileAsync(path, true);
            await WaitAsync(() => receiver.GetState().Valid == 1);
            receiver.ToggleReplayPause();
            Stopwatch stop = Stopwatch.StartNew();
            await receiver.StopAsync();
            Require(stop.Elapsed < TimeSpan.FromSeconds(2), "Stop должен завершить паузу");
            Require(!receiver.GetState().Running, "worker завершен");
            Require(!receiver.GetState().ReplayPaused, "остановленный replay не остается на паузе");
            await receiver.StartFileAsync(path);
            await WaitAsync(() => !receiver.GetState().Running);
            Require(receiver.GetState().Valid == 2 && receiver.GetState().TemporalRejected == 0, "повторный быстрый replay очищает состояние");
        }
        finally { directory.Delete(true); }
    }

    private static async Task CheckReplayBoundaryAsync() {
        TelemetryParser parser = new TelemetryParser();
        Require(parser.TryParse(ReceiverChecks.CreateSyntheticPacket(), out TelemetryPacket? packet, out string error), error);
        ReplayController replay = new ReplayController();
        replay.Reset(true);
        replay.WaitForPacket(packet!, CancellationToken.None);
        replay.TogglePause();
        string before = replay.GetState().Description;
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task pending = Task.Run(() => replay.WaitForPacket(packet! with { BootId = new string('e', 32), UptimeMs = ulong.MaxValue }, cancellation.Token));
        try {
            await Task.Delay(80);
            Require(!pending.IsCompleted && replay.GetState().Description == before, "пауза запрещает смену шкалы нового boot");
            replay.TogglePause();
            await pending;
            replay.Complete();
            Require(replay.GetState().Description.Contains(ulong.MaxValue.ToString(), StringComparison.Ordinal),
                "первый пакет boot сохраняет точный UInt64 uptime без ожидания");
        }
        finally {
            cancellation.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    private static async Task WaitAsync(Func<bool> predicate) {
        for (int attempt = 0; attempt < 500; attempt++) {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("тайм-аут проверки replay");
    }

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
