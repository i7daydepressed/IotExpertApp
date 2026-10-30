using System;
using System.Collections.Immutable;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ViewModels;

public sealed record ConsultationChoice(string Label, string Canonical);

// кнопки замыкаются на этот экземпляр формы с id/редакцией, а не на изменяющийся NextQuestion
public partial class ConsultationFormViewModel : ViewModelBase {
    private readonly Func<ConsultationFormViewModel, string, bool> submit;
    public FactDefinition Definition { get; }
    public long Revision { get; }
    public bool Editing { get; }
    public string Title => Definition.Id + " · " + (Definition.Question ?? Definition.Name);
    public string Hint => KnowledgeTextFormatter.Fact(Definition) + (IsNumeric ? "\nВведите JSON-число с точкой; пустой ввод не означает Не знаю" : "");
    public bool IsNumeric => Definition.Type is FactType.Integer or FactType.Number;
    public ImmutableArray<ConsultationChoice> Choices { get; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(PendingValue))] private string inputText = "";
    public string PendingValue => "Выбрано: " + (InputText == "?" ? "Не знаю" : Definition.Type == FactType.Boolean && InputText is "true" or "false" ? InputText == "true" ? "Да" : "Нет" : InputText);
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool enabled = true;
    public ConsultationFormViewModel(FactDefinition definition, long revision, bool editing, Func<ConsultationFormViewModel, string, bool> submit) {
        Definition = definition; Revision = revision; Editing = editing; this.submit = submit;
        Choices = definition.Type == FactType.Boolean ? ImmutableArray.Create(new ConsultationChoice("Да", "true"), new ConsultationChoice("Нет", "false"))
            : definition.Type == FactType.Enum ? definition.Options.Select(value => new ConsultationChoice(value, value)).ToImmutableArray() : ImmutableArray<ConsultationChoice>.Empty;
    }
    public bool Submit(string text) => submit(this, text);
    [RelayCommand] private void Choose(ConsultationChoice? choice) { if (choice is not null) { if (Editing) InputText = choice.Canonical; else Submit(choice.Canonical); } }
    [RelayCommand] private void Send() => Submit(InputText);
    [RelayCommand] private void Unknown() { if (Editing) InputText = "?"; else Submit("?"); }
}
