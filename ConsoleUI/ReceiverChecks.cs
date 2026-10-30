using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json.Nodes;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Telemetry;

namespace IotExpertApp.ConsoleUI;

public static class ReceiverChecks {
    public static int Run() {
        try {
            string json = CreateSyntheticPacket();
            TelemetryParser parser = new TelemetryParser();
            Check(parser.TryParse(json, out TelemetryPacket? packet, out string error), error);
            Check(packet!.Sequence == ulong.MaxValue, "точность UInt64");
            Check(packet.Observations.Count == 9, "число источников");
            Check(packet.Observations[0].Values!.Value.GetProperty("free_heap_bytes").GetUInt64() == 1000,
                "values должны читаться после Dispose JsonDocument");

            TelemetryLineAssembler assembler = new TelemetryLineAssembler();
            List<Frame> frames = new List<Frame>();
            foreach (byte value in Encoding.UTF8.GetBytes("boot\r\n" + json + "\r\n"))
                frames.AddRange(assembler.Feed(new byte[] { value }, 1));
            Check(frames.Count == 2 && !frames[0].JsonCandidate && frames[1].JsonCandidate, "побайтовый поток/CRLF");
            Check(frames[1].Text == json, "искажение текста при сборке");
            byte[] limit = Encoding.ASCII.GetBytes("{" + new string(' ', 8190) + "\n");
            Check(!assembler.Feed(limit, limit.Length)[0].Oversized, "граница 8192");
            byte[] oversized = Encoding.ASCII.GetBytes("{" + new string(' ', 8191) + "\nboot\n");
            IReadOnlyList<Frame> afterOversized = assembler.Feed(oversized, oversized.Length);
            Check(afterOversized.Count == 2 && afterOversized[0].Oversized && afterOversized[1].Text == "boot", "восстановление после длинной строки");
            assembler.Feed(new byte[] { 123 }, 1);
            Check(assembler.Finish() && !assembler.Finish(), "учет и очистка хвоста");

            Check(!parser.TryParse(json.Replace("\"schema_version\":1", "\"schema_version\":2"), out _, out _), "неизвестная версия");
            Check(!parser.TryParse(json.Replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1"), out _, out _), "дублирующие ключи");
            Check(!parser.TryParse(json.Replace(ulong.MaxValue.ToString(), "18446744073709551616"), out _, out _), "переполнение UInt64");
            Check(!parser.TryParse("{broken}", out _, out _) && parser.TryParse(json, out _, out _), "ошибка и следующий пакет");
            Check(!parser.TryParse(json.Replace("\"schema_version\":1", "\"schema_version\":\"1\""), out _, out _), "неверный тип поля");
            Check(!parser.TryParse(json.Replace("\"uptime_ms\":100,", ""), out _, out _), "нет обязательного поля");
            Check(!parser.TryParse(json.Replace("stand-v1-pir-unverified", "unknown-profile"), out _, out _), "неизвестный профиль");
            Check(!parser.TryParse(json.Replace("\"source_id\":\"ir\"", "\"source_id\":\"light\""), out _, out _), "повтор источника");

            JsonObject badIdentity = JsonNode.Parse(json)!.AsObject();
            badIdentity["device_id"] = "esp32-001122334455\n";
            Check(!parser.TryParse(badIdentity.ToJsonString(), out _, out _), "device_id с конечным LF");
            badIdentity["device_id"] = "esp32-001122334455";
            badIdentity["boot_id"] = new string('a', 32) + "\n";
            Check(!parser.TryParse(badIdentity.ToJsonString(), out _, out _), "boot_id с конечным LF");

            JsonObject changed = JsonNode.Parse(json)!.AsObject();
            changed["uptime_ms"] = 300;
            JsonObject dht = changed["observations"]!.AsArray()[2]!.AsObject();
            dht["status"] = "error";
            dht["last_attempt_ms"] = 200;
            dht["consecutive_errors"] = 1;
            dht["attempt_count"] = 2;
            dht["error_code"] = 263;
            dht["error_domain"] = "esp_err";
            dht["error_detail"] = "synthetic_timeout";
            Check(parser.TryParse(changed.ToJsonString(), out TelemetryPacket? failedDht, out error), error);
            Check(failedDht!.Observations[2].LastSuccessMs == 100 && failedDht.Observations[2].Values.HasValue, "ошибка должна сохранить старый успех");
            dht["last_success_ms"] = 301;
            Check(!parser.TryParse(changed.ToJsonString(), out _, out _), "успех из будущего");

            ObservationStore store = new ObservationStore();
            TelemetryPacket first = packet with { Sequence = 5, UptimeMs = 100 };
            Check(store.TryApply(first, "synthetic", out _, out _), "первый sequence не обязан быть нулем");
            ReceivedSnapshot? accepted = store.Current;
            Check(!store.TryApply(first, "synthetic", out _, out _) && ReferenceEquals(store.Current, accepted),
                "дубликат не заменяет снимок");
            Check(store.TryApply(first with { Sequence = 8, UptimeMs = 200 }, "synthetic", out ulong missing, out _) && missing == 2, "пропуск sequence");
            Check(!store.TryApply(first with { Sequence = 9, UptimeMs = 50 }, "synthetic", out _, out _), "откат uptime");
            Check(store.TryApply(first with { Sequence = 0, UptimeMs = 1, BootId = new string('b', 32) }, "synthetic", out _, out _), "новый boot");
            CheckLifecycleAsync(json).GetAwaiter().GetResult();
            ReceiverStep02Checks.Run();
            KnowledgeChecks.Run();
            Lr1ConsultationChecks.Run();
            EvidenceAndVirtualChecks.Run(json);
            ConsultationPresentationChecks.Run();
            KnowledgeEditorChecks.Run();
            LiveAssessmentChecks.Run(json);
            SelectiveChecks.Run();
            GuidedCheckChecks.Run();
            OnlineKnowledgeChecks.Run(json);
            Console.WriteLine("проверки приемника, ядра, консультации, редактора и живого профиля пройдены; оборудование и нативный GUI этим self-test не проверялись");
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine("проверка не пройдена: " + exception);
            return 1;
        }
    }

    private static async Task CheckLifecycleAsync(string json) {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("iotexpert-self-test-");
        try {
            string path = Path.Combine(directory.FullName, "synthetic.log");
            await File.WriteAllTextAsync(path, "{broken}\n" + json + "\n{tail");
            await using TelemetryReceiverService receiver = new TelemetryReceiverService(new ObservationStore());
            await receiver.StartFileAsync(path);
            ReceiverState state = await WaitForFileAsync(receiver);
            Check(state.Valid == 1 && state.Invalid == 1 && state.Tails == 1, "сервис: ошибка, восстановление и хвост");
            await receiver.StopAsync();

            // повторный запуск обязан очистить счетчики, снимок и хвост предыдущего источника
            await File.WriteAllTextAsync(path, json + "\n");
            await receiver.StartFileAsync(path);
            state = await WaitForFileAsync(receiver);
            Check(state.Valid == 1 && state.Invalid == 0 && state.Tails == 0, "сервис: повторный запуск");
            await receiver.StopAsync();

            await receiver.StartFileAsync(Path.Combine(directory.FullName, "absent.log"));
            state = await WaitForFileAsync(receiver);
            Check(state.Status == "ошибка источника" && state.Valid == 0 && state.Snapshot is null,
                "сервис: ошибка открытия не сохраняет старый снимок");
        }
        finally { directory.Delete(true); }
    }

    private static async Task<ReceiverState> WaitForFileAsync(TelemetryReceiverService receiver) {
        for (int attempt = 0; attempt < 500; attempt++) {
            ReceiverState state = receiver.GetState();
            if (state.Status is "файл прочитан (быстрый режим)" or "ошибка источника") {
                // статус EOF появляется перед finally: StopAsync дожидается учета хвоста и закрытия файла
                await receiver.StopAsync();
                return receiver.GetState();
            }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("сервис не завершил чтение за 5 с");
    }

    public static string CreateSyntheticPacket() {
        JsonArray observations = new JsonArray();
        string[] ids = { "esp32", "oled", "dht11", "light", "ir", "pir", "microsd", "relay_1", "relay_2" };
        string[] types = { "system", "display", "temperature_humidity", "digital_input", "digital_input", "digital_input", "storage_test", "relay_output", "relay_output" };
        string[] values = { "{\"free_heap_bytes\":1000,\"min_free_heap_bytes\":900,\"reset_reason_code\":1,\"reset_reason\":\"POWERON\"}",
            "{\"transfer_ok\":true}", "{\"temperature_c\":24,\"humidity_pct\":50}",
            "{\"digital_level\":1}", "{\"digital_level\":0}", "null", "{\"test_passed\":true,\"duration_ms\":12}",
            "{\"gpio_level\":1,\"pulse_active\":false}", "{\"gpio_level\":1,\"pulse_active\":false}" };
        for (int index = 0; index < ids.Length; index++) {
            bool pir = ids[index] == "pir";
            JsonObject item = new JsonObject {
                ["source_id"] = ids[index],
                ["source_type"] = types[index],
                ["enabled"] = true,
                ["status"] = pir ? "unavailable" : "ok",
                ["last_attempt_ms"] = 100,
                ["last_success_ms"] = pir ? null : JsonValue.Create(100),
                ["values"] = JsonNode.Parse(values[index]),
                ["error_code"] = null,
                ["error_domain"] = null,
                ["error_detail"] = pir ? "power_not_verified" : null,
                ["consecutive_errors"] = pir ? 1 : 0,
                ["attempt_count"] = 1
            };
            if (pir) item["raw_level"] = 0;
            observations.Add(item);
        }
        return new JsonObject {
            ["schema_version"] = 1,
            ["device_id"] = "esp32-001122334455",
            ["boot_id"] = new string('a', 32),
            ["sequence"] = JsonValue.Create(ulong.MaxValue),
            ["uptime_ms"] = 100,
            ["firmware_version"] = "synthetic-check-only",
            ["config_version"] = "stand-v1-pir-unverified",
            ["local_diagnostic"] = new JsonObject { ["state"] = "NORMAL", ["rule"] = "R4", ["reason"] = "synthetic" },
            ["observations"] = observations
        }.ToJsonString();
    }

    private static void Check(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }
}
