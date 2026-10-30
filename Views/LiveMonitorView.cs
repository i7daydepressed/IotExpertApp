using Avalonia;
using System.Collections.Immutable;
using Avalonia.Controls.Templates;
using IotExpertApp.Models.Knowledge;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class LiveMonitorView : Border {
    public LiveMonitorView(bool detailsOnly = false) {
        Padding = new Thickness(14);
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Gray;
        CornerRadius = new CornerRadius(6);
        StackPanel panel = new StackPanel { Spacing = 10 };
        if (!detailsOnly) {
            panel.Children.Add(new TextBlock { Text = "Автоматическая оценка по телеметрии", FontSize = 14 });
            TextBlock title = Text(nameof(LiveMonitorViewModel.Title)); title.FontSize = 20; title.FontWeight = FontWeight.SemiBold;
            panel.Children.Add(title);
            panel.Children.Add(Text(nameof(LiveMonitorViewModel.Recommendation)));
            CheckBox include = new CheckBox { Content = new TextBlock { Text = "Учитывать мои ответы при общей оценке", TextWrapping = TextWrapping.Wrap } };
            Bind(include, CheckBox.IsCheckedProperty, nameof(LiveMonitorViewModel.IncludeCheckAnswers), BindingMode.TwoWay);
            Bind(include, IsEnabledProperty, nameof(LiveMonitorViewModel.CanUseCheckAnswers)); panel.Children.Add(include);
            panel.Children.Add(Text(nameof(LiveMonitorViewModel.AssessmentMode)));
            panel.Children.Add(new Expander { Header = "Какие проверки учитываются", Content = Text(nameof(LiveMonitorViewModel.CheckEvidenceSummary)) });
            panel.Children.Add(Text(nameof(LiveMonitorViewModel.KnowledgeSummary)));
            panel.Children.Add(Text(nameof(LiveMonitorViewModel.ProfileLoadStatus)));
            Button why = new Button { Content = "Почему такой результат?" }; Bind(why, Button.CommandProperty, nameof(LiveMonitorViewModel.FreezeCommand)); panel.Children.Add(why);
            BorderThickness = new Thickness(0); Padding = new Thickness(0); Child = panel; return;
        }
        panel.Children.Add(Text(nameof(LiveMonitorViewModel.ExplanationHistory)));
        Button freeze = new Button { Content = "Показать объяснение текущего результата" }; Bind(freeze, Button.CommandProperty, nameof(LiveMonitorViewModel.FreezeCommand)); panel.Children.Add(freeze);
        panel.Children.Add(Text(nameof(LiveMonitorViewModel.SelectedOutcome)));
        panel.Children.Add(Text(nameof(LiveMonitorViewModel.MissingExplanation)));
        panel.Children.Add(new TextBlock { Text = "Наблюдаемость стенда (OBS)", FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(ResultExplanationView.For(nameof(LiveMonitorViewModel.FrozenDiagnosticExplanation)));
        panel.Children.Add(Text(nameof(LiveMonitorViewModel.FrozenChecksSummary)));
        panel.Children.Add(ResultExplanationView.For(nameof(LiveMonitorViewModel.FrozenEvidenceExplanation)));
        panel.Children.Add(new TextBlock { Text = "Выбор стратегии ЛР1", FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(ResultExplanationView.For(nameof(LiveMonitorViewModel.FrozenStrategyExplanation)));
        StackPanel details = new StackPanel { Spacing = 8 };
        details.Children.Add(Text(nameof(LiveMonitorViewModel.Context)));
        TextBox path = new TextBox { PlaceholderText = "Путь к JSON профиля устройства" };
        Bind(path, TextBox.TextProperty, nameof(LiveMonitorViewModel.ProfilePath), BindingMode.TwoWay);
        details.Children.Add(path);
        Button reload = new Button { Content = "Загрузить профиль" };
        Bind(reload, Button.CommandProperty, nameof(LiveMonitorViewModel.ReloadCommand));
        details.Children.Add(reload);
        details.Children.Add(Text(nameof(LiveMonitorViewModel.LoadMessage)));
        panel.Children.Add(new Expander { Header = "Профиль и происхождение результата", Content = details });
        Child = panel;
    }

    private static TreeView Tree(string property) {
        TreeView tree = new TreeView { MaxHeight = 360, ItemTemplate = new FuncTreeDataTemplate<ExplanationTreeNode>(
            (node, _) => new TextBlock { Text = node?.Title ?? "", TextWrapping = TextWrapping.Wrap, MaxWidth = 900 },
            node => node is null ? ImmutableArray<ExplanationTreeNode>.Empty : node.Children) };
        Bind(tree, ItemsControl.ItemsSourceProperty, property); return tree;
    }

    private static TextBlock Text(string property) {
        TextBlock control = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Bind(control, TextBlock.TextProperty, property);
        return control;
    }

    private static void Bind(Control control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay)
        => control.Bind(property, new ReflectionBinding(path) { Mode = mode });
}
