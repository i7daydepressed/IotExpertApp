using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IotExpertApp.Models.Checks;

namespace IotExpertApp.Services.Checks;

public static class CheckProtocol {
    public static readonly string[] Sources = { "esp32", "oled", "dht11", "light", "ir", "pir", "microsd", "relay_1", "relay_2" };
    public static string Operation(string source, uint protocol = 1) => source switch {
        "esp32" => "snapshot", "oled" => protocol == 2 ? "display_hold" : "display_test", "dht11" => "read_next",
        "light" or "ir" or "pir" => "observe", "microsd" => "run", "relay_1" or "relay_2" => "pulse", _ => ""
    };
    public static string? Human(string? source) => source switch {
        "oled" => "oled_image", "light" or "ir" or "pir" => "input_response", "relay_1" or "relay_2" => "relay_click", _ => null
    };
    public static ulong Id(string text) {
        Require(Regex.IsMatch(text, @"\A(?:0|[1-9][0-9]{0,19})\z") && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _), "неверный UInt64 ID");
        return ulong.Parse(text, CultureInfo.InvariantCulture);
    }
    public static byte[] Encode(CheckRequest request) {
        Require(Regex.IsMatch(request.Boot, @"\A[0-9a-f]{32}\z"), "неверный boot");
        Require(request.ProtocolVersion is 1 or 2, "версия запроса");
        bool finish = request.ProtocolVersion == 2 && request.Operation == "finish_display";
        bool service = request.Operation is "capabilities" or "status" or "all_off" || finish;
        Require(service ? request.Id == 0 && (finish ? request.Source == "oled" : request.Source == "esp32") : request.Id > 0 && Operation(request.Source, request.ProtocolVersion) == request.Operation && Sources.Contains(request.Source), "сочетание source/operation/id");
        Require(request.Operation == "observe" ? request.Duration is >= 100 and <= 10000 : request.Duration is null, "duration только у observe, 100..10000");
        Require(request.Operation == "status" || finish ? request.Target is > 0 : request.Target is null, "target только у status");
        Require(request.Operation == "display_hold" ? request.ProtocolVersion == 2 && request.MaxHold is >= 15000 and <= 310000 : request.MaxHold is null, "max_hold_ms только у display_hold, 15000..310000");
        string text = "TEST_REQUEST {\"protocol_version\":" + request.ProtocolVersion + ",\"request_id\":\"" + request.Id.ToString(CultureInfo.InvariantCulture)
            + "\",\"expected_boot_id\":\"" + request.Boot + "\",\"source_id\":\"" + request.Source + "\",\"operation\":\"" + request.Operation + "\"";
        if (request.MaxHold.HasValue) text += ",\"max_hold_ms\":" + request.MaxHold.Value.ToString(CultureInfo.InvariantCulture);
        if (request.Duration.HasValue) text += ",\"duration_ms\":" + request.Duration.Value.ToString(CultureInfo.InvariantCulture);
        if (request.Target.HasValue) text += ",\"target_request_id\":\"" + request.Target.Value.ToString(CultureInfo.InvariantCulture) + "\"";
        byte[] bytes = Encoding.ASCII.GetBytes(text + "}\n");
        Require(bytes.Length <= 512, "запрос длиннее 512 байт");
        return bytes;
    }
    public static bool TryParse(string line, out CheckEvent? result, out string error) {
        result = null;
        try {
            Require(line.StartsWith("TEST_EVENT ", StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(line) + 2 <= 8192, "префикс/размер TEST_EVENT");
            using JsonDocument document = JsonDocument.Parse(line[11..], new JsonDocumentOptions { MaxDepth = 10 });
            JsonElement r = document.RootElement;
            Unique(r);
            Shape(r, "protocol_version kind device_id boot_id firmware_version config_version request_id reply_to_request_id target_request_id source_id operation stage reason replayed upper_request_id accepted_ms started_ms finished_ms error_code error_domain human_confirmation data");
            uint protocol = checked((uint)Unsigned(r, "protocol_version"));
            Require(protocol is 1 or 2, "неподдержанная версия TEST_EVENT");
            string device = Text(r, "device_id"), boot = Text(r, "boot_id");
            Require(Regex.IsMatch(device, @"\Aesp32-[0-9a-fA-F]{12}\z") && Regex.IsMatch(boot, @"\A[0-9a-f]{32}\z"), "идентичность TEST_EVENT");
            string kind = Text(r, "kind"), stage = Text(r, "stage");
            Require(new[] { "accepted", "running", "completed", "failed", "cancelled", "rejected" }.Contains(stage), "неверная стадия");
            ulong? id = NullableId(r, "request_id"), reply = NullableId(r, "reply_to_request_id"), target = NullableId(r, "target_request_id");
            ulong upper = Id(Text(r, "upper_request_id"));
            string? source = NullableText(r, "source_id"), operation = NullableText(r, "operation");
            bool replayed = Boolean(r, "replayed");
            ulong? accepted = NullableUnsigned(r, "accepted_ms"), started = NullableUnsigned(r, "started_ms"), finished = NullableUnsigned(r, "finished_ms");
            int? code = r.GetProperty("error_code").ValueKind == JsonValueKind.Null ? null : Signed(r, "error_code");
            string? domain = NullableText(r, "error_domain"), human = NullableText(r, "human_confirmation");
            Require(code is null ? domain is null : code != 0 && domain is "esp_err" or "errno", "код/домен ошибки");
            Require(domain != "errno" || code > 0, "errno должен быть положительным");
            Require(source is null || Sources.Contains(source), "неизвестный source");
            Require(human == (operation == "finish_display" ? null : Human(source)), "неверный вопрос человеку");
            bool historical = kind is "lifecycle" or "status" || protocol == 2 && kind is "ready" or "finish";
            if (historical) {
                Require(id > 0 && id <= upper && source is not null && operation == Operation(source, protocol), "историческая операция/id/watermark");
                Require(kind is "status" or "finish" ? reply == 0 && target == id && replayed : reply == id && target is null, "status/reply/target");
                if (kind == "ready") Require(source == "oled" && operation == "display_hold" && stage == "running" && Text(r, "reason") == "ready_for_observation", "ready envelope");
                if (kind == "finish") Require(source == "oled" && operation == "display_hold" && stage is "completed" or "failed", "finish envelope");
                Require(stage != "rejected" && accepted.HasValue, "историческая стадия/принятие");
                Require(stage == "accepted" ? started is null && finished is null : started.HasValue, "время dispatch");
                Require(stage is "completed" or "failed" or "cancelled" ? finished.HasValue : finished is null, "время terminal");
                Require(!started.HasValue || accepted <= started, "accepted > started");
                Require(!finished.HasValue || started <= finished, "started > finished");
            }
            else {
                Require(accepted is null && started is null && finished is null, "служебные времена должны быть null");
                switch (kind) {
                    case "capabilities": Require(id == 0 && reply == 0 && target is null && source == "esp32" && operation == "capabilities" && stage == "completed" && !replayed, "capabilities envelope"); break;
                    case "control":
                        if (protocol == 2 && operation == "finish_display") Require(id == 0 && reply == 0 && target > 0 && source == "oled" && stage == "running" && Text(r, "reason") == "finish_pending", "finish pending envelope");
                        else Require(id == 0 && reply == 0 && target is null && source == "esp32" && operation == "all_off" && stage is "completed" or "failed" && !replayed, "all_off envelope");
                        break;
                    case "reply":
                        Require(stage == "rejected" && !replayed && id == reply, "rejected envelope");
                        Require(target is null || id == 0 && (source == "esp32" && operation == "status" || protocol == 2 && source == "oled" && operation == "finish_display") && target > 0, "rejected target");
                        break;
                    default: throw new InvalidDataException("неизвестный kind");
                }
            }
            JsonElement data = r.GetProperty("data");
            Require(data.ValueKind == JsonValueKind.Object, "data должен быть object");
            CheckCapabilities? capabilities = kind == "capabilities" ? ParseCapabilities(data, upper, protocol) : null;
            if (kind != "capabilities") {
                if (protocol == 2 && operation == "display_hold" && data.EnumerateObject().Any()) {
                    if (stage == "running") Require(Text(r, "reason") == "ready_for_observation", "running data только подтвержденная готовность OLED");
                    ValidateOled(data, stage, kind, started, finished);
                }
                else ValidateData(data, operation, source, stage, kind, started, finished);
                if (kind == "ready") Require(data.EnumerateObject().Any(), "ready без данных");
            }
            result = new CheckEvent(kind, device, boot, Text(r, "firmware_version"), Text(r, "config_version"), id, reply, target,
                source, operation, stage, Text(r, "reason"), replayed, upper, accepted, started, finished, code, domain, human, data.Clone(), capabilities) { ProtocolVersion = protocol };
            error = ""; return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException) {
            error = "TEST_EVENT: " + exception.Message; return false;
        }
    }

    private static CheckCapabilities ParseCapabilities(JsonElement data, ulong upper, uint protocol) {
        Shape(data, "history_capacity queue_capacity max_request_line_bytes frame_timeout_ms legacy_mode busy id_exhausted sources");
        Require(Unsigned(data, "history_capacity") == 8 && Unsigned(data, "queue_capacity") == 0 && Unsigned(data, "max_request_line_bytes") == 512
            && Unsigned(data, "frame_timeout_ms") == 2000 && Text(data, "legacy_mode") == "disabled", "неподдержанные лимиты capabilities");
        bool exhausted = Boolean(data, "id_exhausted"); Require(exhausted == (upper == ulong.MaxValue), "id_exhausted/watermark");
        JsonElement array = data.GetProperty("sources");
        Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == 9, "нужны девять capabilities");
        ImmutableArray<SourceCapability>.Builder sources = ImmutableArray.CreateBuilder<SourceCapability>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement s in array.EnumerateArray()) {
            string source = Text(s, "source_id");
            Require(Sources.Contains(source) && seen.Add(source), "неверный/повторный source capabilities");
            bool observe = Operation(source, protocol) == "observe";
            bool hold = source == "oled" && protocol == 2;
            Shape(s, "source_id initialized limitation operations human_confirmation duration_ms cancel" + (observe ? " min_duration_ms max_duration_ms default_duration_ms sample_period_ms" : hold ? " min_hold_ms max_hold_ms default_hold_ms ready_event hold_starts_at finish_operation" : ""));
            string[] expected = source == "esp32" ? new[] { "capabilities", "status", "all_off", "snapshot" } : hold ? new[] { "display_hold", "finish_display" } : new[] { Operation(source, protocol) };
            JsonElement ops = s.GetProperty("operations");
            Require(ops.ValueKind == JsonValueKind.Array && ops.GetArrayLength() == expected.Length && ops.EnumerateArray().Select(v => v.GetString()).ToHashSet().SetEquals(expected), "операции capabilities");
            Require(NullableText(s, "human_confirmation") == Human(source), "вопрос capabilities");
            string? limitation = source switch { "oled" => "transfer_only", "pir" => "power_not_verified", "relay_1" or "relay_2" => "no_contact_feedback", _ => null };
            Require(NullableText(s, "limitation") == limitation, "ограничение capabilities");
            ulong? duration = NullableUnsigned(s, "duration_ms");
            Require(duration == (source == "oled" && protocol == 1 ? 3000UL : source.StartsWith("relay_", StringComparison.Ordinal) ? 900UL : null), "фиксированная длительность");
            Require(Text(s, "cancel") == (source.StartsWith("relay_", StringComparison.Ordinal) ? "all_off" : "unsupported"), "cancel capabilities");
            if (hold) Require(Unsigned(s, "min_hold_ms") == 15000 && Unsigned(s, "max_hold_ms") == 310000 && Unsigned(s, "default_hold_ms") == 40000
                && Text(s, "ready_event") == "ready" && Text(s, "hold_starts_at") == "shown_ms" && Text(s, "finish_operation") == "finish_display", "контракт OLED hold");
            if (observe) Require(Unsigned(s, "min_duration_ms") == 100 && Unsigned(s, "max_duration_ms") == 10000 && Unsigned(s, "default_duration_ms") == 3000 && Unsigned(s, "sample_period_ms") == 100, "окно observe");
            sources.Add(new SourceCapability(source, Boolean(s, "initialized"), limitation, expected.ToImmutableArray(), Human(source)));
        }
        return new CheckCapabilities(Boolean(data, "busy"), exhausted, sources.ToImmutable());
    }

    // v2 различает dispatch и подтвержденный показ; исходные shown/deadline остаются теми же при status
    private static void ValidateOled(JsonElement d, string stage, string kind, ulong? started, ulong? finished) {
        Shape(d, "pattern max_hold_ms transfer_ok shown_ms deadline_ms dashboard_restored restored_ms finished_ms completion_cause restore_attempt_ms draw_error_code restore_error_code");
        Require(Text(d, "pattern") == "border_cross_OLED_TEST", "pattern OLED");
        ulong hold = Unsigned(d, "max_hold_ms"); Require(hold is >= 15000 and <= 310000, "max_hold_ms OLED");
        bool transfer = Boolean(d, "transfer_ok");
        ulong? shown = NullableUnsigned(d, "shown_ms"), deadline = NullableUnsigned(d, "deadline_ms");
        ulong? restored = NullableUnsigned(d, "restored_ms"), ownerFinished = NullableUnsigned(d, "finished_ms"), attempt = NullableUnsigned(d, "restore_attempt_ms");
        bool? dashboard = d.GetProperty("dashboard_restored").ValueKind == JsonValueKind.Null ? null : Boolean(d, "dashboard_restored");
        string? cause = NullableText(d, "completion_cause");
        int? drawError = d.GetProperty("draw_error_code").ValueKind == JsonValueKind.Null ? null : Signed(d, "draw_error_code");
        int? restoreError = d.GetProperty("restore_error_code").ValueKind == JsonValueKind.Null ? null : Signed(d, "restore_error_code");
        Require(drawError != 0 && restoreError != 0, "нулевой error должен быть null");
        Require(transfer == shown.HasValue && (!shown.HasValue || shown >= started && (!finished.HasValue || shown <= finished)), "shown/transfer/time");
        if (shown.HasValue && shown <= ulong.MaxValue - hold) Require(deadline == shown + hold, "deadline не равен shown+max_hold");
        else Require(deadline is null, "deadline без shown или при переполнении");
        if (stage == "running") {
            Require(kind is "ready" or "status" or "lifecycle" && transfer && deadline.HasValue && dashboard is null && restored is null
                && ownerFinished is null && cause is null && attempt is null && drawError is null && restoreError is null, "ready data");
        }
        else {
            Require(stage is "completed" or "failed" && dashboard.HasValue && ownerFinished.HasValue && attempt.HasValue
                && attempt >= started && attempt <= ownerFinished && ownerFinished <= finished, "terminal OLED времена");
            Require(cause is "explicit_finish" or "watchdog_expired" or "hardware_error", "completion_cause OLED");
            Require(dashboard == true ? restored.HasValue && restored >= attempt && restored <= ownerFinished && restoreError is null
                : restored is null && restoreError.HasValue, "результат restore");
            if (stage == "completed") Require(dashboard == true && drawError is null && cause != "hardware_error", "completed OLED без успешного восстановления");
            if (cause == "watchdog_expired") Require(deadline.HasValue && attempt >= deadline, "watchdog до deadline");
        }
    }

    // проверяем машинные поля до клонирования JsonElement; null не подменяется числом ноль
    // временные границы относятся к boot операции, а не к моменту получения status или повтора
    private static void ValidateData(JsonElement d, string? operation, string? source, string stage, string kind, ulong? started, ulong? finished) {
        if (!d.EnumerateObject().Any()) {
            Require(stage != "completed", "completed без результата"); return;
        }
        Require(stage is "completed" or "failed" or "cancelled", "данные до результата");
        void Time(string name) {
            ulong time = Unsigned(d, name);
            Require((!started.HasValue || time >= started) && (!finished.HasValue || time <= finished), "время data вне операции: " + name);
        }
        void Levels() { Require(Unsigned(d, "in1_level") <= 1 && Unsigned(d, "in2_level") <= 1, "уровни реле"); }
        switch (operation) {
            case "snapshot":
                Shape(d, "free_heap_bytes min_free_heap_bytes reset_reason_code reset_reason sampled_ms");
                U32(d, "free_heap_bytes"); U32(d, "min_free_heap_bytes"); U32(d, "reset_reason_code"); Text(d, "reset_reason"); Time("sampled_ms");
                Require(Unsigned(d, "min_free_heap_bytes") <= Unsigned(d, "free_heap_bytes"), "минимум RAM больше текущего"); break;
            case "read_next":
                Shape(d, "temperature_c humidity_pct"); Number(d, "temperature_c");
                double humidity = Number(d, "humidity_pct"); Require(humidity is >= 0 and <= 100, "влажность вне 0..100"); break;
            case "observe":
                Shape(d, "initial_raw_level final_raw_level transition_count last_transition_ms window_start_ms window_end_ms requested_duration_ms sample_period_ms sample_count max_sample_gap_ms");
                Require(Unsigned(d, "initial_raw_level") <= 1 && Unsigned(d, "final_raw_level") <= 1, "raw не 0/1");
                U32(d, "transition_count"); U32(d, "requested_duration_ms");
                ulong begin = Unsigned(d, "window_start_ms"), end = Unsigned(d, "window_end_ms"), requested = Unsigned(d, "requested_duration_ms");
                Time("window_start_ms"); Time("window_end_ms");
                Require(begin <= end && requested is >= 100 and <= 10000 && end - begin >= requested && Unsigned(d, "sample_period_ms") == 100, "границы observe");
                ulong count = Unsigned(d, "sample_count"), transitions = Unsigned(d, "transition_count");
                Require(count >= 2 && transitions < count && Unsigned(d, "max_sample_gap_ms") <= end - begin, "выборки observe");
                ulong? last = NullableUnsigned(d, "last_transition_ms");
                Require(transitions == 0 ? last is null : last >= begin && last <= end, "последний переход"); break;
            case "display_test":
                Shape(d, "pattern requested_duration_ms shown_ms restored_ms transfer_ok dashboard_restored");
                Require(Text(d, "pattern") == "border_cross_OLED_TEST" && Unsigned(d, "requested_duration_ms") == 3000, "рисунок OLED");
                Time("shown_ms"); Time("restored_ms"); Require(Unsigned(d, "shown_ms") <= Unsigned(d, "restored_ms"), "время OLED");
                Boolean(d, "transfer_ok"); Boolean(d, "dashboard_restored"); break;
            case "run":
                bool created = Boolean(d, "file_created");
                Shape(d, "test_passed duration_ms test_started_ms test_finished_ms file_created file_retained cleanup_ok" + (created ? " test_file_index" : ""));
                Boolean(d, "test_passed"); Boolean(d, "file_retained"); Boolean(d, "cleanup_ok");
                Time("test_started_ms"); Time("test_finished_ms");
                ulong a = Unsigned(d, "test_started_ms"), b = Unsigned(d, "test_finished_ms");
                Require(a <= b && Unsigned(d, "duration_ms") == b - a, "длительность SD");
                if (created) Require(Unsigned(d, "test_file_index") <= 99, "индекс файла SD");
                Require(!Boolean(d, "file_retained") || created, "оставлен не созданный файл"); break;
            case "pulse" when stage != "cancelled" && !d.TryGetProperty("off_command_ok", out _):
                Shape(d, "channel gpio requested_pulse_ms in1_level in2_level pulse_started_ms off_callback_ms");
                Require(Unsigned(d, "channel") == (source == "relay_1" ? 1UL : 2UL) && Unsigned(d, "gpio") == (source == "relay_1" ? 32UL : 33UL) && Unsigned(d, "requested_pulse_ms") == 900, "канал/интервал реле");
                Levels(); Time("pulse_started_ms");
                ulong? callback = NullableUnsigned(d, "off_callback_ms");
                Require(!callback.HasValue || callback >= Unsigned(d, "pulse_started_ms") && (!finished.HasValue || callback <= finished), "callback реле"); break;
            case "pulse":
            case "all_off":
                Shape(d, "in1_level in2_level off_command_ok stopped_channel gpio requested_pulse_ms pulse_started_ms off_command_ms");
                Levels(); Boolean(d, "off_command_ok");
                ulong channel = Unsigned(d, "stopped_channel"); Require(channel <= 2, "stopped_channel");
                Require(NullableUnsigned(d, "gpio") == (channel == 0 ? null : channel == 1 ? 32UL : 33UL)
                    && NullableUnsigned(d, "requested_pulse_ms") == (channel == 0 ? null : 900UL), "all_off канал");
                ulong? pulse = NullableUnsigned(d, "pulse_started_ms");
                Require(channel == 0 ? pulse is null : pulse.HasValue && pulse <= Unsigned(d, "off_command_ms"), "all_off время");
                Time("off_command_ms"); break;
            default: throw new InvalidDataException("data у неподдержанной операции");
        }
    }
    public static void Require(bool valid, string error) { if (!valid) throw new InvalidDataException(error); }
    private static void Unique(JsonElement e) {
        if (e.ValueKind == JsonValueKind.Object) {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in e.EnumerateObject()) { Require(seen.Add(p.Name), "повтор JSON-ключа " + p.Name); Unique(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (JsonElement v in e.EnumerateArray()) Unique(v);
    }
    private static void Shape(JsonElement e, string fields) {
        Require(e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields.Split(' ')), "поля объекта: " + fields);
    }
    private static string Text(JsonElement e, string name) {
        JsonElement value = e.GetProperty(name); Require(value.ValueKind == JsonValueKind.String, "ожидается строка " + name);
        string text = value.GetString()!; Require(text.Length is > 0 and <= 512 && !text.Any(char.IsControl), "размер/символы " + name); return text;
    }
    private static string? NullableText(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(e, name);
    private static ulong? NullableId(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Id(Text(e, name));
    private static ulong Unsigned(JsonElement e, string name) {
        JsonElement v = e.GetProperty(name); Require(v.ValueKind == JsonValueKind.Number && Regex.IsMatch(v.GetRawText(), @"\A[0-9]+\z") && v.TryGetUInt64(out _), "UInt64 " + name); return v.GetUInt64();
    }
    private static ulong? NullableUnsigned(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Unsigned(e, name);
    private static void U32(JsonElement e, string name) => Require(Unsigned(e, name) <= uint.MaxValue, "UInt32 " + name);
    private static int Signed(JsonElement e, string name) {
        JsonElement v = e.GetProperty(name); Require(v.ValueKind == JsonValueKind.Number && Regex.IsMatch(v.GetRawText(), @"\A-?[0-9]+\z") && v.TryGetInt32(out _), "Int32 " + name); return v.GetInt32();
    }
    private static bool Boolean(JsonElement e, string name) {
        JsonElement v = e.GetProperty(name); Require(v.ValueKind is JsonValueKind.True or JsonValueKind.False, "bool " + name); return v.GetBoolean();
    }
    private static double Number(JsonElement e, string name) {
        JsonElement v = e.GetProperty(name); Require(v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && double.IsFinite(n), "finite number " + name); return v.GetDouble();
    }
}
