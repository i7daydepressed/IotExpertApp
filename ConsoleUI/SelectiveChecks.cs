using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Checks;
using IotExpertApp.Services.Telemetry;
using IotExpertApp.ViewModels;

namespace IotExpertApp.ConsoleUI;

public static class SelectiveChecks {
    public static void Run() {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "CheckExamples", "real-selective.jsonl"));
        string[] events = lines.Where(l => l.StartsWith("TEST_EVENT ", StringComparison.Ordinal)).ToArray();
        Check(events.Length == 25, "число реальных событий fixture");
        List<CheckEvent> parsed = new List<CheckEvent>();
        foreach (string line in events) {
            Check(CheckProtocol.TryParse(line, out CheckEvent? e, out string error), error);
            parsed.Add(e!);
        }
        string v1 = lines.First(l => l.StartsWith("{", StringComparison.Ordinal));
        Check(new TelemetryParser().TryParse(v1, out TelemetryPacket? p, out string parserError), parserError);
        TelemetryPacket packet = p!;
        CommandCompatibility compatibility = CommandCompatibility.Load(Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "selective-checks.json"));
        JsonObject cap = JsonNode.Parse(events[0][11..])!.AsObject();
        CheckEvent completed = parsed.First(e => e.Operation == "snapshot" && e.Stage == "completed");
        CheckEvent status = parsed.First(e => e.Kind == "status");
        Check(status.RequestId == 1 && status.ReplyTo == 0 && status.Target == 1, "status имеет три разных назначения ID");
        Check(CheckProtocol.Id(ulong.MaxValue.ToString()) == ulong.MaxValue, "UInt64 MAX");
        Bad(events[0].Replace("\"protocol_version\":1", "\"protocol_version\":1,\"protocol_version\":1"));
        Bad(events[0].Replace("\"protocol_version\":1", "\"protocol_version\":2"));
        Bad(events[0].Replace("\"upper_request_id\":\"0\"", "\"upper_request_id\":\"18446744073709551616\""));
        Bad(events[0].Replace("\"upper_request_id\":\"0\"", "\"upper_request_id\":\"00\""));
        JsonObject bad = JsonNode.Parse(events.First(l => l.Contains("\"sampled_ms\"", StringComparison.Ordinal)) [11..])!.AsObject();
        bad["finished_ms"] = 1; Bad("TEST_EVENT " + bad.ToJsonString());
        bad = JsonNode.Parse(events[0][11..])!.AsObject(); bad["data"]!["busy"] = "false"; Bad("TEST_EVENT " + bad.ToJsonString());
        bad = JsonNode.Parse(events[0][11..])!.AsObject(); bad["data"]!["sources"]![0]!["initialized"] = 0; Bad("TEST_EVENT " + bad.ToJsonString());
        JsonObject maximumTime = JsonNode.Parse(events.First(l => l.Contains("\"sampled_ms\"", StringComparison.Ordinal))[11..])!.AsObject();
        maximumTime["request_id"] = ulong.MaxValue.ToString(); maximumTime["reply_to_request_id"] = ulong.MaxValue.ToString(); maximumTime["upper_request_id"] = ulong.MaxValue.ToString();
        maximumTime["accepted_ms"] = ulong.MaxValue - 1; maximumTime["started_ms"] = ulong.MaxValue - 1;
        maximumTime["finished_ms"] = ulong.MaxValue; maximumTime["data"]!["sampled_ms"] = ulong.MaxValue;
        Check(CheckProtocol.TryParse("TEST_EVENT " + maximumTime.ToJsonString(), out CheckEvent? exact, out parserError)
            && exact!.Finished == ulong.MaxValue, "времена UInt64 MAX без double");
        Bad("TEST_EVENT {broken}");

        SelectiveCheckSession session = new SelectiveCheckSession(); session.Configure(compatibility); session.Begin(true);
        Check(!session.Enqueue("esp32", "capabilities", null, null, out _), "до v1 запрещено даже capabilities");
        session.Observe(packet with { FirmwareVersion = "lr3-1-old" });
        Check(!session.Enqueue("esp32", "all_off", null, null, out _), "старая прошивка: никаких байтов");
        session.Observe(packet);
        Check(!session.Enqueue("esp32", "snapshot", null, null, out _), "обычная операция требует capabilities");
        Check(session.Enqueue("esp32", "capabilities", null, null, out _) && session.TryTake(out CheckCommand? capability), "read-only после совместимой v1");
        Check(session.Accept(parsed[0], "serial: fake", out _), "capabilities identity");
        CheckCommand? first = null;
        Check(session.Enqueue("esp32", "snapshot", null, null, out _) && session.TryTake(out first) && first!.Request.Id == 1, "обычный ID1");
        Check(!session.Enqueue("esp32", "snapshot", null, null, out _), "одновременно одна проверка");
        session.Sent(first!, "synthetic partial write");
        Check(session.State().PendingId == 1 && session.State().Message.Contains("неопределенная", StringComparison.Ordinal), "неопределенная отправка не равна неисполнению");
        Check(session.Enqueue("esp32", "status", null, 1, out _) && session.TryTake(out CheckCommand? query) && query!.Request.Id == 0, "потеря ответа -> status исходного ID");
        Check(session.Accept(status, "serial: fake", out _), "восстановление terminal status");
        CheckResult pinned = session.State().History.Single();
        Check(session.Accept(completed with { Replayed = true }, "serial: fake", out _) && ReferenceEquals(pinned, session.State().History.Single()), "повтор не освежает исторический результат");
        Check(!session.Accept(completed with { Stage = "running", Finished = null }, "serial: fake", out _), "terminal не откатывается");
        Check(!session.Accept(completed with { Boot = new string('b', 32) }, "serial: fake", out _), "чужой boot события");
        Check(!session.Accept(completed with { Device = "esp32-000000000000" }, "serial: fake", out _), "чужая identity");
        Check(!session.Enqueue("relay_1", "pulse", null, null, out _), "pulse требует отдельную готовность");
        session.AuthorizeRelay(true);
        Check(session.Enqueue("relay_1", "pulse", null, null, out _), "разрешенный fake pulse только в очереди");
        session.Invalidate(); Check(!session.TryTake(out _), "Stop удаляет очередь");
        session.Begin(true); session.Observe(packet); Check(!session.State().RelayAuthorized && !session.State().Ready, "reconnect сбрасывает разрешения/capabilities");
        session.Accept(parsed[0], "serial: fake", out _);
        Check(session.State().PendingId is null && session.Enqueue("esp32", "snapshot", null, null, out _) && session.TryTake(out CheckCommand? afterReconnect) && afterReconnect!.Request.Id == 3, "неотправленный ID2 снят, reconnect использует новый ID3");
        session.Observe(packet with { BootId = new string('c', 32) }); Check(!session.State().RelayAuthorized && !session.TryTake(out _), "новый boot сбрасывает очередь/разрешения");
        session.Begin(false); session.Observe(packet); session.Accept(parsed[0], "файл: fake", out _);
        Check(!session.Enqueue("esp32", "capabilities", null, null, out _) && !session.Enqueue("esp32", "all_off", null, null, out _), "replay: никаких отправок");

        SelectiveCheckSession max = new SelectiveCheckSession(); max.Configure(compatibility); max.Begin(true); max.Observe(packet);
        cap["upper_request_id"] = ulong.MaxValue.ToString(); cap["data"]!["id_exhausted"] = true;
        Check(CheckProtocol.TryParse("TEST_EVENT " + cap.ToJsonString(), out CheckEvent? exhausted, out parserError), parserError);
        max.Accept(exhausted!, "serial: fake", out _);
        Check(!max.Enqueue("esp32", "snapshot", null, null, out _) && max.Enqueue("esp32", "all_off", null, null, out _), "MAX не переполняется; all_off ID0 доступен");
        for (int i = 0; i < 8; i++) Check(max.Enqueue("esp32", "capabilities", null, null, out _), "заполнение read-only очереди");
        Check(max.Enqueue("esp32", "all_off", null, null, out _) && max.TryTake(out CheckCommand? urgent)
            && urgent!.Request.Operation == "all_off", "явный all_off доступен и при полной очереди, не требует разрешения pulse");
        byte[] encoded = CheckProtocol.Encode(new CheckRequest(ulong.MaxValue, packet.BootId, "relay_1", "pulse"));
        Check(encoded.Length <= 512 && encoded[^1] == 10 && encoded.All(b => b < 128) && !Encoding.ASCII.GetString(encoded).Contains("duration_ms", StringComparison.Ordinal), "плоский ASCII/LF без длительности pulse");
        CheckEvent pir = parsed.First(e => e.Source == "pir" && e.Stage == "completed");
        max.Accept(pir, "serial: fake", out _);
        CheckResult pirResult = max.State().History.Single();
        max.Answer(pirResult, "Да", "synthetic human answer, не физическое наблюдение");
        Check(max.State().Answers.Single().RequestId == pir.RequestId && packet.Observations.Single(o => o.SourceId == "pir").Status == "unavailable", "человек не меняет v1 PIR");
        CheckAllOff(packet, compatibility, parsed[0], completed);
        IntegrationAsync(v1, events[0], events, packet).GetAwaiter().GetResult();
        Console.WriteLine("выборочные проверки: 25 реальных TEST_EVENT, границы parser/ID/status, fake UART/Stop/reconnect/boot, replay без отправок, история и отдельный ответ человека пройдены; физических импульсов не было");
    }

    private static void CheckAllOff(TelemetryPacket packet, CommandCompatibility compatibility, CheckEvent capabilities, CheckEvent snapshot) {
        SelectiveCheckSession Ready() {
            SelectiveCheckSession session = new SelectiveCheckSession();
            session.Configure(compatibility); session.Begin(true); session.Observe(packet);
            Check(session.Accept(capabilities, "serial: fake", out _), "all_off fixture capabilities");
            session.AuthorizeRelay(true);
            return session;
        }
        List<CheckCommand> Drain(SelectiveCheckSession session, FakeSource transport) {
            List<CheckCommand> sent = new List<CheckCommand>();
            while (session.TryTake(out CheckCommand? command)) {
                CheckCommand next = command!;
                try { transport.Write(CheckProtocol.Encode(next.Request)); session.Sent(next, null); }
                catch (TimeoutException exception) { session.Sent(next, exception.Message); }
                sent.Add(next);
            }
            return sent;
        }

        SelectiveCheckSession queued = Ready();
        using FakeSource fake = new FakeSource();
        Check(queued.Enqueue("relay_1", "pulse", null, null, out _), "pulse поставлен до обработки очереди");
        Check(queued.Enqueue("esp32", "all_off", null, null, out _), "явный all_off после queued pulse");
        Check(queued.State().PendingId is null && !queued.State().RelayAuthorized, "all_off снимает queued pulse и разрешение");
        Check(Drain(queued, fake).Single().Request.Operation == "all_off" && fake.Writes.Count == 1,
            "fake получил только all_off, отмененный pulse не отправлен");
        Check(!queued.Enqueue("relay_2", "pulse", null, null, out _), "следующий pulse требует нового разрешения");
        queued.AuthorizeRelay(true);
        Check(Drain(queued, fake).Count == 0 && fake.Writes.Count == 1, "одно разрешение не восстанавливает снятый pulse");
        Check(queued.Enqueue("relay_2", "pulse", null, null, out _), "отдельный клик нового pulse после разрешения");
        CheckCommand nextPulse = Drain(queued, fake).Single();
        Check(nextPulse.Request.Operation == "pulse" && nextPulse.Request.Id == 2 && fake.Writes.Count == 2,
            "снятый ID1 остается зарезервированным, новый клик использует ID2");

        // обычная запись и исключение после Write одинаково требуют подтверждения исходного pulse
        foreach (bool uncertain in new[] { false, true }) {
            SelectiveCheckSession sent = Ready();
            using FakeSource transport = new FakeSource { FailWrite = uncertain };
            Check(sent.Enqueue("relay_1", "pulse", null, null, out _), "подготовлен отправляемый fake pulse");
            Check(Drain(sent, transport).Single().Request.Id == 1 && sent.State().PendingId == 1, "Write сохраняет pending ID1");
            transport.FailWrite = false;
            Check(sent.Enqueue("esp32", "all_off", null, null, out _), "all_off при отправленном или неопределенном pulse");
            Check(Drain(sent, transport).Single().Request.Operation == "all_off"
                && sent.State().PendingId == 1 && !sent.State().RelayAuthorized, "all_off не снимает отправленный pending");
            using JsonDocument offData = JsonDocument.Parse("{\"in1_level\":1,\"in2_level\":1,\"off_command_ok\":true,\"stopped_channel\":0,\"gpio\":null,\"requested_pulse_ms\":null,\"pulse_started_ms\":null,\"off_command_ms\":2638}");
            CheckEvent control = capabilities with { Kind = "control", Operation = "all_off", Reason = "all_off", Capabilities = null, Data = offData.RootElement.Clone() };
            Check(sent.Accept(control, "serial: fake", out _) && sent.State().PendingId == 1, "ответ ID0 all_off не заменяет terminal pulse ID1");
            using JsonDocument empty = JsonDocument.Parse("{}");
            CheckEvent running = snapshot with { Kind = "status", ReplyTo = 0, Target = 1, Source = "relay_1", Operation = "pulse",
                Stage = "running", Reason = "running", Replayed = true, Finished = null, HumanConfirmation = "relay_click", Data = empty.RootElement.Clone() };
            Check(sent.Accept(running, "serial: fake", out _) && sent.State().PendingId == 1, "status running не завершает ожидание pulse");
            Check(sent.Accept(running with { Stage = "failed", Reason = "synthetic_return_off_failed", Finished = snapshot.Finished }, "serial: fake", out _)
                && sent.State().PendingId is null && !sent.State().RelayAuthorized, "terminal status освобождает pending без возврата разрешения");
        }
        Console.WriteLine("all_off: queued pulse снят без отправки и повторного использования ID; новое разрешение требует отдельного клика; отправленный/неопределенный pending сохранен до terminal status; только fake-транспорт");
    }

    private static async Task IntegrationAsync(string v1, string capabilities, string[] events, TelemetryPacket packet) {
        List<FakeSource> opened = new List<FakeSource>();
        await using TelemetryReceiverService receiver = new TelemetryReceiverService(new ObservationStore(), (_, _) => {
            FakeSource fake = new FakeSource(); opened.Add(fake); return fake;
        });
        await receiver.StartSerialAsync("fake-only");
        FakeSource source = opened[0];
        source.Feed(v1.Replace(packet.FirmwareVersion, "old-firmware") + "\n");
        await Wait(() => receiver.GetState().Valid == 1);
        Check(!receiver.QueueCheck("esp32", "capabilities", null, null, out _) && source.Writes.Count == 0, "receiver не пишет старой прошивке");
        await receiver.StopAsync(); await receiver.StartSerialAsync("fake-only"); source = opened[1];
        // один смешанный поток дробим до отдельных байтов; поврежденное событие не заменяет снимок
        source.Feed("boot log\r\n" + v1 + "\r\nTEST_EVENT {broken}\r\n" + capabilities + "\r\n", true);
        await Wait(() => receiver.GetState().TestEvents == 1);
        Check(receiver.GetState().Valid == 1 && receiver.GetState().TestErrors == 1, "v1/event/log и восстановление после повреждения");
        source.FailWrite = true;
        Check(receiver.QueueCheck("esp32", "snapshot", null, null, out _), "fake запрос");
        await Wait(() => source.Writes.Count == 1);
        Check(receiver.GetChecks().PendingId == 1 && receiver.GetChecks().Message.Contains("неопределенная", StringComparison.Ordinal), "частичная запись fake сохраняет ожидающий ID");
        Check(source.Writes.Single().StartsWith("TEST_REQUEST ", StringComparison.Ordinal), "один владелец реально записал framed запрос");
        source.Feed(string.Join("\n", events.Skip(1)) + "\n");
        await Wait(() => receiver.GetState().TestEvents == 25);
        Check(receiver.GetChecks().History.Count(r => r.Event.Stage == "completed" && r.Event.RequestId > 0) == 7, "семь реальных terminal без новых исполнений status/replay");
        ModuleChecksViewModel modules = new ModuleChecksViewModel(receiver); modules.Update(receiver.GetState());
        Check(modules.Cards.Count == 9 && modules.Cards.Select(c => c.Source).Distinct().Count() == 9, "девять разных источников");
        ModuleCardViewModel card = modules.Cards.Single(c => c.Source == "pir"); card.FreezeCommand.Execute(null);
        ModuleCheckChoice? held = card.Selected;
        await using MemoryStream export = new MemoryStream(); await modules.ExportAsync(export);
        Check(JsonDocument.Parse(export.ToArray()).RootElement.GetProperty("results").GetArrayLength() == 7, "экспорт ограниченного протокола отдельно от v1");
        await receiver.StopAsync(); modules.Update(receiver.GetState());
        Check(ReferenceEquals(held, card.Selected) && opened.All(f => f.Disposed), "выбранная проверка сохраняется, транспорт закрыт");
        await receiver.StartFileAsync("fake-replay"); source = opened[^1]; source.Feed(v1 + "\n" + capabilities + "\n");
        await Wait(() => receiver.GetState().TestEvents == 1);
        Check(!receiver.QueueCheck("esp32", "capabilities", null, null, out _) && source.Writes.Count == 0, "на replay нет записи даже у fake с Write");
        await receiver.StopAsync();
    }
    private sealed class FakeSource : ITelemetryCommandSource {
        private readonly BlockingCollection<byte[]> incoming = new BlockingCollection<byte[]>(1024);
        public ConcurrentQueue<string> Writes { get; } = new ConcurrentQueue<string>();
        public bool Disposed { get; private set; }
        public bool FailWrite { get; set; }
        public void Feed(string text, bool fragmented = false) {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (fragmented) foreach (byte b in bytes) incoming.Add(new[] { b }); else {
                for (int offset = 0; offset < bytes.Length; offset += 4096) incoming.Add(bytes.Skip(offset).Take(4096).ToArray());
            }
        }
        public int Read(byte[] buffer) {
            if (!incoming.TryTake(out byte[]? bytes, 10)) throw new TimeoutException();
            bytes.CopyTo(buffer, 0); return bytes.Length;
        }
        public void Write(byte[] bytes) {
            Writes.Enqueue(Encoding.ASCII.GetString(bytes));
            if (FailWrite) throw new TimeoutException("synthetic partial write");
        }
        public void Dispose() { Disposed = true; incoming.Dispose(); }
    }
    private static async Task Wait(Func<bool> condition) {
        for (int i = 0; i < 300; i++) { if (condition()) return; await Task.Delay(10); }
        throw new InvalidOperationException("ожидание fake-потока превысило 3 секунды");
    }
    private static void Bad(string text) => Check(!CheckProtocol.TryParse(text, out _, out _), "parser должен отклонить повреждение");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
