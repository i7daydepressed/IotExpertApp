using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class ConsultationView : UserControl {
    public ConsultationView() {
        Content = new TabControl {
            ItemsSource = new TabItem[] {
                new TabItem { Header = "Вопросы и ответы", Content = CreateDialogue() },
                new TabItem { Header = "Дерево объяснения", Content = CreateTree() },
                new TabItem { Header = "Текст трассы", Content = CreateTrace() }
            }
        };
    }

    private static ScrollViewer CreateDialogue() {
        StackPanel panel = new StackPanel { Spacing = 10, Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock {
            Text = "Ручная консультация: диагностическое заключение и ограничения вводит оператор. Пакеты монитора не задают ответы.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(Label("JSON базы знаний (читается заново только при новой консультации)"));
        panel.Children.Add(Input(nameof(ConsultationViewModel.KnowledgePath), "Путь к БЗ"));
        panel.Children.Add(Label("JSON начальных ручных фактов (необязательно; пусто — опрос с начала)"));
        panel.Children.Add(Input(nameof(ConsultationViewModel.InitialFactsPath), "Путь к файлу входов"));
        WrapPanel sessions = new WrapPanel();
        sessions.Children.Add(Button("Новая консультация", nameof(ConsultationViewModel.StartCommand)));
        sessions.Children.Add(Button("Отмена", nameof(ConsultationViewModel.CancelCommand)));
        panel.Children.Add(sessions);
        panel.Children.Add(Text(nameof(ConsultationViewModel.SessionHeader)));
        panel.Children.Add(Text(nameof(ConsultationViewModel.OutcomeText), bold: true));
        panel.Children.Add(Text(nameof(ConsultationViewModel.Message)));
        panel.Children.Add(Text(nameof(ConsultationViewModel.QuestionText), bold: true));
        panel.Children.Add(Text(nameof(ConsultationViewModel.AnswerHint)));
        TextBox answer = Input(nameof(ConsultationViewModel.AnswerText), "Ответ на текущий вопрос");
        Bind(answer, IsEnabledProperty, nameof(ConsultationViewModel.CanAnswer));
        panel.Children.Add(answer);
        WrapPanel actions = new WrapPanel();
        actions.Children.Add(Button("Ответить", nameof(ConsultationViewModel.AnswerCommand)));
        actions.Children.Add(Button("Не знаю", nameof(ConsultationViewModel.UnknownCommand)));
        actions.Children.Add(Button("Почему этот вопрос?", nameof(ConsultationViewModel.WhyCommand)));
        panel.Children.Add(actions);
        panel.Children.Add(Text(nameof(ConsultationViewModel.WhyText)));
        panel.Children.Add(Label("Ручные входы: выберите признак для просмотра источника и исправления"));
        ComboBox facts = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        Bind(facts, ItemsControl.ItemsSourceProperty, nameof(ConsultationViewModel.Answers));
        Bind(facts, ComboBox.SelectedItemProperty, nameof(ConsultationViewModel.SelectedFact), BindingMode.TwoWay);
        panel.Children.Add(facts);
        panel.Children.Add(Text(nameof(ConsultationViewModel.SelectedFact) + ".Origin"));
        TextBox edit = Input(nameof(ConsultationViewModel.EditText), "Новое значение выбранного ручного входа");
        Bind(edit, IsEnabledProperty, nameof(ConsultationViewModel.CanCorrect));
        panel.Children.Add(edit);
        panel.Children.Add(Button("Исправить ответ и пересчитать", nameof(ConsultationViewModel.CorrectCommand)));
        return new ScrollViewer { Content = panel };
    }

    private static Grid CreateTree() {
        Grid panel = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
            Margin = new Thickness(8)
        };
        panel.Children.Add(Label("Раскрывайте узлы стрелкой: исход → правило → факты → источники. При конфликте значения предварительные."));
        TreeView tree = new TreeView {
            ItemTemplate = new FuncTreeDataTemplate<ExplanationTreeNode>(
                (node, _) => new TextBlock { Text = node?.Title ?? "", TextWrapping = TextWrapping.Wrap, MaxWidth = 900 },
                node => node is null ? ImmutableArray<ExplanationTreeNode>.Empty : node.Children)
        };
        Bind(tree, ItemsControl.ItemsSourceProperty, nameof(ConsultationViewModel.TreeRoots));
        Grid.SetRow(tree, 1);
        panel.Children.Add(tree);
        return panel;
    }

    private static TextBox CreateTrace() {
        TextBox trace = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        Bind(trace, TextBox.TextProperty, nameof(ConsultationViewModel.TraceText));
        return trace;
    }

    private static TextBlock Label(string value) => new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Text(string property, bool bold = false) {
        TextBlock text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (bold) text.FontWeight = FontWeight.SemiBold;
        Bind(text, TextBlock.TextProperty, property);
        return text;
    }

    private static TextBox Input(string property, string placeholder) {
        TextBox input = new TextBox { PlaceholderText = placeholder };
        Bind(input, TextBox.TextProperty, property, BindingMode.TwoWay);
        return input;
    }

    private static Button Button(string label, string command) {
        Button button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
        Bind(button, Avalonia.Controls.Button.CommandProperty, command);
        return button;
    }

    private static void Bind(Control control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay) {
        control.Bind(property, new ReflectionBinding(path) { Mode = mode });
    }
}
