using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Knowledge;
using IotExpertApp.Services.Live;
using IotExpertApp.Services.Telemetry;
using IotExpertApp.ViewModels;

namespace IotExpertApp.ConsoleUI;

public static class OnlineKnowledgeChecks {
    public static void Run(string syntheticJson) {
        DeviceProfile profile = new DeviceProfileService().Load(Path.Combine(AppContext.BaseDirectory, "DeviceProfiles", "esp32-observability.json"));
        TelemetryParser parser = new TelemetryParser();
        Check(parser.TryParse(syntheticJson, out TelemetryPacket? parsed, out string error), error);
        TelemetryPacket packet = parsed! with { DeviceId = profile.DeviceId };
        ReceiverState state = new ReceiverState("прием данных", "synthetic-step12", 1, 0, 0, 0, 0, 0, 0, "",
            new ReceivedSnapshot(packet, DateTimeOffset.UtcNow, "synthetic-step12"), 0, true, false, false, "", "") { IsSerial = true };
        OnlineDataViewModel online = new OnlineDataViewModel();
        online.Update(state with { Snapshot = null, Running = false }, null, TimeSpan.Zero);
        Check(online.Sources.All(t => t.Values == "Неизвестно") && online.Mode == "Еще нет данных", "начальное состояние без нулей");
        online.Update(state with { Snapshot = null }, profile, TimeSpan.Zero);
        Check(online.Mode == "Порт открыт, ожидаем данные", "открытый порт без пакета");
        online.Update(state, profile, TimeSpan.Zero);
        OnlineSourceViewModel[] tiles = online.Sources.ToArray();
        string initialAge = tiles[0].Freshness;
        online.Update(state, profile, TimeSpan.FromMilliseconds(profile.PacketTimeoutMs + 1));
        Check(online.Sources.SequenceEqual(tiles) && tiles[0].Freshness != initialAge && online.Mode == "Нет свежих пакетов", "тот же Snapshot: возраст растет, плитки прежние");
        Check(tiles[0].Status.Contains("устарели", StringComparison.Ordinal), "обязательный stale совпадает с Normalize");
        Check(!tiles[6].Status.Contains("устарели", StringComparison.Ordinal) && tiles[6].Freshness.Contains("исторический", StringComparison.Ordinal), "SD без выдуманного периода");
        Check(!tiles[7].Status.Contains("устарели", StringComparison.Ordinal), "реле без выдуманного периода");
        online.Update(state with { Running = false }, profile, TimeSpan.FromDays(500));
        Check(!tiles[0].Status.Contains("устарели", StringComparison.Ordinal), "Stop не добавляет hostAge к историческому статусу");
        Check(online.Mode.Contains("источник отключен", StringComparison.Ordinal), "Stop не live");
        online.Update(state with { TestErrors = 5, Oversized = 1, Tails = 1, Invalid = long.MaxValue, TemporalRejected = long.MaxValue }, profile, TimeSpan.Zero);
        Check(online.Counters.Contains("ошибок TEST_EVENT: 5", StringComparison.Ordinal) && online.Counters.Contains("неполных строк: 1", StringComparison.Ordinal), "ненулевые счетчики ошибок не скрыты и не суммируются с переполнением");
        ReceiverState replay = state with { IsSerial = false };
        foreach ((ReceiverState current, string mode) in new[] {
            (replay, "Запись: воспроизведение"), (replay with { ReplayPaused = true }, "Запись: пауза"),
            (replay with { Running = false, Completed = true }, "Запись: конец файла"), (replay with { Running = false }, "Запись: остановлена; последние полученные данные") }) {
            online.Update(current, profile, TimeSpan.FromDays(500)); Check(online.Mode == mode, mode);
            Check(!tiles[0].Status.Contains("устарели", StringComparison.Ordinal), "replay не стареет по хосту");
        }
        Observation dht = packet.Observations.Single(o => o.SourceId == "dht11");
        TelemetryPacket failure = packet with { Observations = packet.Observations.Select(o => o.SourceId == "dht11" ? dht with { Status = "error", ErrorDetail = "synthetic_timeout" } : o).ToArray() };
        online.Update(state with { Snapshot = new ReceivedSnapshot(failure, DateTimeOffset.UtcNow, state.Origin) }, profile, TimeSpan.Zero);
        Check(tiles[2].Status == "Ошибка последнего чтения" && tiles[2].Values.Contains("Последние успешные значения", StringComparison.Ordinal) && tiles[2].Error == "synthetic_timeout", "ошибка DHT не зеленит старый успех");
        Check(tiles[5].Values.Contains("Raw GPIO", StringComparison.Ordinal) && !tiles[5].Freshness.Contains("Возраст успеха", StringComparison.Ordinal), "PIR raw не успешное измерение");
        online.Update(state, null, TimeSpan.FromDays(500));
        Check(online.Policy.Contains("профиль не загружен", StringComparison.Ordinal) && online.Mode == "Живой поток", "без профиля нет запасного срока10с");
        online.Update(state, profile with { DeviceId = "другой" }, TimeSpan.FromDays(500));
        Check(online.Policy.Contains("не совпали", StringComparison.Ordinal), "чужая политика не применяется");
        TelemetryPacket large = packet with { UptimeMs = ulong.MaxValue, BootId = new string('b', 32), Observations = packet.Observations.Select(o => o with {
            LastAttemptMs = 0, LastSuccessMs = o.SourceId == "pir" ? null : 0, RawLevel = null }).ToArray() };
        online.Update(state with { Snapshot = new ReceivedSnapshot(large, DateTimeOffset.UtcNow, state.Origin) }, profile, TimeSpan.FromMilliseconds(2));
        Check(tiles[0].Freshness.Contains("18446744073709551617 мс", StringComparison.Ordinal), "UInt64 возраст плюс host без double/overflow");
        Check(tiles[5].Values.Contains("нет данных", StringComparison.Ordinal) && !tiles[5].Values.Contains("GPIO: 0", StringComparison.Ordinal), "null raw не ноль");
        Check(online.Details.Contains(large.BootId, StringComparison.Ordinal), "новый boot меняет общий контекст");
        using (JsonDocument zero = JsonDocument.Parse("{\"free_heap_bytes\":0,\"min_free_heap_bytes\":0}")) {
            TelemetryPacket zeroPacket = packet with { Observations = packet.Observations.Select(o => o.SourceId == "esp32" ? o with { Values = zero.RootElement.Clone() } : o).ToArray() };
            online.Update(state with { Snapshot = new ReceivedSnapshot(zeroPacket, DateTimeOffset.UtcNow, state.Origin) }, profile, TimeSpan.Zero);
            Check(tiles[0].Values.Contains("0 байт", StringComparison.Ordinal), "известный ноль не unknown");
        }
        online.Update(state with { Snapshot = null }, profile, TimeSpan.Zero);
        Check(online.Sources.All(t => t.Values == "Неизвестно"), "reconnect очищает все9 значений согласованно");
        CheckKnowledge(profile, state);
        Console.WriteLine("STEP12: онлайн/возрасты/UInt64/Stop/replay/ошибки; справочник/поиск/смена комплекта/вложенные условия и сохранение результатов проверены на искусственных входах; native/live этим не подтверждены");
    }
    private static void CheckKnowledge(DeviceProfile profile, ReceiverState state) {
        LiveMonitorViewModel live = new LiveMonitorViewModel { ProfilePath = profile.Path };
        live.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult(); live.Update(state); live.FreezeCommand.Execute(null);
        LiveAssessment? frozen = live.Frozen;
        DeviceProfile actual = live.Profile!;
        ProfileKnowledgeViewModel browser = new ProfileKnowledgeViewModel(live);
        browser.Synchronize(actual, false, live.ProfileLoadStatus, live.LoadMessage);
        Check(ReferenceEquals(browser.Groups[0].Knowledge, actual.DiagnosticKnowledge) && ReferenceEquals(browser.Groups[1].Knowledge, actual.StrategyKnowledge)
            && ReferenceEquals(browser.Groups[2].Knowledge, actual.ModuleKnowledge), "справочник использует тот же комплект, не второй loader");
        browser.SelectedKind = "Признаки";
        Check(browser.Entries.Any(e => e.Body.Contains("роль: вход", StringComparison.Ordinal)), "справочник показывает типы и роли признаков");
        browser.SelectedKind = "Результаты";
        Check(browser.Entries.Count == actual.DiagnosticKnowledge.Outcomes.Count && browser.Entries.All(e => e.Body.Contains("Ведущие правила", StringComparison.Ordinal)), "исходы имеют описания и реальные ведущие правила");
        browser.SelectedKind = "Правила";
        browser.SelectedGroup = browser.Groups[1]; browser.Search = "R-13";
        KnowledgeEntry first = browser.SelectedEntry!;
        browser.Synchronize(actual, false, "все еще загружен", "");
        Check(browser.Search == "R-13" && ReferenceEquals(browser.SelectedEntry, first), "новый пакет/статус не сбрасывает поиск и выбор");
        browser.Search = "нет такого правила step12"; Check(browser.Entries.Count == 0 && browser.Found.Contains("ничего не найдено", StringComparison.Ordinal), "явный пустой поиск");
        browser.ClearSearchCommand.Execute(null); Check(browser.Entries.Count == actual.StrategyKnowledge.Rules.Length, "поиск не меняет число правил");
        KnowledgeBase changed = actual.StrategyKnowledge with { Id = "synthetic-other", Version = "test2", Rules = actual.StrategyKnowledge.Rules.Where(r => r.Id == "R-13").ToImmutableArray() };
        DeviceProfile replacement = actual with { Title = "Другой тестовый профиль", StrategyKnowledge = changed, ModuleKnowledge = null };
        browser.Search = "R-13"; browser.Synchronize(replacement, false, "заменен", "");
        Check(browser.SelectedEntry?.Id == "R-13" && browser.Entries.Count == 1 && browser.Groups[1].Summary.Contains("test2", StringComparison.Ordinal) && browser.ProfileSummary.Contains(replacement.Title, StringComparison.Ordinal), "новый комплект отражен без констант и сохраняет ID");
        browser.SelectedGroup = browser.Groups[2]; Check(browser.Found == "БЗ выборочных проверок не подключена", "старый профиль без module");
        browser.Synchronize(actual, false, live.ProfileLoadStatus, live.LoadMessage);
        browser.SelectedGroup = browser.Groups[0]; browser.Search = "OBS-01";
        first = browser.SelectedEntry!;
        browser.OpenEditorCommand.Execute(null); browser.OpenReferenceCommand.Execute(null);
        Check(!browser.IsEditorOpen && browser.SelectedEntry == first && ReferenceEquals(live.Frozen, frozen) && ReferenceEquals(live.Profile, actual), "переход не активирует/не заменяет профиль/Frozen");
        string missing = Path.Combine(Path.GetTempPath(), "iot-step12-missing-" + Guid.NewGuid().ToString("N") + ".json");
        live.ProfilePath = missing; live.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        browser.Synchronize(live.Profile, live.IsLoading, live.ProfileLoadStatus, live.LoadMessage);
        Check(ReferenceEquals(browser.SelectedEntry, first) && ReferenceEquals(live.Frozen, frozen) && browser.LoadStatus.Contains("не заменен", StringComparison.Ordinal), "ошибка загрузки сохраняет комплект/выбор/Frozen");
        Check(!File.Exists(missing), "просмотр/переход не записывают файл");
        FactDefinition input = new FactDefinition("X", "Тестовый вход", FactType.Boolean, FactRole.Input, ImmutableArray<string>.Empty, null, null, null);
        FactDefinition derived = input with { Id = "D", Name = "Промежуточный результат", Role = FactRole.Derived };
        KnowledgeBase custom = changed with { Facts = ImmutableDictionary<string, FactDefinition>.Empty.Add("X", input).Add("D", derived) };
        Condition nested = new AllCondition(ImmutableArray.Create<Condition>(new ComparisonCondition("X", "eq", new KnowledgeValue(FactType.Boolean, "true")),
            new AnyCondition(ImmutableArray.Create<Condition>(new ComparisonCondition("X", "eq", new KnowledgeValue(FactType.Boolean, "false")),new ComparisonCondition("D", "eq", new KnowledgeValue(FactType.Boolean, "true"))))));
        KnowledgeRule rule = new KnowledgeRule("SYN12", "Тест групп", 1, nested, ImmutableArray.Create(new Conclusion("D", new KnowledgeValue(FactType.Boolean, "true"))), "тест", "synthetic");
        string text = KnowledgeTextFormatter.Rule(custom, rule);
        Check(text.Contains("ВСЕ условия (И) (\n  X · Тестовый вход", StringComparison.Ordinal) && text.Contains("  ХОТЯ БЫ ОДНО (ИЛИ) (\n    X", StringComparison.Ordinal)
            && text.Contains("\n  )\n)", StringComparison.Ordinal) && text.Contains("D · Промежуточный результат = да", StringComparison.Ordinal), "рекурсивный И/ИЛИ и именованный промежуточный вывод");
        Check(actual.StrategyKnowledge.Rules.Length == profile.StrategyKnowledge.Rules.Length, "поиск/форматтер не меняют KnowledgeBase");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
