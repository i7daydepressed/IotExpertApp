using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Consultation;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ViewModels;

public sealed record ConsultationAnswerRow(string Id, string Name, string Value, string Origin, string EditValue) {
    public override string ToString() => Id + " — " + Name + " = " + Value;
}

public partial class ConsultationViewModel : ViewModelBase {
    private ConsultationService? session;
    [ObservableProperty] private string knowledgePath = Path.Combine(AppContext.BaseDirectory, "Knowledge", "lr1-strategies.json");
    [ObservableProperty] private string initialFactsPath = "";
    [ObservableProperty] private string sessionHeader = "Ручной сеанс еще не начат";
    [ObservableProperty] private string questionText = "Нажмите Новая консультация";
    [ObservableProperty] private string answerHint = "";
    [ObservableProperty] private string answerText = "";
    [ObservableProperty] private string editText = "";
    [ObservableProperty] private string outcomeText = "Подтвержденного исхода нет";
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string whyText = "";
    [ObservableProperty] private string traceText = "Сначала начните консультацию";
    [ObservableProperty] private ConsultationAnswerRow? selectedFact;
    [ObservableProperty] private ImmutableArray<ExplanationTreeNode> treeRoots = ImmutableArray<ExplanationTreeNode>.Empty;

    public ObservableCollection<ConsultationAnswerRow> Answers { get; } = new ObservableCollection<ConsultationAnswerRow>();
    public InferenceResult? ResultSnapshot => session?.Result;
    public bool CanAnswer => session?.NextQuestion is not null;
    public bool CanCorrect => session is not null && !session.Cancelled && SelectedFact is not null;
    public bool CanCancel => session is not null && !session.Cancelled;

    [RelayCommand]
    private void Start() {
        try {
            KnowledgeBase knowledge = new KnowledgeBaseService().Load(KnowledgePath);
            KnowledgeJson.Require(knowledge.SchemaVersion == 2 && knowledge.Outcomes.Count > 0,
                "GUI консультации требует schema_version 2 с каталогом исходов; техническая схема 1 доступна через --infer");
            ImmutableDictionary<string, InputFact>? inputs = string.IsNullOrWhiteSpace(InitialFactsPath)
                ? null : new InputFactService().Load(InitialFactsPath, knowledge);
            ConsultationService replacement = new ConsultationService(knowledge, inputs);
            // новый сеанс заменяет старый только после полной проверки обоих файлов
            session = replacement;
            SelectedFact = null;
            AnswerText = "";
            EditText = "";
            Refresh("Новая ручная консультация начата");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException) {
            Message = "Сеанс не заменен: " + exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void Answer() => ApplyCurrent(AnswerText ?? "");

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void Unknown() => ApplyCurrent("не знаю");

    private void ApplyCurrent(string text) {
        FactDefinition? question = session?.NextQuestion;
        if (session is null || question is null) return;
        if (!session.TryAnswer(question.Id, text, out string error)) { Message = "Ответ не принят: " + error; return; }
        AnswerText = "";
        Refresh("Ответ " + question.Id + " принят; производные пересчитаны");
    }

    [RelayCommand(CanExecute = nameof(CanCorrect))]
    private void Correct() {
        if (session is null || SelectedFact is null || session.Cancelled) return;
        string id = SelectedFact.Id;
        if (!session.TryAnswer(id, EditText ?? "", out string error)) { Message = "Исправление не принято: " + error; return; }
        AnswerText = "";
        Refresh("Ответ " + id + " исправлен; прежние производные удалены и вычислены заново");
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void Why() { WhyText = session?.ExplainWhy() ?? "Сеанс не начат"; }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() {
        session?.Cancel();
        Refresh("Консультация отменена; сохраненный расчет доступен только как история");
    }

    partial void OnSelectedFactChanged(ConsultationAnswerRow? value) {
        EditText = value?.EditValue ?? "";
        OnPropertyChanged(nameof(CanCorrect));
        CorrectCommand.NotifyCanExecuteChanged();
    }

    private void Refresh(string message) {
        if (session is null) return;
        string? selectedId = SelectedFact?.Id;
        Answers.Clear();
        foreach (string id in session.Knowledge.QuestionOrder) {
            FactDefinition definition = session.Knowledge.Facts[id];
            bool answered = session.Answers.TryGetValue(id, out InputFact? input);
            string value = answered ? input!.Value?.Canonical ?? "неизвестно (ответ получен)" : "ответа нет";
            string edit = answered ? input!.Value?.Canonical ?? "не знаю" : "";
            Answers.Add(new ConsultationAnswerRow(id, definition.Name, value, input?.Origin ?? "", edit));
        }
        SelectedFact = selectedId is null ? null : Answers.FirstOrDefault(row => row.Id == selectedId);
        SessionHeader = "Ручной сеанс: " + session.StartedAt.ToString("O", CultureInfo.InvariantCulture)
            + "\nБЗ " + session.Knowledge.Id + " | версия " + session.Knowledge.Version + "\nSHA256 " + session.Knowledge.Sha256
            + (session.Knowledge.Synthetic ? "\nИСКУССТВЕННАЯ БЗ: проверка алгоритма" : "");
        FactDefinition? question = session.NextQuestion;
        QuestionText = session.Cancelled ? "Сеанс отменен" : question is null ? "Текущего вопроса нет" : question.Id + ": " + question.Question;
        AnswerHint = question is null ? "" : DescribeOptions(question);
        ConsultationOutcome? outcome = session.Outcome;
        OutcomeText = session.Cancelled ? "Сеанс отменен; сохраненный расчет не является активной рекомендацией"
            : outcome is null ? "Подтвержденного исхода нет: " + session.Result.Status
            : (outcome.Kind switch {
                ConsultationOutcomeKind.Strategy => "Рекомендация",
                ConsultationOutcomeKind.Clarification => "Требуется уточнение",
                _ => "Отказ от рекомендации"
            }) + ": " + outcome.Value + " — " + outcome.Title + "\n" + outcome.Description;
        WhyText = "";
        TraceText = (session.Cancelled ? "ИСТОРИЯ ОТМЕНЕННОГО СЕАНСА\n" : "") + session.ExplainHow();
        ImmutableArray<ExplanationTreeNode> projected = new ExplanationTreeBuilder().Build(session.Result);
        TreeRoots = session.Cancelled
            ? ImmutableArray.Create(new ExplanationTreeNode("История отмененного сеанса; активной рекомендации нет", projected))
            : projected;
        Message = message;
        OnPropertyChanged(nameof(ResultSnapshot));
        OnPropertyChanged(nameof(CanAnswer));
        OnPropertyChanged(nameof(CanCorrect));
        OnPropertyChanged(nameof(CanCancel));
        AnswerCommand.NotifyCanExecuteChanged();
        UnknownCommand.NotifyCanExecuteChanged();
        WhyCommand.NotifyCanExecuteChanged();
        CorrectCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeOptions(FactDefinition question) {
        if (question.Type == FactType.Boolean) return "да / нет / не знаю";
        if (question.Type == FactType.Enum)
            return string.Join("\n", question.Options.Select((value, index) => (index + 1) + ") " + value))
                + "\nВведите номер или точное значение; не знаю — неизвестность";
        return "Число в JSON-формате с точкой, диапазон "
            + (question.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "без нижней границы") + " .. "
            + (question.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "без верхней границы") + "; не знаю — неизвестность";
    }
}
