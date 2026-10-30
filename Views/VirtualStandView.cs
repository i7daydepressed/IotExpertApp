using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class VirtualStandView : UserControl {
    private readonly Dictionary<string, Control> targets = new Dictionary<string, Control>();
    public VirtualStandView() {
        StackPanel body = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 12) };
        body.Children.Add(new TextBlock { Text = "Виртуальный стенд", FontSize = 22, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = "Ручной сценарий по выбранному профилю. Значения не являются измерениями; команды плате не отправляются.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Text(nameof(VirtualStandViewModel.ProfileSummary)));
        WrapPanel actions = new WrapPanel();
        actions.Children.Add(CommandButton("Загрузить профиль / начать заново", "LoadProfileCommand"));
        actions.Children.Add(CommandButton("Объяснить результат", "ExplainCommand"));
        actions.Children.Add(CommandButton("Общий опрос (дополнительно)", "OpenConsultationCommand")); body.Children.Add(actions);
        TextBox path = new TextBox { PlaceholderText = "Путь к профилю" };
        Bind(path, TextBox.TextProperty, "Live.ProfilePath", BindingMode.TwoWay);
        body.Children.Add(new Expander { Header = "Выбор профиля", Content = path });
        body.Children.Add(Text(nameof(VirtualStandViewModel.ProfileNotice)));
        body.Children.Add(Text(nameof(VirtualStandViewModel.Outcome), true));
        body.Children.Add(Text(nameof(VirtualStandViewModel.Message)));
        body.Children.Add(Text(nameof(VirtualStandViewModel.Missing), true));
        Button focus = CommandButton("Уточнить недостающие данные", "FocusMissingCommand"); body.Children.Add(focus);
        body.Children.Add(new TextBlock { Text = "Модули и основания", FontSize = 18, FontWeight = FontWeight.SemiBold });
        body.Children.Add(Cards<VirtualModuleViewModel>("Modules", Module));
        body.Children.Add(new TextBlock { Text = "Условия выбора стратегии · V1–V11", FontSize = 18, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = "Ручное значение, в том числе «Неизвестно», перекрывает расчет. Кнопка возврата снова включает значение из правил или профиля.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Cards<VirtualFactViewModel>("Conditions", Fact));
        body.Children.Add(ResultExplanationView.For(nameof(VirtualStandViewModel.DiagnosticExplanation)));
        body.Children.Add(ResultExplanationView.For(nameof(VirtualStandViewModel.EvidenceExplanation)));
        body.Children.Add(ResultExplanationView.For(nameof(VirtualStandViewModel.StrategyExplanation)));
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        VirtualStandViewModel? attached = null;
        void Focus(string id) => Dispatcher.UIThread.Post(() => { if (targets.TryGetValue(id, out Control? target)) { target.BringIntoView(); target.Focus(); } });
        DataContextChanged += (_, _) => {
            if (attached is not null) attached.FocusRequested -= Focus;
            attached = DataContext as VirtualStandViewModel;
            if (attached is not null) attached.FocusRequested += Focus;
        };
    }
    private Control Module(VirtualModuleViewModel module) {
        StackPanel fields = new StackPanel { Spacing = 6 };
        fields.Children.Add(new TextBlock { Text = module.Title, FontSize = 17, FontWeight = FontWeight.SemiBold });
        fields.Children.Add(new TextBlock { Text = "Наблюдаемость · задано вручную", TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(Select(module.ObservationOptions, "SelectedObservation"));
        fields.Children.Add(new TextBlock { Text = "Выборочная проверка · задано вручную", TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(Select(module.MachineOptions, "SelectedMachine"));
        if (module.NeedsHuman) fields.Children.Add(Select(module.HumanOptions, "SelectedHuman"));
        if (module.IsDigital) fields.Children.Add(Select(module.SignalOptions, "SelectedSignal"));
        fields.Children.Add(Text("CheckSummary"));
        fields.Children.Add(new TextBlock { Text = module.Limitation, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        Border card = Surface(fields, module); targets[module.Source] = card; return card;
    }
    private Control Fact(VirtualFactViewModel fact) {
        StackPanel fields = new StackPanel { Spacing = 6 };
        fields.Children.Add(new TextBlock { Text = fact.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(new TextBlock { Text = fact.Question, TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(Text("ValueText", true)); fields.Children.Add(Text("OriginText"));
        TextBlock attention = new TextBlock { Text = "Требуется уточнение", FontWeight = FontWeight.Bold };
        Bind(attention, IsVisibleProperty, "NeedsAttention"); fields.Children.Add(attention);
        WrapPanel choices = new WrapPanel();
        foreach (ConsultationChoice choice in fact.Choices) {
            Button button = new Button { Content = new TextBlock { Text = choice.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 }, Margin = new Thickness(0, 0, 6, 6), Command = fact.ChooseCommand, CommandParameter = choice };
            choices.Children.Add(button);
        }
        choices.Children.Add(new Button { Content = "Неизвестно", Command = fact.UnknownCommand, Margin = new Thickness(0, 0, 6, 6) }); fields.Children.Add(choices);
        if (fact.IsNumeric) {
            TextBox number = new TextBox { PlaceholderText = "Число с точкой" }; Bind(number, TextBox.TextProperty, "NumericText", BindingMode.TwoWay);
            fields.Children.Add(number); fields.Children.Add(new Button { Content = "Задать число", Command = fact.NumberCommand });
        }
        Button reset = new Button { Content = "Вернуть расчетное значение", Command = fact.ResetCommand };
        Bind(reset, IsVisibleProperty, "HasOverride"); fields.Children.Add(reset); fields.Children.Add(Text("Error"));
        Border card = Surface(fields, fact); targets[fact.Definition.Id] = card; return card;
    }
    private static ItemsControl Cards<T>(string path, System.Func<T, Control> create) where T : class {
        ItemsControl cards = new ItemsControl {
            ItemsPanel = new FuncTemplate<Panel?>(() => new ModuleTilePanel()),
            ItemTemplate = new FuncDataTemplate<T>((value, _) => value is null ? new TextBlock() : create(value))
        };
        Bind(cards, ItemsControl.ItemsSourceProperty, path); return cards;
    }
    private static ComboBox Select(object choices, string path) {
        ComboBox select = new ComboBox {
            ItemsSource = (System.Collections.IEnumerable)choices,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<ConsultationChoice>((choice, _) => new TextBlock { Text = choice?.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 })
        };
        Bind(select, SelectingItemsControl.SelectedItemProperty, path, BindingMode.TwoWay); return select;
    }
    private static Border Surface(Control content, object context) {
        Border border = new Border { Child = content, DataContext = context, Padding = new Thickness(12), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(8), Focusable = true };
        border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush")); return border;
    }
    private static TextBlock Text(string path, bool bold = false) {
        TextBlock text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = bold ? 17 : 14, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        Bind(text, TextBlock.TextProperty, path); return text;
    }
    private static Button CommandButton(string title, string path) {
        Button button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 8) }; Bind(button, Button.CommandProperty, path); return button;
    }
    private static void Bind(Control control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay) => control.Bind(property, new ReflectionBinding(path) { Mode = mode });
}
