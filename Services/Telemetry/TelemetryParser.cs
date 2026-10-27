using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Telemetry;

public sealed class TelemetryParser {
    private static readonly Dictionary<string, string> Expected = new Dictionary<string, string> {
        ["esp32"] = "system",
        ["oled"] = "display",
        ["dht11"] = "temperature_humidity",
        ["light"] = "digital_input",
        ["ir"] = "digital_input",
        ["pir"] = "digital_input",
        ["microsd"] = "storage_test",
        ["relay_1"] = "relay_output",
        ["relay_2"] = "relay_output"
    };

    // строку сначала проверяем целиком: дубли ключей, типы, профиль и согласованность наблюдений
    // у каждого источника попытка и последний успех могут различаться, например после ошибки DHT
    // только после всех проверок возвращаем пакет, чтобы неверная строка не заменила текущий снимок
    public bool TryParse(string text, out TelemetryPacket? packet, out string error) {
        packet = null;
        try {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            CheckKeys(root);
            Require(root.GetProperty("schema_version").GetInt32() == 1, "неподдержанная schema_version");
            string device = Text(root, "device_id");
            string boot = Text(root, "boot_id");
            Require(Regex.IsMatch(device, @"\Aesp32-[0-9a-fA-F]{12}\z"), "неверный device_id");
            Require(Regex.IsMatch(boot, @"\A[0-9a-fA-F]{32}\z"), "неверный boot_id");
            ulong sequence = root.GetProperty("sequence").GetUInt64();
            ulong uptime = root.GetProperty("uptime_ms").GetUInt64();
            string firmware = Text(root, "firmware_version");
            string config = Text(root, "config_version");
            Require(config == "stand-v1-pir-unverified", "неподдержанный config_version: " + config);
            JsonElement local = root.GetProperty("local_diagnostic");
            string diagnostic = Text(local, "state") + " / " + Text(local, "rule") + " / " + Text(local, "reason");
            JsonElement items = root.GetProperty("observations");
            Require(items.ValueKind == JsonValueKind.Array, "observations должен быть массивом");
            List<Observation> observations = new List<Observation>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in items.EnumerateArray()) {
                string id = Text(item, "source_id");
                string type = Text(item, "source_type");
                Require(ids.Add(id), "повтор source_id: " + id);
                Require(Expected.TryGetValue(id, out string? expected) && expected == type, "неподдержанный источник/тип: " + id);
                bool enabled = item.GetProperty("enabled").GetBoolean();
                string status = Text(item, "status");
                Require(new[] { "disabled", "not_polled", "unavailable", "error", "ok" }.Contains(status), "неверный status");
                Require(enabled == (status != "disabled"), "enabled противоречит status");
                ulong? attempt = NullableUInt(item, "last_attempt_ms");
                ulong? success = NullableUInt(item, "last_success_ms");
                JsonElement value = item.GetProperty("values");
                bool hasValues = value.ValueKind != JsonValueKind.Null;
                Require(hasValues == success.HasValue, "values/last_success_ms не согласованы");
                Require(!attempt.HasValue || attempt.Value <= uptime, "last_attempt_ms позже uptime");
                Require(!success.HasValue || (attempt.HasValue && success.Value <= attempt.Value), "неверный last_success_ms");
                uint consecutive = item.GetProperty("consecutive_errors").GetUInt32();
                ulong attempts = item.GetProperty("attempt_count").GetUInt64();
                Require((attempts > 0) == attempt.HasValue && consecutive <= attempts, "неверные счетчики");
                int? code = NullableInt(item, "error_code");
                string? domain = NullableText(item, "error_domain");
                string? detail = NullableText(item, "error_detail");
                Require(domain is null or "esp_err" or "errno", "неизвестный error_domain");
                Require(code.HasValue == (domain is not null), "код ошибки без домена");
                if (status == "ok")
                    Require(hasValues && success == attempt && consecutive == 0 && code is null && detail is null, "несогласованный ok");
                if (status is "disabled" or "not_polled")
                    Require(!attempt.HasValue && !hasValues && attempts == 0 && consecutive == 0 && code is null && detail is null, "несогласованный исходный статус");
                if (status is "unavailable" or "error")
                    Require(attempt.HasValue && consecutive > 0 && !string.IsNullOrEmpty(detail), "ошибка без попытки/причины");
                int? raw = item.TryGetProperty("raw_level", out JsonElement rawValue) && rawValue.ValueKind != JsonValueKind.Null ? rawValue.GetInt32() : null;
                if (id == "pir") {
                    Require(item.TryGetProperty("raw_level", out _), "нет raw_level PIR");
                    Require(raw is null or 0 or 1, "raw_level вне 0/1");
                    Require(!hasValues, "непроверенный PIR не должен иметь values");
                    Require(status == "not_polled" || (status == "unavailable" && detail == "power_not_verified"), "неверный профиль PIR");
                }
                if (hasValues) ValidateValues(id, value);
                // values клонируем отдельно от JsonDocument, который закроется при выходе из TryParse
                observations.Add(new Observation(id, type, enabled, status, attempt, success,
                    hasValues ? value.Clone() : null, code, domain, detail, consecutive, attempts, raw));
            }
            Require(ids.SetEquals(Expected.Keys), "ожидаются все девять источников");
            packet = new TelemetryPacket(device, boot, sequence, uptime, firmware, config, diagnostic, observations.AsReadOnly());
            error = "";
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException or InvalidDataException) {
            error = exception.Message;
            return false;
        }
    }

    private static void ValidateValues(string id, JsonElement values) {
        Require(values.ValueKind == JsonValueKind.Object, "values должен быть объектом");
        switch (id) {
            case "esp32":
                ulong free = values.GetProperty("free_heap_bytes").GetUInt64();
                ulong minimum = values.GetProperty("min_free_heap_bytes").GetUInt64();
                Require(minimum <= free, "минимум памяти больше текущей");
                values.GetProperty("reset_reason_code").GetInt32();
                Text(values, "reset_reason");
                break;
            case "oled": Require(values.GetProperty("transfer_ok").GetBoolean(), "неверный успешный OLED"); break;
            case "dht11":
                double temperature = values.GetProperty("temperature_c").GetDouble();
                double humidity = values.GetProperty("humidity_pct").GetDouble();
                Require(double.IsFinite(temperature) && double.IsFinite(humidity), "нечисловое измерение");
                break;
            case "light":
            case "ir": Require(values.GetProperty("digital_level").GetInt32() is 0 or 1, "digital_level вне 0/1"); break;
            case "microsd":
                Require(values.GetProperty("test_passed").GetBoolean(), "неверный успешный SD-тест");
                values.GetProperty("duration_ms").GetUInt64();
                break;
            case "relay_1":
            case "relay_2":
                Require(values.GetProperty("gpio_level").GetInt32() is 0 or 1, "gpio_level вне 0/1");
                values.GetProperty("pulse_active").GetBoolean();
                break;
        }
    }

    // в объекте проверяем уникальность имен, затем спускаемся в его значения и элементы массивов
    // скаляр завершает обход: вложенных ключей у него нет
    private static void CheckKeys(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object) {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject()) {
                Require(names.Add(property.Name), "дублирующий ключ JSON: " + property.Name);
                CheckKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) CheckKeys(child);
    }

    private static string Text(JsonElement item, string key) {
        string? text = item.GetProperty(key).GetString();
        Require(!string.IsNullOrWhiteSpace(text), "пустое поле: " + key);
        return text!;
    }
    private static string? NullableText(JsonElement item, string key) => item.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Text(item, key);
    private static ulong? NullableUInt(JsonElement item, string key) => item.GetProperty(key).ValueKind == JsonValueKind.Null ? null : item.GetProperty(key).GetUInt64();
    private static int? NullableInt(JsonElement item, string key) => item.GetProperty(key).ValueKind == JsonValueKind.Null ? null : item.GetProperty(key).GetInt32();
    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidDataException(message);
    }
}
