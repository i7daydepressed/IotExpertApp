using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ViewModels;

public sealed record KnowledgeEntry(string Id, string Title, string Body, string Technical);
public partial class KnowledgeGroupViewModel : ViewModelBase {
    public string Title { get; }
    public KnowledgeBase? Knowledge { get; private set; }
    [ObservableProperty] private string summary = "БЗ не загружена";
    public KnowledgeGroupViewModel(string title) { Title = title; }
    public void Set(KnowledgeBase? knowledge, bool strategy = false) {
        Knowledge = knowledge;
        Summary = knowledge is null ? "БЗ не подключена" : knowledge.Id + " · " + knowledge.Version + " · правил " + knowledge.Rules.Length
            + "; входов " + knowledge.Facts.Values.Count(f => f.Role == FactRole.Input) + "; производных " + knowledge.Facts.Values.Count(f => f.Role == FactRole.Derived)
            + "; исходов " + knowledge.Outcomes.Count
            + (strategy ? "; R-правил " + knowledge.Rules.Count(r => r.Id.StartsWith("R-", StringComparison.Ordinal)) + "; D-правил " + knowledge.Rules.Count(r => r.Id.StartsWith("D-", StringComparison.Ordinal)) : "");
    }
}

public partial class ProfileKnowledgeViewModel : ViewModelBase {
    public LiveMonitorViewModel Live { get; }
    public IAsyncRelayCommand LoadCommand => Live.ReloadCommand;
    public ObservableCollection<KnowledgeGroupViewModel> Groups { get; } = new ObservableCollection<KnowledgeGroupViewModel>();
    public ObservableCollection<KnowledgeEntry> Entries { get; } = new ObservableCollection<KnowledgeEntry>();
    public string[] Kinds { get; } = { "Правила", "Признаки", "Результаты" };
    private DeviceProfile? profile;
    [ObservableProperty] private KnowledgeGroupViewModel? selectedGroup;
    [ObservableProperty] private string selectedKind = "Правила";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private KnowledgeEntry? selectedEntry;
    [ObservableProperty] private string profileSummary = "Профиль еще не загружен";
    [ObservableProperty] private string loadStatus = "Профиль еще не загружен";
    [ObservableProperty] private string found = "Загрузите профиль для просмотра знаний";
    [ObservableProperty] private string selectedTitle = "Выберите элемент списка";
    [ObservableProperty] private string selectedBody = "Это просмотр правил вообще, не трасса конкретного расчета";
    [ObservableProperty] private string selectedTechnical = "";
    [ObservableProperty] private string policyMemo = "После загрузки здесь появятся критерии текущего профиля";
    [ObservableProperty] private string profileDetails = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsReferenceOpen))] private bool isEditorOpen;
    public bool IsReferenceOpen => !IsEditorOpen;
    public ProfileKnowledgeViewModel(LiveMonitorViewModel live) {
        Live = live;
        Groups.Add(new KnowledgeGroupViewModel("Наблюдаемость стенда"));
        Groups.Add(new KnowledgeGroupViewModel("Выбор стратегии"));
        Groups.Add(new KnowledgeGroupViewModel("Выборочные проверки"));
        SelectedGroup = Groups[0];
    }
    public void Synchronize(DeviceProfile? replacement, bool loading, string status, string message) {
        LoadStatus = loading ? "Загрузка профиля и правил…" : status + (message.StartsWith("Профиль не заменен:", StringComparison.Ordinal) ? "\n" + message : "");
        if (ReferenceEquals(profile, replacement)) return;
        profile = replacement;
        Groups[0].Set(profile?.DiagnosticKnowledge); Groups[1].Set(profile?.StrategyKnowledge, true); Groups[2].Set(profile?.ModuleKnowledge);
        ProfileSummary = profile is null ? "Профиль еще не загружен" : profile.Title + " · " + profile.Version + "\n" + profile.Scope;
        PolicyMemo = profile is null ? "После загрузки здесь появятся критерии текущего профиля" : Memo(profile);
        ProfileDetails = profile is null ? "" : profile.Path + "\nID " + profile.Id + "; device " + profile.DeviceId + "; config " + profile.ConfigVersion + "\nSHA256 " + profile.Sha256
            + "\nDefaults: " + string.Join("; ", profile.Defaults.Select(p => p.Key + "=" + KnowledgeTextFormatter.Value(p.Value)))
            + "\nMappings:\n" + string.Join("\n", profile.Mappings.Select(m => m.Key + ": " + string.Join("; ", m.Value.Select(v => v.Key + "=" + KnowledgeTextFormatter.Value(v.Value)))))
            + "\n" + string.Join("\n", Groups.Where(g => g.Knowledge is not null).Select(g => g.Title + ": " + g.Knowledge!.Id + " / SHA256 " + g.Knowledge.Sha256));
        Rebuild();
    }
    partial void OnSearchChanged(string value) => Rebuild();
    partial void OnSelectedKindChanged(string value) => Rebuild();
    partial void OnSelectedGroupChanged(KnowledgeGroupViewModel? value) => Rebuild(false);
    partial void OnSelectedEntryChanged(KnowledgeEntry? value) {
        SelectedTitle = value?.Title ?? "Выберите элемент списка";
        SelectedBody = value?.Body ?? "Это просмотр правил вообще, не трасса конкретного расчета";
        SelectedTechnical = value?.Technical ?? "";
    }
    [RelayCommand] private void ClearSearch() => Search = "";
    [RelayCommand] private void OpenEditor() => IsEditorOpen = true;
    [RelayCommand] private void OpenReference() => IsEditorOpen = false;
    private void Rebuild(bool preserve = true) {
        KnowledgeBase? knowledge = SelectedGroup?.Knowledge;
        string? previous = preserve ? SelectedEntry?.Id : null;
        KnowledgeEntry[] entries = knowledge is null ? Array.Empty<KnowledgeEntry>() : SelectedKind switch {
            "Признаки" => knowledge.Facts.Values.OrderBy(f => f.Id, StringComparer.Ordinal).Select(f => new KnowledgeEntry(f.Id, f.Id + " · " + f.Name, KnowledgeTextFormatter.Fact(f), "Тип " + f.Type + "; роль " + f.Role)).ToArray(),
            "Результаты" => knowledge.Outcomes.Values.OrderBy(o => o.Value, StringComparer.Ordinal).Select(o => new KnowledgeEntry(o.Value, o.Value + " · " + o.Title,
                KnowledgeTextFormatter.Outcome(o) + "\nВедущие правила: " + string.Join(", ", knowledge.Rules.Where(r => r.Conclusions.Any(c => knowledge.Facts[c.FactId].Role == FactRole.Goal && c.Value.Canonical == o.Value)).Select(r => r.Id)), "Kind=" + o.Kind)).ToArray(),
            _ => knowledge.Rules.Select(r => new KnowledgeEntry(r.Id, r.Id + " · " + r.Title, KnowledgeTextFormatter.Rule(knowledge, r), "Priority=" + r.Priority)).ToArray()
        };
        string query = Search.Trim();
        Entries.Clear();
        foreach (KnowledgeEntry entry in entries.Where(e => query.Length == 0 || (e.Title + "\n" + e.Body).Contains(query, StringComparison.OrdinalIgnoreCase))) Entries.Add(entry);
        Found = knowledge is null ? profile is null ? "Загрузите профиль для просмотра знаний" : "БЗ выборочных проверок не подключена"
            : Entries.Count == 0 ? "По запросу ничего не найдено" : "Найдено: " + Entries.Count + " из " + entries.Length;
        SelectedEntry = Entries.FirstOrDefault(e => e.Id == previous) ?? Entries.FirstOrDefault();
    }
    private static string Memo(DeviceProfile profile) => "Область наблюдаемости: " + profile.Scope
        + "\nПредел возраста пакета: " + profile.PacketTimeoutMs + " мс; обязательные источники: " + string.Join(", ", profile.Sources.Select(s => s.SourceId + " ≤ " + s.MaximumAgeMs + " мс"))
        + "\nДиагностические исходы и отображение в общую БЗ:\n" + string.Join("\n", profile.DiagnosticKnowledge.Outcomes.Values.Select(o => o.Value + " · " + o.Title + ": " + o.Description
            + (profile.Mappings.TryGetValue(o.Value, out System.Collections.Immutable.ImmutableDictionary<string, KnowledgeValue>? mapping) ? " → " + string.Join(", ", mapping.Select(m => KnowledgeTextFormatter.Name(profile.StrategyKnowledge, m.Key) + "=" + KnowledgeTextFormatter.Value(m.Value))) : " → mapping отсутствует")))
        + "\nИсходы общей стратегии (условия — в соответствующих правилах):\n" + string.Join("\n", profile.StrategyKnowledge.Outcomes.Values.Select(o => o.Value + " · " + o.Title + ": " + o.Description
            + " [" + string.Join(", ", profile.StrategyKnowledge.Rules.Where(r => r.Conclusions.Any(c => profile.StrategyKnowledge.Facts[c.FactId].Role == FactRole.Goal && c.Value.Canonical == o.Value)).Select(r => r.Id)) + "]"))
        + "\nОтветы человека: Нет — явное отрицание наблюдения; Неясно — неопределенный ответ; Не проверял — явный пропуск; ответ не получен — отсутствие ответа по сроку, не Нет."
        + "\nGPIO, передача изображения и завершенный файловый тест не доказывают универсальную физическую исправность. PIR не подтверждает питание; реле не измеряет контакты.";
}
