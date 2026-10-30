using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Consultation;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ViewModels;

public sealed record ConsultationAnswerRow(string Id, string Name, string Value, string Origin, string EditValue) {
    public IRelayCommand? EditCommand { get; init; }
    public override string ToString() => Id + " — " + Name + " = " + Value;
}

public partial class ConsultationViewModel : ViewModelBase {
    private ConsultationService? session;
    private long revision;
    private string activePath = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SelectionCaption))] private string knowledgePath = Path.Combine(AppContext.BaseDirectory, "Knowledge", "lr1-strategies.json");
    [ObservableProperty] private string initialFactsPath = "";
    [ObservableProperty] private string sessionHeader = "Ручной сеанс еще не начат";
    [ObservableProperty] private string sessionDetails = "";
    [ObservableProperty] private string questionText = "Начните консультацию";
    [ObservableProperty] private string answerHint = "";
    [ObservableProperty] private string answerText = "";
    [ObservableProperty] private string editText = "";
    [ObservableProperty] private string outcomeText = "Консультация еще не начата";
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string whyText = "";
    [ObservableProperty] private string traceText = "Сначала начните консультацию";
    [ObservableProperty] private ConsultationAnswerRow? selectedFact;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasQuestion))] private ConsultationFormViewModel? questionForm;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsEditing))] private ConsultationFormViewModel? editForm;
    [ObservableProperty] private ResultExplanationViewModel? explanation;
    [ObservableProperty] private ImmutableArray<ExplanationTreeNode> treeRoots = ImmutableArray<ExplanationTreeNode>.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanStart))][NotifyPropertyChangedFor(nameof(StateText))][NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool isLoading;
    public ObservableCollection<ConsultationAnswerRow> Answers { get; } = new ObservableCollection<ConsultationAnswerRow>();
    public ObservableCollection<ConsultationAnswerRow> OtherInputs { get; } = new ObservableCollection<ConsultationAnswerRow>();
    public string AnswerCount => Answers.Count == 0 ? "Полученных ответов пока нет" : "Получено ответов: " + Answers.Count;
    public string SelectionCaption => "Выбран файл " + Path.GetFileName(KnowledgePath) + (session is null ? "; еще не загружен" : "; для нового сеанса");
    public InferenceResult? ResultSnapshot => session?.Result;
    public bool HasQuestion => QuestionForm is not null;
    public bool IsEditing => EditForm is not null;
    public bool CanStart => !IsLoading;
    public bool CanAnswer => !IsLoading && session?.NextQuestion is not null && EditForm is null;
    public bool CanCorrect => !IsLoading && session is not null && !session.Cancelled && EditForm is not null;
    public bool CanCancel => !IsLoading && session is not null && !session.Cancelled;
    public bool CanExplain => !IsLoading && ResultSnapshot is not null;
    public string StateText => IsLoading ? "Загружаем базу знаний…" : session is null ? "Консультация не начата" : session.Cancelled ? "Консультация отменена; расчет сохранен как история"
        : session.NextQuestion is not null ? "Ожидается ответ" : ResultExplanationViewModel.StatusLabel(session.Result.Status);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync() {
        if (IsLoading) return;
        string path = KnowledgePath, factsPath = InitialFactsPath;
        IsLoading = true; NotifyActions();
        try {
            ConsultationService replacement = await Task.Run(() => {
                KnowledgeBase knowledge = new KnowledgeBaseService().Load(path);
                KnowledgeJson.Require(knowledge.SchemaVersion == 2 && knowledge.Outcomes.Count > 0,
                    "GUI консультации требует schema_version 2 с каталогом исходов; техническая схема 1 доступна через --infer");
                ImmutableDictionary<string, InputFact>? inputs = string.IsNullOrWhiteSpace(factsPath) ? null : new InputFactService().Load(factsPath, knowledge);
                return new ConsultationService(knowledge, inputs);
            });
            // новый сеанс заменяет старый только после полной проверки обоих файлов
            session = replacement; activePath = path; revision++;
            SelectedFact = null; EditForm = null; AnswerText = EditText = "";
            Refresh("Новая ручная консультация начата");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) {
            Message = "Новый сеанс не начат; прежний сеанс не заменен: " + exception.Message;
        }
        finally { IsLoading = false; NotifyActions(); }
    }
    [RelayCommand(CanExecute = nameof(CanAnswer))] private void Answer() => QuestionForm?.Submit(AnswerText ?? "");
    [RelayCommand(CanExecute = nameof(CanAnswer))] private void Unknown() => QuestionForm?.Submit("?");
    private bool Submit(ConsultationFormViewModel form, string text) {
        if (IsLoading || session is null || session.Cancelled || form.Revision != revision
            || (form.Editing ? !ReferenceEquals(form, EditForm) : !ReferenceEquals(form, QuestionForm) || EditForm is not null)) return false;
        if (string.IsNullOrWhiteSpace(text)) { form.Error = "Введите значение или явно выберите Не знаю"; Message = "Ответ не принят: пустой ввод"; return false; }
        if (!session.TryAnswer(form.Definition.Id, text, out string error, canonicalEnum: form.Definition.Type == FactType.Enum)) {
            form.Error = "Ответ не принят: " + error; Message = form.Error; return false;
        }
        revision++; SelectedFact = null; EditForm = null; AnswerText = EditText = "";
        Refresh("Ответ " + form.Definition.Id + " принят; прежние производные пересчитаны"); return true;
    }
    [RelayCommand(CanExecute = nameof(CanCorrect))] private void Correct() => EditForm?.Submit(EditText ?? "");
    [RelayCommand] private void CloseEdit() { SelectedFact = null; EditForm = null; NotifyActions(); }
    [RelayCommand(CanExecute = nameof(CanAnswer))] private void Why() { WhyText = session?.ExplainWhy() ?? "Сеанс не начат"; }
    [RelayCommand(CanExecute = nameof(CanExplain))] private void ExplainResult() {
        if (session is null || IsLoading) return;
        Explanation = new ResultExplanationViewModel(session.Result, (session.Cancelled ? "ИСТОРИЯ отмененной консультации\n" : "Ручная консультация\n") + SessionHeader + "\nСеанс начат " + session.StartedAt.ToString("O"), SessionDetails);
    }
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() {
        if (!CanCancel) return;
        session!.Cancel(); revision++; SelectedFact = null; EditForm = null;
        Refresh("Консультация отменена; сохраненный расчет доступен только как история");
    }
    partial void OnSelectedFactChanged(ConsultationAnswerRow? value) {
        if (value is not null && !Answers.Concat(OtherInputs).Any(row => ReferenceEquals(row, value))) { SelectedFact = null; return; }
        EditText = value?.EditValue ?? "";
        EditForm = value is null || session is null || session.Cancelled || IsLoading ? null
            : new ConsultationFormViewModel(session.Knowledge.Facts[value.Id], revision, true, Submit) { InputText = EditText };
        NotifyActions();
    }
    private void Refresh(string message) {
        if (session is null) return;
        Explanation = null; WhyText = "";
        Answers.Clear(); OtherInputs.Clear();
        foreach (string id in session.Knowledge.QuestionOrder) {
            FactDefinition definition = session.Knowledge.Facts[id];
            bool answered = session.Answers.TryGetValue(id, out InputFact? input);
            ConsultationAnswerRow row = new ConsultationAnswerRow(id, definition.Name,
                answered ? input!.Value is null ? "Не знаю" : KnowledgeTextFormatter.Value(input.Value) : "Ответа нет",
                input?.Origin ?? "Ответ еще не получен", answered ? input!.Value?.Canonical ?? "?" : "");
            long rowRevision = revision;
            row = row with { EditCommand = new RelayCommand(() => { if (!IsLoading && !session.Cancelled && rowRevision == revision) SelectedFact = row; }) };
            if (answered) Answers.Add(row); else OtherInputs.Add(row);
        }
        SessionHeader = "БЗ " + session.Knowledge.Id + " · " + session.Knowledge.Version + (session.Knowledge.Synthetic ? " · ИСКУССТВЕННАЯ БЗ" : "");
        SessionDetails = "Сеанс начат " + session.StartedAt.ToString("O") + "\n" + activePath + "\nSHA256 " + session.Knowledge.Sha256;
        FactDefinition? question = session.NextQuestion;
        QuestionForm = question is null ? null : new ConsultationFormViewModel(question, revision, false, Submit);
        QuestionText = question is null ? "Текущего вопроса нет" : question.Id + ": " + question.Question;
        AnswerHint = QuestionForm?.Hint ?? "";
        ConsultationOutcome? outcome = session.Outcome;
        OutcomeText = session.Cancelled ? "Сеанс отменен; сохраненный расчет не является активной рекомендацией"
            : outcome is null ? ResultExplanationViewModel.StatusLabel(session.Result.Status)
            : (outcome.Kind switch { ConsultationOutcomeKind.Strategy => "Рекомендация", ConsultationOutcomeKind.Clarification => "Требуется уточнение", _ => "Решение не выдается" })
                + ": " + outcome.Value + " — " + outcome.Title + "\n" + outcome.Description;
        TraceText = (session.Cancelled ? "ИСТОРИЯ ОТМЕНЕННОГО СЕАНСА\n" : "") + session.ExplainHow();
        ImmutableArray<ExplanationTreeNode> projected = new ExplanationTreeBuilder().Build(session.Result);
        TreeRoots = session.Cancelled ? ImmutableArray.Create(new ExplanationTreeNode("История отмененного сеанса; активной рекомендации нет", projected)) : projected;
        Message = message;
        OnPropertyChanged(nameof(ResultSnapshot)); OnPropertyChanged(nameof(AnswerCount)); OnPropertyChanged(nameof(SelectionCaption)); NotifyActions();
    }
    private void NotifyActions() {
        OnPropertyChanged(nameof(CanAnswer)); OnPropertyChanged(nameof(CanCorrect)); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanExplain)); OnPropertyChanged(nameof(StateText));
        if (QuestionForm is not null) QuestionForm.Enabled = CanAnswer;
        if (EditForm is not null) EditForm.Enabled = CanCorrect;
        AnswerCommand.NotifyCanExecuteChanged(); UnknownCommand.NotifyCanExecuteChanged(); WhyCommand.NotifyCanExecuteChanged(); CorrectCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ExplainResultCommand.NotifyCanExecuteChanged();
    }
}
