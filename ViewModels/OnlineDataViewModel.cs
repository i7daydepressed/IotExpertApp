using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Live;

namespace IotExpertApp.ViewModels;

public partial class OnlineDataViewModel : ViewModelBase {
    public ObservableCollection<OnlineSourceViewModel> Sources { get; } = new ObservableCollection<OnlineSourceViewModel>();
    [ObservableProperty] private string mode = "Еще нет данных";
    [ObservableProperty] private string identity = "Устройство неизвестно";
    [ObservableProperty] private string packetInfo = "Пакет еще не принят";
    [ObservableProperty] private string counters = "Принято пакетов: 0";
    [ObservableProperty] private string policy = "Политика актуальности профиля не применена: профиль не загружен";
    [ObservableProperty] private string details = "";
    public OnlineDataViewModel() {
        string[] ids = { "esp32", "oled", "dht11", "light", "ir", "pir", "microsd", "relay_1", "relay_2" };
        string[] titles = { "ESP32 · RAM", "OLED", "DHT11 · воздух", "Свет", "ИК", "PIR", "microSD", "Реле 1", "Реле 2" };
        for (int i = 0; i < ids.Length; i++) Sources.Add(new OnlineSourceViewModel(ids[i], titles[i]));
    }
    public void Update(ReceiverState state, DeviceProfile? profile, TimeSpan hostAge) {
        TelemetryPacket? packet = state.Snapshot?.Packet;
        bool matches = packet is not null && profile is not null && packet.DeviceId == profile.DeviceId && packet.ConfigVersion == profile.ConfigVersion;
        bool live = state.IsSerial && state.Running;
        bool expired = matches && live && hostAge > TimeSpan.FromMilliseconds(profile!.PacketTimeoutMs);
        Mode = packet is null ? live ? "Порт открыт, ожидаем данные" : "Еще нет данных"
            : !state.Running && !state.Completed ? state.IsSerial ? "Последние полученные данные; источник отключен" : "Запись: остановлена; последние полученные данные"
            : !state.IsSerial ? state.ReplayPaused ? "Запись: пауза" : state.Completed ? "Запись: конец файла" : "Запись: воспроизведение"
            : expired ? "Нет свежих пакетов" : "Живой поток";
        Identity = packet?.DeviceId ?? "Устройство неизвестно";
        Policy = matches ? "Политика актуальности: " + profile!.Title + " · " + profile.Version
            : "Политика актуальности профиля не применена: " + (profile is null ? "профиль не загружен" : packet is null ? "еще нет пакета" : "не совпали устройство или config");
        PacketInfo = state.Snapshot is null ? "Пакет еще не принят" : "Принято " + state.Snapshot.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture)
            + (live ? "; возраст пакета " + Milliseconds(hostAge) + " мс" : "; исторический снимок, часы хоста не применяются");
        Counters = "Принято пакетов: " + state.Valid
            + (state.Invalid > 0 || state.TemporalRejected > 0 || state.RejectedOrder > 0 ? "; отклонено: " + state.Invalid + "/" + state.TemporalRejected + "/" + state.RejectedOrder : "")
            + (state.Missing > 0 ? "; пропусков sequence: " + state.Missing + " (не диагноз датчика)" : "")
            + (state.TestErrors > 0 ? "; ошибок TEST_EVENT: " + state.TestErrors : "")
            + (state.Oversized > 0 ? "; слишком длинных строк: " + state.Oversized : "")
            + (state.Tails > 0 ? "; неполных строк: " + state.Tails : "");
        Details = "Источник: " + state.Origin + "; " + state.Status + "\n"
            + (packet is null ? "Нет принятого снимка" : "boot " + packet.BootId + "; sequence " + packet.Sequence + "; uptime " + packet.UptimeMs + " мс\n" + packet.FirmwareVersion + " | " + packet.ConfigVersion)
            + "\nСчетчики: valid=" + state.Valid + "; logs=" + state.Logs + "; invalid=" + state.Invalid + "; temporal=" + state.TemporalRejected
            + "; order=" + state.RejectedOrder + "; missing=" + state.Missing + "; oversized=" + state.Oversized + "; tails=" + state.Tails
            + "; TEST_EVENT=" + state.TestEvents + "; test_errors=" + state.TestErrors;
        foreach (OnlineSourceViewModel tile in Sources) {
            Observation? observation = packet?.Observations.FirstOrDefault(o => o.SourceId == tile.Source);
            SourceRequirement? requirement = matches ? profile!.Sources.FirstOrDefault(s => s.SourceId == tile.Source) : null;
            tile.Update(observation, packet, state, requirement, hostAge, expired);
        }
    }
    internal static decimal Milliseconds(TimeSpan age) => Math.Max(0m, (decimal)age.Ticks / TimeSpan.TicksPerMillisecond);
    internal static string Age(ulong uptime, ulong? timestamp, TimeSpan hostAge) {
        if (!timestamp.HasValue) return "неизвестен";
        if (timestamp > uptime) return "время источника противоречит uptime";
        // сначала вычитаем UInt64; decimal вмещает весь возраст и небольшой прирост хоста без потери разрядов
        return ((decimal)(uptime - timestamp.Value) + Milliseconds(hostAge)).ToString("0", CultureInfo.InvariantCulture) + " мс";
    }
}

public partial class OnlineSourceViewModel : ViewModelBase {
    public string Source { get; }
    public string Title { get; }
    [ObservableProperty] private string status = "Еще нет данных";
    [ObservableProperty] private string values = "Неизвестно";
    [ObservableProperty] private string freshness = "Нет принятого наблюдения";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string details = "";
    private Observation? shown;
    public OnlineSourceViewModel(string source, string title) { Source = source; Title = title; }
    internal void Update(Observation? observation, TelemetryPacket? packet, ReceiverState state, SourceRequirement? requirement, TimeSpan hostAge, bool expired) {
        if (observation is null || packet is null) {
            shown = null; Status = "Еще нет данных"; Values = "Неизвестно"; Freshness = "Нет принятого наблюдения"; Error = ""; Details = ""; return;
        }
        string normalized = requirement is null ? !observation.Enabled ? "disabled" : observation.LastAttemptMs is null ? "not_polled" : observation.Status
            : LiveAssessmentService.Normalize(requirement, state, state.Running ? hostAge : TimeSpan.Zero);
        Status = normalized switch { "ok" => "Данные получены", "error" => "Ошибка последнего чтения", "unavailable" => "Источник недоступен",
            "disabled" => "Источник отключен", "not_polled" => "Еще не опрошен", "stale" => "Данные устарели", _ => "Данных недостаточно" };
        if (Source is "microsd" or "relay_1" or "relay_2") Status = "Последнее программное наблюдение · " + Status;
        if (Source == "pir") Status += " · Питание не подтверждено";
        if (expired && requirement is not null) Status = "Нет свежих пакетов · " + Status;
        TimeSpan added = state.IsSerial && state.Running ? hostAge : TimeSpan.Zero;
        Freshness = "Возраст попытки: " + OnlineDataViewModel.Age(packet.UptimeMs, observation.LastAttemptMs, added)
            + (Source == "pir" ? "; raw не является успешным измерением движения" : "\nВозраст успеха: " + OnlineDataViewModel.Age(packet.UptimeMs, observation.LastSuccessMs, added))
            + (requirement is null ? "; периодическая актуальность профилем не задана" : "; предел " + requirement.MaximumAgeMs + " мс");
        if (Source is "microsd" or "relay_1" or "relay_2") Freshness += "; исторический тест/состояние, текущая физическая доступность не доказана";
        Error = observation.Status is "error" or "unavailable" ? observation.ErrorDetail ?? "Причина не указана" : "";
        if (ReferenceEquals(shown, observation)) return;
        shown = observation;
        string Field(string key) => observation.Values.HasValue && observation.Values.Value.TryGetProperty(key, out JsonElement value) ? value.ValueKind switch {
            JsonValueKind.True => "да", JsonValueKind.False => "нет", JsonValueKind.Null => "нет данных", _ => value.ToString() } : "нет данных";
        string Bytes(string key) => observation.Values.HasValue && observation.Values.Value.TryGetProperty(key, out JsonElement value) && value.TryGetUInt64(out ulong bytes)
            ? (bytes / 1024m).ToString("0.0", CultureInfo.CurrentCulture) + " КиБ (" + bytes + " байт)" : "нет данных";
        Values = Source switch {
            "esp32" => "Свободно: " + Bytes("free_heap_bytes") + "\nМинимум: " + Bytes("min_free_heap_bytes"),
            "oled" => "Передача framebuffer: " + Field("transfer_ok"),
            "dht11" => (observation.Status == "ok" ? "" : "Последние успешные значения: ") + Field("temperature_c") + " °C · влажность " + Field("humidity_pct") + " %",
            "light" or "ir" => "Цифровой уровень: " + Field("digital_level"),
            "pir" => "Raw GPIO: " + (observation.RawLevel?.ToString(CultureInfo.InvariantCulture) ?? "нет данных") + "; питание не подтверждено",
            "microsd" => "Последний файловый тест: " + Field("test_passed") + "\nДлительность: " + Field("duration_ms") + " мс",
            _ => "GPIO: " + Field("gpio_level") + "\nИмпульс активен: " + Field("pulse_active")
        };
        string limit = Source switch { "esp32" => "Метрики RAM, не проверка всех ячеек; сброс: " + Field("reset_reason"), "oled" => "Передача не доказывает видимость изображения",
            "dht11" => "Температура воздуха; калибровка не проверена", "light" => "Без люксов и неподтвержденной полярности", "ir" => "Расстояние не измеряется",
            "pir" => "Raw GPIO не доказывает движение или питание", "microsd" => "Емкость и свободное место не измерены", _ => "GPIO/импульс без обратной связи контактов" };
        Details = limit + "\nstatus=" + observation.Status + "; enabled=" + observation.Enabled + "; last_attempt=" + observation.LastAttemptMs + "; last_success=" + observation.LastSuccessMs
            + "; attempts=" + observation.AttemptCount + "; errors=" + observation.ConsecutiveErrors
            + (observation.ErrorCode.HasValue || observation.ErrorDomain is not null ? "\n" + observation.ErrorDomain + " / " + observation.ErrorCode : "");
    }
}
