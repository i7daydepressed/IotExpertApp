using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Checks;

public sealed record CommandCompatibility(string Config, ImmutableHashSet<string> FirmwareVersions) {
    public ImmutableDictionary<string, uint> Protocols { get; init; } = ImmutableDictionary<string, uint>.Empty;
    public string? DeviceId { get; init; }
    public uint Protocol(string firmware) => Protocols.TryGetValue(firmware, out uint version) ? version : 1;
    public static CommandCompatibility Load(string path) {
        using FileStream input = File.OpenRead(path);
        CheckProtocol.Require(input.Length <= 4096, "слишком большой профиль совместимости");
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        using JsonDocument doc = IotExpertApp.Services.Knowledge.KnowledgeJson.Parse(bytes);
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty("schema_version", out JsonElement schema)) {
            IotExpertApp.Services.Knowledge.KnowledgeJson.Shape(root, "профиль команд", new[] { "schema_version", "config_version", "device_id", "firmware_protocols" });
            CheckProtocol.Require(schema.GetInt32() == 2, "schema профиля команд");
            ImmutableDictionary<string, uint>.Builder protocols = ImmutableDictionary.CreateBuilder<string, uint>(StringComparer.Ordinal);
            foreach (JsonProperty p in root.GetProperty("firmware_protocols").EnumerateObject()) {
                uint v = p.Value.GetUInt32(); CheckProtocol.Require(v is 1 or 2 && p.Name.Length > 0, "протокол версии прошивки"); protocols.Add(p.Name, v);
            }
            CheckProtocol.Require(protocols.Count is > 0 and <= 32, "размер whitelist");
            return new CommandCompatibility(root.GetProperty("config_version").GetString()!, protocols.Keys.ToImmutableHashSet(StringComparer.Ordinal)) {
                Protocols = protocols.ToImmutable(), DeviceId = root.GetProperty("device_id").GetString()
            };
        }
        IotExpertApp.Services.Knowledge.KnowledgeJson.Shape(root, "старый профиль команд", new[] { "protocol_version", "config_version", "compatible_firmware_versions" });
        CheckProtocol.Require(root.GetProperty("protocol_version").GetInt32() == 1, "профиль команд v1");
        ImmutableHashSet<string> versions = root.GetProperty("compatible_firmware_versions").EnumerateArray().Select(v => v.GetString()!).ToImmutableHashSet(StringComparer.Ordinal);
        CheckProtocol.Require(versions.Count is > 0 and <= 32 && versions.All(v => !string.IsNullOrEmpty(v)), "пустая совместимость");
        return new CommandCompatibility(root.GetProperty("config_version").GetString()!, versions);
    }
}

// все методы вызываются под gate единственного приемника; сам сеанс не открывает UART
// на входе подтвержденная v1, явные запросы оператора и проверенные события того же boot
// reservations хранит верхнюю бронь ID и единственный незавершенный запрос; очередь несет поколение соединения
// перед Write сверяем поколение еще раз, а после возможной отправки ждем terminal или status без нового действия
// история ограничена, terminal не меняем задним числом; старый boot и ответ человека не становятся текущей телеметрией
public sealed class SelectiveCheckSession {
    private sealed class Reservation {
        public ulong High;
        public ulong? Pending;
        public bool SentOrUncertain;
        public CheckRequest? Request;
    }
    private readonly Dictionary<string, Reservation> reservations = new Dictionary<string, Reservation>(StringComparer.Ordinal);
    private CheckCommand? urgentOff;
    private CheckCommand? urgentFinish;
    private bool workflowBusy, autoCapabilities;
    private ulong? uncertainId, uncertainFinishTarget;
    private readonly Queue<CheckCommand> queue = new Queue<CheckCommand>();
    private readonly List<CheckResult> history = new List<CheckResult>();
    private readonly List<HumanCheckAnswer> answers = new List<HumanCheckAnswer>();
    private CommandCompatibility? compatibility;
    private TelemetryPacket? packet;
    private Reservation? reservation;
    private CheckCapabilities? capabilities;
    private bool serial, active, relayAuthorized;
    private long generation, revision, pendingTick;
    private string message = "Ожидание источника; команды запрещены";
    public void Configure(CommandCompatibility? profile) { compatibility = profile; }
    private string Identity => packet is null ? "" : packet.DeviceId + "/" + packet.BootId;
    private bool Compatible => active && serial && packet is not null && compatibility is not null
        && packet.ConfigVersion == compatibility.Config && compatibility.FirmwareVersions.Contains(packet.FirmwareVersion)
        && (compatibility.DeviceId is null || compatibility.DeviceId == packet.DeviceId);
    public uint ProtocolVersion => packet is null || compatibility is null ? 1 : compatibility.Protocol(packet.FirmwareVersion);
    public void ClearUncertainty() { uncertainId = uncertainFinishTarget = null; }
    public void SetWorkflowBusy(bool value) { workflowBusy = value; revision++; }
    public CheckSessionState State() => new CheckSessionState(
        reservation?.Pending is ulong id && pendingTick != 0 && Stopwatch.GetElapsedTime(pendingTick).TotalSeconds > 10
            ? "Ответ не завершен для ID " + id + "; запросите status. Тайм-аут не останавливает действие на плате" : message,
        Compatible, Compatible && capabilities is not null, relayAuthorized, reservation?.Pending,
        history.ToImmutableArray(), answers.ToImmutableArray(), capabilities, revision, Identity) { ProtocolVersion = ProtocolVersion, UncertainId = uncertainId, UncertainFinishTarget = uncertainFinishTarget, WorkflowBusy = workflowBusy };
    public void Begin(bool isSerial) {
        Invalidate(); serial = isSerial; active = true; packet = null; reservation = null;
        message = isSerial ? "До валидной v1 команды запрещены" : "Запись: отправка команд запрещена";
    }
    public void Invalidate() {
        generation++;
        if (reservation is not null && !reservation.SentOrUncertain) reservation.Pending = null;
        queue.Clear(); urgentOff = urgentFinish = null; autoCapabilities = false; uncertainId = null; active = false; relayAuthorized = false; capabilities = null; revision++;
        message = "Источник закрыт; остановка действия на плате не подтверждена";
    }
    public void Observe(TelemetryPacket next) {
        if (packet is null || packet.DeviceId != next.DeviceId || packet.BootId != next.BootId
            || packet.FirmwareVersion != next.FirmwareVersion || packet.ConfigVersion != next.ConfigVersion) {
            generation++;
            if (reservation is not null && !reservation.SentOrUncertain) reservation.Pending = null;
            queue.Clear(); urgentOff = urgentFinish = null; autoCapabilities = false; uncertainId = null; capabilities = null; relayAuthorized = false;
            string key = next.DeviceId + "/" + next.BootId;
            if (!reservations.TryGetValue(key, out reservation) && reservations.Count < 64) {
                reservation = new Reservation(); reservations.Add(key, reservation);
            }
            packet = next;
            message = serial ? "Получена v1; запросите возможности совместимой прошивки" : "Исторические данные записи; команды запрещены";
            revision++;
        }
        packet = next;
        if (Compatible && ProtocolVersion == 2 && !autoCapabilities) {
            autoCapabilities = true;
            Enqueue("esp32", "capabilities", null, null, out _);
        }
    }
    public bool CancelUnsent(ulong id) {
        bool removed = false;
        int count = queue.Count;
        for (int i = 0; i < count; i++) {
            CheckCommand pending = queue.Dequeue();
            if (pending.Request.Id == id) removed = true; else queue.Enqueue(pending);
        }
        if (removed && reservation?.Pending == id && !reservation.SentOrUncertain) reservation.Pending = null;
        return removed;
    }
    public void AuthorizeRelay(bool value) { relayAuthorized = value && Compatible && capabilities is not null; revision++; }
    public bool Enqueue(string source, string operation, uint? duration, ulong? target, out string error, uint? maxHold = null) {
        error = "";
        if (!Compatible) { error = "Команды запрещены: нужен текущий serial/v1 и известная совместимая версия прошивки"; return false; }
        if (queue.Count >= 8 && operation is not ("all_off" or "finish_display")) { error = "Очередь команд заполнена"; return false; }
        bool finish = operation == "finish_display" && ProtocolVersion == 2;
        bool service = operation is "capabilities" or "status" or "all_off" || finish;
        if (service && (finish ? source != "oled" : source != "esp32")) { error = "Служебная команда только esp32"; return false; }
        if (finish && (target is null || !(reservation?.Pending == target && reservation.Request?.Operation == "display_hold")
            && !history.Any(r => r.Event.Device == packet!.DeviceId && r.Event.Boot == packet.BootId && r.Event.RequestId == target && r.Event.Operation == "display_hold"))) {
            error = "finish требует известный OLED target текущего boot"; return false;
        }
        if (!service) {
            if (workflowBusy) { error = "Завершите активную управляемую проверку"; return false; }
            SourceCapability? supported = capabilities?.Sources.FirstOrDefault(s => s.Source == source);
            if (reservation is null || supported is null || !supported.Initialized || !supported.Operations.Contains(operation)) { error = "Операция недоступна; нужны актуальные capabilities/initialized"; return false; }
            if (reservation.Pending.HasValue || capabilities!.Busy) { error = "Одна проверка уже ожидается; используйте status исходного ID"; return false; }
            if (operation == "pulse" && !relayAuthorized) { error = "Сначала подтвердите готовность и текущие условия нагрузки"; return false; }
            if (reservation.High == ulong.MaxValue) { error = "Номера UInt64 исчерпаны; status/all_off доступны"; return false; }
        }
        // номер резервируем навсегда до помещения в очередь, даже отказ отправки не разрешает его переиспользовать
        ulong id = service ? 0 : reservation!.High + 1;
        CheckRequest request = new CheckRequest(id, packet!.BootId, source, operation, duration, target, ProtocolVersion, maxHold);
        try { CheckProtocol.Encode(request); }
        catch (InvalidDataException exception) { error = exception.Message; return false; }
        if (!service) { reservation!.High = id; reservation.Pending = id; reservation.SentOrUncertain = false; reservation.Request = request; uncertainId = null; pendingTick = Stopwatch.GetTimestamp(); }
        CheckCommand command = new CheckCommand(request, generation, packet.DeviceId, packet.FirmwareVersion, packet.ConfigVersion);
        // отдельное место для явно запрошенного all_off не занято обычными read-only запросами
        if (operation == "all_off") {
            relayAuthorized = false;
            int queued = queue.Count;
            for (int index = 0; index < queued; index++) {
                CheckCommand waiting = queue.Dequeue();
                if (waiting.Request.Operation != "pulse") queue.Enqueue(waiting);
                // снимаем только еще не переданный pulse; верхняя бронь ID и отправленный pending сохраняются
                else if (reservation?.Pending == waiting.Request.Id && !reservation.SentOrUncertain) reservation.Pending = null;
            }
            urgentOff = command;
        }
        else if (finish) urgentFinish = command;
        else queue.Enqueue(command);
        message = "В очереди: " + operation + " ID " + id; revision++; return true;
    }
    public bool TryTake(out CheckCommand? command) {
        while (urgentOff is not null || urgentFinish is not null || queue.Count > 0) {
            CheckCommand next;
            if (urgentOff is not null) { next = urgentOff; urgentOff = null; }
            else if (urgentFinish is not null) { next = urgentFinish; urgentFinish = null; }
            else next = queue.Dequeue();
            if (Compatible && next.Generation == generation && next.Request.Boot == packet!.BootId
                && next.Device == packet.DeviceId && next.Firmware == packet.FirmwareVersion && next.Config == packet.ConfigVersion && next.Request.ProtocolVersion == ProtocolVersion) {
                if (next.Request.Id > 0 && (capabilities is null || !capabilities.Sources.Any(s => s.Source == next.Request.Source && s.Initialized)
                    || next.Request.Operation == "pulse" && !relayAuthorized)) {
                    if (reservation?.Pending == next.Request.Id) reservation.Pending = null;
                    message = "Команда снята до отправки: разрешение или доступность изменились; ID остается зарезервированным";
                    revision++; continue;
                }
                if (next.Request.Id > 0 && reservation is not null) reservation.SentOrUncertain = true;
                command = next; return true;
            }
        }
        command = null; return false;
    }
    public void Sent(CheckCommand command, string? failure) {
        if (failure is not null && command.Request.Operation == "finish_display") uncertainFinishTarget = command.Request.Target;
        if (failure is not null && command.Request.Id > 0) uncertainId = command.Request.Id;
        message = failure is null ? "Отправлено " + command.Request.Operation + " ID " + command.Request.Id + "; ожидается TEST_EVENT"
            : "Отправка частичная или неопределенная: " + failure + ". Не повторяйте действие; запросите status ID " + command.Request.Id;
        revision++;
    }
    public bool Accept(CheckEvent e, string origin, out string error) {
        error = "";
        if (packet is null || serial && e.ProtocolVersion != ProtocolVersion || e.Device != packet.DeviceId || e.Boot != packet.BootId || e.Firmware != packet.FirmwareVersion || e.Config != packet.ConfigVersion) {
            error = "TEST_EVENT не совпадает с текущей v1 identity/boot/firmware/config"; return false;
        }
        if (e.RequestId > 0 && reservation?.Pending == e.RequestId && reservation.Request is CheckRequest expected) {
            if (e.Source != expected.Source || e.Operation != expected.Operation
                || expected.MaxHold.HasValue && e.Data.TryGetProperty("max_hold_ms", out JsonElement hold) && hold.GetUInt64() != expected.MaxHold.Value
                || expected.Duration.HasValue && e.Data.TryGetProperty("requested_duration_ms", out JsonElement duration) && duration.GetUInt64() != expected.Duration.Value) {
                error = "TEST_EVENT не совпадает с параметрами зарезервированного запроса"; return false;
            }
        }
        if (e.Kind == "capabilities") {
            if (!serial || Compatible) capabilities = e.Capabilities;
        }
        else {
            int index = e.RequestId is > 0 ? history.FindIndex(r => r.Event.Key == e.Key && r.Origin == origin) : -1;
            if (index >= 0) {
                CheckEvent old = history[index].Event;
                bool same = old.Source == e.Source && old.Operation == e.Operation && old.Accepted == e.Accepted
                    && (!old.Started.HasValue || old.Started == e.Started);
                if (old.Terminal) {
                    // status и replayed возвращают сохраненный итог, но не освежают его время получения
                    if (!same || old.Stage != e.Stage || old.Finished != e.Finished || old.Reason != e.Reason
                        || old.ErrorCode != e.ErrorCode || !JsonElement.DeepEquals(old.Data, e.Data)) {
                        error = "TEST_EVENT пытается заменить terminal или исторические времена"; return false;
                    }
                    UpdateWatermark(e); return true;
                }
                if (old.Operation == "display_hold" && old.Data.TryGetProperty("shown_ms", out JsonElement shown) && shown.ValueKind != JsonValueKind.Null) {
                    if (!e.Data.TryGetProperty("shown_ms", out JsonElement nextShown) || !JsonElement.DeepEquals(shown, nextShown)
                        || !e.Data.TryGetProperty("deadline_ms", out JsonElement nextDeadline) || !JsonElement.DeepEquals(old.Data.GetProperty("deadline_ms"), nextDeadline)) {
                        error = "Повтор меняет готовность/срок OLED"; return false;
                    }
                }
                if (!same || old.Stage == "running" && e.Stage == "accepted") { error = "Регрессия стадии/времени TEST_EVENT"; return false; }
                history[index] = new CheckResult(e, origin, history[index].ReceivedAt);
            }
            else {
                history.Add(new CheckResult(e, origin, DateTimeOffset.UtcNow));
                if (history.Count > 64) history.RemoveAt(0);
            }
        }
        UpdateWatermark(e);
        message = (serial ? "Получено " : "Запись: ") + e.Kind + " / " + e.Stage + " / " + e.Reason;
        revision++; return true;
    }
    private void UpdateWatermark(CheckEvent e) {
        if (reservation is null) return;
        reservation.High = Math.Max(reservation.High, e.UpperId);
        if (serial && active && e.RequestId == reservation.Pending) uncertainId = null;
        if (serial && active && e.Operation == "finish_display" && e.Target == uncertainFinishTarget) uncertainFinishTarget = null;
        if (serial && active && e.RequestId == reservation.Pending && e.Terminal) {
            reservation.Pending = null; uncertainId = uncertainFinishTarget = null;
            if (capabilities is not null) capabilities = capabilities with { Busy = false };
        }
        if (e.Kind == "capabilities" && e.Capabilities is not null && reservation.Pending.HasValue)
            message = "После reconnect запросите status ID " + reservation.Pending.Value;
        if (capabilities is not null && e.Kind == "lifecycle" && !e.Replayed)
            capabilities = capabilities with { Busy = !e.Terminal };
    }
    public void Answer(CheckResult result, string answer, string method) {
        CheckProtocol.Require(result.Event.RequestId is > 0 && result.Event.HumanConfirmation is not null && result.Event.Terminal
            && result.Event.Stage != "rejected", "нет результата для вопроса человеку");
        CheckProtocol.Require(new[] { "Да", "Нет", "Неясно", "Не проверял" }.Contains(answer) && !string.IsNullOrWhiteSpace(method) && method.Length <= 300, "ответ/способ проверки");
        CheckEvent e = result.Event;
        answers.RemoveAll(a => a.Device == e.Device && a.Boot == e.Boot && a.RequestId == e.RequestId && a.Source == e.Source);
        answers.Add(new HumanCheckAnswer(e.Device, e.Boot, e.RequestId!.Value, e.Source!, answer, DateTimeOffset.UtcNow, method));
        if (answers.Count > 128) answers.RemoveAt(0);
        revision++;
    }
}
