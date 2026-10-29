using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Checks;

// работаем под gate приемника: событие, ответ и deadline меняют один immutable-снимок атомарно
// начало ответа задает подтвержденная готовность, а не новый пакет v1 или время устройства
// одна фиксация ответного исхода ставит один адресный finish; terminal машины и ответ человека храним раздельно
// ручной status создает помеченную редакцию неопределенного итога, исходная история остается неизменной
public sealed class GuidedCheckCoordinator {
    private readonly SelectiveCheckSession session;
    private readonly Func<TimeSpan> now;
    private readonly List<GuidedCheckResult> history = new List<GuidedCheckResult>();
    public GuidedCheckResult? Current { get; private set; }
    public ImmutableArray<GuidedCheckResult> History => history.ToImmutableArray();
    public string Message { get; private set; } = "Выберите модуль и начните проверку";
    public TimeSpan Now => now();
    public GuidedCheckCoordinator(SelectiveCheckSession session, Func<TimeSpan>? clock = null) {
        this.session = session;
        long started = Stopwatch.GetTimestamp();
        now = clock ?? (() => Stopwatch.GetElapsedTime(started));
    }
    public bool Start(string source, int seconds, uint window, KnowledgeBase? knowledge, ReceiverState state) {
        if (Current?.Active == true) { Message = "Завершите активную проверку; ее вопрос и срок сохранены"; return false; }
        CheckPreferences.Parse(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        TelemetryPacket? packet = state.Snapshot?.Packet;
        if (!state.Running || !state.IsSerial || packet is null) { Message = "Нужен текущий serial; запись не запускает новую проверку"; return false; }
        uint protocol = session.ProtocolVersion;
        if (source == "oled" && protocol != 2) { Message = "Для проверки с ожиданием ответа обновите прошивку"; return false; }
        session.SetWorkflowBusy(false);
        string operation = CheckProtocol.Operation(source, protocol);
        if (!session.Enqueue(source, operation, operation == "observe" ? window : null, null, out string error,
            operation == "display_hold" ? checked((uint)(seconds + 10) * 1000) : null)) { Message = error; return false; }
        ulong id = session.State().PendingId!.Value;
        Current = new GuidedCheckResult(packet.DeviceId, packet.BootId, packet.FirmwareVersion, packet.ConfigVersion, id, source, operation,
            state.Origin, protocol, seconds, operation == "observe" ? window : null, knowledge,
            "starting", "pending", now(), now() + TimeSpan.FromSeconds(12), null, null, null, false, null);
        session.SetWorkflowBusy(true);
        Recompute(); Message = "Запрос " + id + ": ожидаем запуск"; return true;
    }
    public void Tick(ReceiverState state) {
        GuidedCheckResult? current = Current;
        if (current is null || !current.Active) return;
        TelemetryPacket? packet = state.Snapshot?.Packet;
        if (!state.IsSerial || !state.Running || packet is null || packet.DeviceId != current.Device || packet.BootId != current.Boot
            || packet.FirmwareVersion != current.Firmware || packet.ConfigVersion != current.Config) {
            Interrupt(); return;
        }
        if (session.State().UncertainFinishTarget == current.RequestId) { Finish("uncertain", "uncertain", "Отправка finish неопределенна; запросите status исходного OLED, не новый display_hold"); return; }
        if (session.State().UncertainId == current.RequestId) { Finish("uncertain", "uncertain", "Отправка неопределенна; запросите status исходного ID"); return; }
        if (current.Phase == "answer" && now() >= current.AnswerDeadline) EndResponse("timeout", null);
        else if (current.Phase is "starting" or "machine" or "finishing" && now() >= current.TransportDeadline)
            Finish("uncertain", "uncertain", "Ответ устройства не подтвержден; status исходного ID, watchdog OLED конечен");
        else if (current.Operation == "pulse" && current.Phase == "starting" && session.State().PendingId is null && current.Machine is null)
            Finish("interrupted", "interrupted", "Pulse снят до отправки, новый запуск требует нового разрешения");
    }
    public void Event(CheckEvent e) {
        GuidedCheckResult? current = Current;
        if (current is null || !current.Active || e.Device != current.Device || e.Boot != current.Boot || e.Firmware != current.Firmware) return;
        if (e.Operation == "finish_display" && e.Target == current.RequestId && e.Stage == "rejected") {
            Finish("uncertain", "uncertain", "Завершение OLED не подтверждено: " + e.Reason + "; используйте status"); return;
        }
        if (e.RequestId != current.RequestId || e.Source != current.Source || e.Operation != current.Operation) return;
        if (ReferenceEquals(current.Machine, e)) return;
        Current = current with { Machine = e };
        if (e.Stage is "failed" or "rejected" or "cancelled") {
            Finish("finished", e.Stage == "cancelled" ? "interrupted" : current.ResponseOutcome == "pending" ? "machine" : current.ResponseOutcome,
                "Машинная операция: " + e.Stage + " / " + e.Reason); return;
        }
        if (current.Source == "oled") {
            if (e.Terminal) {
                if (current.ResponseOutcome == "pending") Finish("finished", "early_end", "OLED завершился до ответа: " + e.Reason + "; рисунок уже не удерживается");
                else Finish("finished", current.ResponseOutcome, "Получен terminal OLED; восстановление оценивается по данным");
                return;
            }
            if (e.Data.TryGetProperty("transfer_ok", out System.Text.Json.JsonElement ready) && ready.ValueKind == System.Text.Json.JsonValueKind.True) {
                if (current.AnswerDeadline is null && current.ResponseOutcome == "pending") {
                    Current = Current! with { Phase = "answer", AnswerDeadline = now() + TimeSpan.FromSeconds(current.ResponseSeconds) };
                    Message = "Рисунок передан; ожидаем ответ человека";
                }
            }
            else if (current.Phase == "starting") { Current = Current! with { Phase = "machine" }; Message = "Ожидаем подтвержденный показ рисунка"; }
        }
        else if (e.Stage == "completed") {
            if (CheckProtocol.Human(current.Source) is null) { Finish("finished", "machine", "Машинная операция завершена"); return; }
            if (current.AnswerDeadline is null) {
                Current = Current! with { Phase = "answer", AnswerDeadline = now() + TimeSpan.FromSeconds(current.ResponseSeconds) };
                Message = "Операция завершена; ответьте о выбранном окне/импульсе";
            }
        }
        else { Current = Current! with { Phase = "machine" }; Message = current.Operation == "observe" ? "Идет измерительное окно: выполните инструкцию" : "Машинная операция выполняется"; }
        Recompute();
    }
    public bool Answer(string answer) {
        GuidedCheckResult? current = Current;
        if (current is null || current.Phase != "answer" || current.ResponseOutcome != "pending") { Message = "Ответ уже закрыт или еще не начат"; return false; }
        if (now() >= current.AnswerDeadline) { EndResponse("timeout", null); return false; }
        CheckProtocol.Require(new[] { "Да", "Нет", "Неясно", "Не проверял" }.Contains(answer), "неизвестный ответ");
        HumanCheckAnswer human = new HumanCheckAnswer(current.Device, current.Boot, current.RequestId, current.Source, answer,
            DateTimeOffset.UtcNow, "Явная кнопка ответа в управляемой проверке");
        EndResponse("answered", human); return true;
    }
    private void EndResponse(string outcome, HumanCheckAnswer? human) {
        if (Current is null || Current.ResponseOutcome != "pending") return;
        Current = Current with { EndReason = outcome, ResponseOutcome = outcome, Human = human };
        if (Current.Source == "oled") {
            Current = Current with { Phase = "finishing", TransportDeadline = now() + TimeSpan.FromSeconds(12) };
            RequestFinish(); Recompute();
        }
        else Finish("finished", outcome, outcome == "timeout" ? "Пользователь не подтвердил: ответ не получен за " + Current.ResponseSeconds + " с" : "Ответ сохранен");
    }
    private void RequestFinish() {
        if (Current is null || Current.Source != "oled" || Current.ProtocolVersion != 2 || Current.FinishRequested) return;
        Current = Current with { FinishRequested = true };
        if (!session.Enqueue("oled", "finish_display", null, Current.RequestId, out string error)) {
            Finish("uncertain", "uncertain", "Не удалось запросить возврат OLED: " + error + "; действует watchdog");
        }
        else Message = "Ожидаем подтвержденное восстановление OLED; не запускайте новый display_hold";
    }
    public void PrepareStop() {
        if (Current?.Active != true) return;
        if (Current.Source == "oled") {
            if (session.CancelUnsent(Current.RequestId)) { Finish("interrupted", "interrupted", "OLED снят до отправки; ID остается зарезервированным"); return; }
            Current = Current with { Phase = "finishing", ResponseOutcome = "interrupted", EndReason = "interrupted" };
            RequestFinish();
        }
    }
    public void Interrupt() {
        if (Current?.Active == true) Finish("interrupted", "interrupted", "Источник прерван; возврат OLED не подтвержден, действует watchdog прошивки");
    }
    public void Recover(ReceiverState state) {
        if (Current is null || Current.Active || Current.EndReason != "uncertain") return;
        if (!state.IsSerial || !state.Running || state.Snapshot?.Packet.DeviceId != Current.Device || state.Snapshot?.Packet.BootId != Current.Boot) {
            Message = "Status не восстанавливает проверку другого boot/соединения"; return;
        }
        if (!session.Enqueue("esp32", "status", null, Current.RequestId, out string error)) { Message = error; return; }
        session.ClearUncertainty();
        Current = Current with { Phase = Current.FinishRequested ? "finishing" : "machine", EndReason = Current.ResponseOutcome,
            TransportDeadline = now() + TimeSpan.FromSeconds(12), Revision = Current.Revision + 1 };
        session.SetWorkflowBusy(true); Recompute(); Message = "Новая редакция восстановления по status; исходная неопределенная запись сохранена";
    }
    public void RetryFinish() {
        if (Current?.Active != true || Current.Phase != "finishing" || Current.Machine?.Kind != "status" || Current.Machine.Stage != "running") return;
        Current = Current with { FinishRequested = false, TransportDeadline = now() + TimeSpan.FromSeconds(12) };
        RequestFinish();
    }
    private void Recompute() { if (Current is not null) Current = Current with { Inference = ModuleCheckAssessment.Run(Current) }; }
    private void Finish(string phase, string reason, string message) {
        if (Current is null) return;
        Current = Current with { Phase = phase, EndReason = reason }; Recompute();
        history.Add(Current!); if (history.Count > 32) history.RemoveAt(0);
        session.SetWorkflowBusy(false); Message = message;
    }
}
