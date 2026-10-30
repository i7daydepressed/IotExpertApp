using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class ConsultationView : UserControl {
    public ConsultationView() {
        StackPanel page = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        page.Children.Add(new TextBlock { Text = "Ручная консультация", FontSize = 24, FontWeight = FontWeight.SemiBold });
        page.Children.Add(Label("Ответьте на вопросы, чтобы получить рекомендацию. Ответы задаете вы; показания стенда не подставляются автоматически."));
        page.Children.Add(Text(nameof(ConsultationViewModel.SelectionCaption)));
        page.Children.Add(Text(nameof(ConsultationViewModel.SessionHeader), true));
        page.Children.Add(Button("Начать / новая консультация", nameof(ConsultationViewModel.StartCommand)));
        page.Children.Add(Text(nameof(ConsultationViewModel.StateText), true));
        page.Children.Add(Text(nameof(ConsultationViewModel.Message)));
        StackPanel settings = new StackPanel { Spacing = 8 };
        settings.Children.Add(Label("Файлы выбираются для нового сеанса. Активная БЗ не меняется до успешного начала."));
        settings.Children.Add(Input(nameof(ConsultationViewModel.KnowledgePath), "БЗ schema 2 с каталогом исходов"));
        settings.Children.Add(Input(nameof(ConsultationViewModel.InitialFactsPath), "Начальные факты (необязательно; пусто — опрос с начала)"));
        settings.Children.Add(Text(nameof(ConsultationViewModel.SessionDetails)));
        page.Children.Add(new Expander { Header = "Настройки консультации", Content = settings });
        ContentControl question = Form(nameof(ConsultationViewModel.QuestionForm)); page.Children.Add(question);
        StackPanel why = new StackPanel { Spacing = 6 }; Bind(why, IsVisibleProperty, nameof(ConsultationViewModel.HasQuestion));
        why.Children.Add(Button("Почему этот вопрос?", nameof(ConsultationViewModel.WhyCommand)));
        why.Children.Add(Label("Пояснение вопроса — возможные пути, которым нужен ответ, а не уже сработавшие правила."));
        why.Children.Add(Text(nameof(ConsultationViewModel.WhyText))); page.Children.Add(why);
        page.Children.Add(Text(nameof(ConsultationViewModel.AnswerCount), true));
        ItemsControl answers = new ItemsControl { ItemTemplate = new FuncDataTemplate<ConsultationAnswerRow>((row, _) => {
            StackPanel p = new StackPanel { DataContext = row, Spacing = 4 };
            p.Children.Add(new TextBlock { Text = row?.ToString(), TextWrapping = TextWrapping.Wrap, FontSize = 14 });
            p.Children.Add(new Expander { Header = "Источник ответа", Content = new TextBlock { Text = row?.Origin, TextWrapping = TextWrapping.Wrap } });
            p.Children.Add(new Button { Content = "Изменить", Command = row?.EditCommand, Margin = new Thickness(0, 0, 0, 8) }); return p;
        }) };
        Bind(answers, ItemsControl.ItemsSourceProperty, nameof(ConsultationViewModel.Answers)); page.Children.Add(answers);
        StackPanel edit = new StackPanel { Spacing = 8 }; Bind(edit, IsVisibleProperty, nameof(ConsultationViewModel.IsEditing)); edit.Children.Add(Form(nameof(ConsultationViewModel.EditForm)));
        edit.Children.Add(Button("Закрыть исправление", nameof(ConsultationViewModel.CloseEditCommand))); page.Children.Add(edit);
        ComboBox other = new ComboBox { PlaceholderText = "Выбрать неотвеченный вход для ручного задания", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<ConsultationAnswerRow>((row, _) => new TextBlock { Text = row?.Id + " · " + row?.Name, TextWrapping = TextWrapping.Wrap }) };
        Bind(other, ItemsControl.ItemsSourceProperty, nameof(ConsultationViewModel.OtherInputs)); Bind(other, ComboBox.SelectedItemProperty, nameof(ConsultationViewModel.SelectedFact), BindingMode.TwoWay);
        page.Children.Add(new Expander { Header = "Остальные входные данные", Content = other });
        page.Children.Add(Text(nameof(ConsultationViewModel.OutcomeText), true));
        WrapPanel actions = new WrapPanel(); actions.Children.Add(Button("Почему такой результат?", nameof(ConsultationViewModel.ExplainResultCommand)));
        actions.Children.Add(Button("Завершить / отменить консультацию", nameof(ConsultationViewModel.CancelCommand))); page.Children.Add(actions);
        page.Children.Add(ResultExplanationView.For(nameof(ConsultationViewModel.Explanation)));
        Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
    private static ContentControl Form(string path) {
        ContentControl host = new ContentControl { ContentTemplate = new FuncDataTemplate<ConsultationFormViewModel>((form, _) => form is null ? new TextBlock() : new ConsultationFormView(form)) };
        Bind(host, ContentControl.ContentProperty, path); return host;
    }
    private static TextBlock Label(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private static TextBlock Text(string path, bool bold = false) {
        TextBlock text = Label(""); if (bold) text.FontWeight = FontWeight.SemiBold; Bind(text, TextBlock.TextProperty, path); return text;
    }
    private static TextBox Input(string path, string placeholder) {
        TextBox input = new TextBox { PlaceholderText = placeholder }; Bind(input, TextBox.TextProperty, path, BindingMode.TwoWay); return input;
    }
    private static Button Button(string title, string path) {
        Button button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 8) }; Bind(button, Avalonia.Controls.Button.CommandProperty, path); return button;
    }
    private static void Bind(Control control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay) => control.Bind(property, new ReflectionBinding(path) { Mode = mode });
}
