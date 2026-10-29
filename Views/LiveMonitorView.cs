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
            panel.Children.Add(Text(nameof(LiveMonitorViewModel.KnowledgeSummary)));
            panel.Children.Add(Text(nameof(LiveMonitorViewModel.ProfileLoadStatus)));
            BorderThickness = new Thickness(0); Padding = new Thickness(0); Child = panel; return;
        }
        Button freeze = new Button { Content = "Зафиксировать объяснение" };
        Bind(freeze, Button.CommandProperty, nameof(LiveMonitorViewModel.FreezeCommand));
        panel.Children.Add(freeze);
        StackPanel explanation = new StackPanel { Spacing = 10 };
        explanation.Children.Add(new TextBlock { Text = "1. Итог автоматической стратегии", FontWeight = FontWeight.SemiBold });
        explanation.Children.Add(Text(nameof(LiveMonitorViewModel.SelectedOutcome)));
        explanation.Children.Add(new TextBlock { Text = "2. Закрепленный снимок" });
        explanation.Children.Add(Text(nameof(LiveMonitorViewModel.SelectedSnapshot)));
        explanation.Children.Add(new TextBlock { Text = "3. Наблюдения, использованные для оценки" });
        explanation.Children.Add(Text(nameof(LiveMonitorViewModel.SelectedObservations)));
        explanation.Children.Add(new Expander { Header = "4. Диагностические правила профиля", Content = Tree(nameof(LiveMonitorViewModel.DiagnosticRoots)) });
        StackPanel strategy = new StackPanel { Spacing = 8 };
        strategy.Children.Add(Text(nameof(LiveMonitorViewModel.StrategyBridge)));
        strategy.Children.Add(Tree(nameof(LiveMonitorViewModel.StrategyRoots)));
        explanation.Children.Add(new Expander { Header = "5. Входы ЛР1 и цепочка выбора стратегии", Content = strategy });
        explanation.Children.Add(new Expander { Header = "6. Версии, хеши и полная техническая трасса", Content = Text(nameof(LiveMonitorViewModel.ExplanationText)) });
        panel.Children.Add(new Expander { Header = "Объяснение выбранного результата", Content = explanation });
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
