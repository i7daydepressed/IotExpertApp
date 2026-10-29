using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Live;
using IotExpertApp.Services.Telemetry;
using IotExpertApp.ViewModels;

namespace IotExpertApp.ConsoleUI;

public static class LiveAssessmentChecks {
    public static void Run(string syntheticJson) {
        string profilePath = Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "esp32-observability.json");
        byte[] original = File.ReadAllBytes(profilePath);
        DeviceProfileService loader = new DeviceProfileService();
        DeviceProfile profile = loader.Load(profilePath);
        TelemetryParser parser = new TelemetryParser();
        Check(parser.TryParse(syntheticJson, out TelemetryPacket? parsed, out string error), error);
        // fixture остается искусственным: совпадение device_id здесь проверяет ветвь профиля, а не оборудование
        TelemetryPacket normal = parsed! with { DeviceId = profile.DeviceId };
        ReceiverState ready = State(normal);
        LiveAssessmentService service = new LiveAssessmentService();
        LiveAssessment first = service.Assess(profile, ready, TimeSpan.Zero);
        Check(Strategy(first) == "A1" && first.Diagnostic!.Steps.Single().RuleId == "OBS-01", "свежие обязательные источники -> A1");
        Check(first.Context.Contains("18446744073709551615", StringComparison.Ordinal), "sequence UInt64 без double");
        Observation dht = normal.Observations.Single(item => item.SourceId == "dht11");
        Observation failed = dht with { Status = "error", LastAttemptMs = 200, ConsecutiveErrors = 1,
            AttemptCount = 2, ErrorCode = 263, ErrorDomain = "esp_err", ErrorDetail = "synthetic_timeout" };
        TelemetryPacket failedPacket = normal with { UptimeMs = 200,
            Observations = normal.Observations.Select(item => item.SourceId == "dht11" ? failed : item).ToImmutableArray() };
        ReceiverState failedState = State(failedPacket);
        LiveAssessment failure = service.Assess(profile, failedState, TimeSpan.Zero);
        Check(Strategy(failure) == "A2" && failure.Diagnostic!.Steps.Single().RuleId == "OBS-02", "ошибка со старым values -> A2");
        Check(failure.Diagnostic!.Inputs["climate_status"].Value!.Canonical == "error", "старое значение не скрывает текущую ошибку");
        LiveAssessment recovered = service.Assess(profile, ready, TimeSpan.Zero);
        Check(Strategy(recovered) == "A1" && Strategy(failure) == "A2", "новый расчет не оставляет производные старого расчета");
        Check(failure.Strategy!.Inputs["V3"].Origin.Contains("OBS-02", StringComparison.Ordinal), "мост сохраняет ID диагностического правила");
        Check(service.Assess(profile, ready, TimeSpan.FromMilliseconds(profile.PacketTimeoutMs + 1)).Strategy is null,
            "устаревший живой пакет снимает рекомендацию");
        Check(Strategy(service.Assess(profile, ready, TimeSpan.FromMilliseconds(profile.PacketTimeoutMs))) == "A1", "граница срока включена");
        TelemetryPacket aged = normal with { UptimeMs = 10200,
            Observations = normal.Observations.Select(item => item.SourceId is "esp32" or "oled" or "light" or "ir"
                ? item with { LastAttemptMs = 10200, LastSuccessMs = 10200 } : item).ToImmutableArray() };
        Check(Strategy(service.Assess(profile, State(aged), TimeSpan.Zero)) == "NEED_DATA", "старый источник внутри свежего пакета -> NEED_DATA");
        TelemetryPacket delayed = normal with { UptimeMs = 5000,
            Observations = normal.Observations.Select(item => item.SourceId is "esp32" or "oled" or "light" or "ir"
                ? item with { LastAttemptMs = 5000, LastSuccessMs = 5000 } : item).ToImmutableArray() };
        ReceiverState delayedState = State(delayed);
        TimeSpan hostDelay = TimeSpan.FromMilliseconds(6000);
        Check(service.RefreshKey(profile, delayedState, TimeSpan.Zero) != service.RefreshKey(profile, delayedState, hostDelay)
            && Strategy(service.Assess(profile, delayedState, hostDelay)) == "NEED_DATA",
            "суммарный возраст источника меняет ключ без нового пакета, пока возраст самого пакета еще допустим");
        Check(service.Assess(profile, ready with { Running = false }, TimeSpan.Zero).Strategy is null, "Stop снимает текущую рекомендацию");
        ReceiverState replay = ready with { IsSerial = false, ReplayPaused = true };
        Check(Strategy(service.Assess(profile, replay, TimeSpan.FromDays(30))) == "A1", "пауза replay не стареет по часам хоста");
        Check(service.Assess(profile, replay, TimeSpan.Zero).Title.Contains("Запись", StringComparison.Ordinal), "запись не названа живым потоком");
        Check(Strategy(service.Assess(profile, replay with { Running = false, Completed = true }, TimeSpan.FromDays(30))) == "A1", "EOF сохраняет архивный результат");
        Check(Strategy(service.Assess(profile, State(normal with { DeviceId = "esp32-000000000000" }), TimeSpan.Zero)) == "NO_PROFILE", "чужое устройство -> NO_PROFILE");
        Check(Strategy(service.Assess(profile, State(normal with { ConfigVersion = "different" }), TimeSpan.Zero)) == "NO_PROFILE", "другая конфигурация -> NO_PROFILE");
        TelemetryPacket rawChange = normal with { LocalDiagnostic = "FAULT / synthetic / не вход экспертной оценки",
            Observations = normal.Observations.Select(item => item.SourceId == "pir" ? item with { RawLevel = 1 } : item).ToImmutableArray() };
        Check(Strategy(service.Assess(profile, State(rawChange), TimeSpan.Zero)) == "A1", "PIR raw и локальный диагноз не задают итог ЭС");
        Check(PortSelectionService.Choose(null, new[] { "/dev/cu.Bluetooth-Incoming-Port", "/dev/cu.usbserial-0001" }) == "/dev/cu.usbserial-0001", "один кандидат выбран");
        Check(PortSelectionService.Choose(null, new[] { "COM3", "COM4" }) is null, "несколько кандидатов требуют выбора");
        Check(PortSelectionService.Choose("COM4", new[] { "COM3", "COM4" }) == "COM4", "выбор пользователя сохранен");
        Check(PortSelectionService.Choose(null, new[] { "/dev/cu.Bluetooth-Incoming-Port" }) is null, "Bluetooth не выбран автоматически");
        Check(PortSelectionService.Choose(null, new[] { "/dev/cu.SLAB_USBtoUART" }) == "/dev/cu.SLAB_USBtoUART", "macOS CP210x кандидат");
        Check(PortSelectionService.Choose(null, new[] { "/dev/ttyUSB0" }) == "/dev/ttyUSB0", "Linux кандидат");

        DirectoryInfo temporary = Directory.CreateTempSubdirectory("iot-live-check-");
        try {
            JsonObject json = JsonNode.Parse(original)!.AsObject();
            json["diagnostic_knowledge"] = Path.GetRelativePath(temporary.FullName,
                Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "esp32-observability.rules.json"));
            json["strategy_knowledge"] = Path.GetRelativePath(temporary.FullName,
                Path.Combine(AppContext.BaseDirectory, "Knowledge", "lr1-strategies.json"));
            if (json.ContainsKey("module_check_knowledge")) json["module_check_knowledge"] = Path.GetRelativePath(temporary.FullName,
                Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "esp32-module-checks.rules.json"));
            json["version"] = "synthetic-reload-2";
            string alternative = Path.Combine(temporary.FullName, "profile.json");
            File.WriteAllText(alternative, json.ToJsonString());
            DeviceProfile next = loader.Load(alternative);
            Check(next.Version == "synthetic-reload-2" && next.Sha256 != profile.Sha256, "новая версия профиля загружена отдельно");
            json["static_facts"]!["V1"] = true;
            string invalid = Path.Combine(temporary.FullName, "invalid.json");
            File.WriteAllText(invalid, json.ToJsonString());
            bool rejected = false;
            try { loader.Load(invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "профиль не может подменить проверку идентичности V1 константой");
            LiveMonitorViewModel model = new LiveMonitorViewModel { ProfilePath = profilePath };
            model.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            model.Update(failedState); model.FreezeCommand.Execute(null);
            LiveAssessment pinned = model.Frozen ?? throw new InvalidOperationException("результат не закреплен");
            string explanation = model.ExplanationText;
            model.Update(ready);
            Check(Strategy(model.Latest!) == "A1" && ReferenceEquals(pinned, model.Frozen)
                && Strategy(pinned) == "A2" && model.ExplanationText == explanation, "новый пакет не меняет объяснение выбранного A2");
            model.ProfilePath = invalid; model.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(model.Profile!.Sha256 == profile.Sha256 && ReferenceEquals(pinned, model.Frozen), "ошибка загрузки сохраняет действующий профиль и объяснение");
            model.ProfilePath = alternative; model.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult(); model.Update(ready);
            Check(model.Latest!.Profile.Version == "synthetic-reload-2" && model.Frozen!.Profile.Version == profile.Version,
                "новый профиль меняет следующий расчет, старый остается закрепленным");
            Check(File.ReadAllBytes(profilePath).SequenceEqual(original), "поставляемый профиль не изменен");
        }
        finally { temporary.Delete(true); }
        Console.WriteLine("живой профиль: A1/A2/NEED_DATA/NO_PROFILE, возраст, Stop/replay, закрепление и выбор порта проверены на искусственных входах; физический стенд этим не проверен");
    }

    private static ReceiverState State(TelemetryPacket packet) => new ReceiverState("прием данных", "synthetic-step09",
        1, 0, 0, 0, 0, 0, 0, "", new ReceivedSnapshot(packet, DateTimeOffset.UtcNow, "synthetic-step09"),
        0, true, false, false, "", "") { IsSerial = true };
    private static string Strategy(LiveAssessment result) => result.Strategy is null ? "none" : LiveAssessmentService.Goal(result.Strategy);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
