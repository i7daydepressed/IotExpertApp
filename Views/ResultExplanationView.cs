using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class ResultExplanationView : UserControl {
    public ResultExplanationView() {
        StackPanel body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = "Результат", FontSize = 18, FontWeight = FontWeight.SemiBold });
        body.Children.Add(Text(nameof(ResultExplanationViewModel.Outcome), true)); body.Children.Add(Text(nameof(ResultExplanationViewModel.Status)));
        body.Children.Add(Text(nameof(ResultExplanationViewModel.Context)));
        body.Children.Add(new TextBlock { Text = "Основания выбранного исхода", FontSize = 17, FontWeight = FontWeight.SemiBold });
        body.Children.Add(Text(nameof(ResultExplanationViewModel.Basis)));
        body.Children.Add(new Expander { Header = "Входные данные расчета (включая дополнительные)", Content = Text(nameof(ResultExplanationViewModel.Inputs)) });
        body.Children.Add(new TextBlock { Text = "Фактическая цепочка правил", FontSize = 17, FontWeight = FontWeight.SemiBold });
        body.Children.Add(Text(nameof(ResultExplanationViewModel.Rules)));
        body.Children.Add(new TextBlock { Text = "Трасса может содержать срабатывания вне ветви цели. Дерево отдельно показывает доказательство, фактические оценки условий и источники.", TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        TreeView tree = new TreeView { MaxHeight = 480, ItemTemplate = new FuncTreeDataTemplate<ExplanationTreeNode>(
            (node, _) => new TextBlock { Text = node?.Title, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, FontSize = 14 },
            node => node?.Children ?? ImmutableArray<ExplanationTreeNode>.Empty) };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        tree.Bind(ItemsControl.ItemsSourceProperty, new ReflectionBinding(nameof(ResultExplanationViewModel.TreeRoots)));
        body.Children.Add(new Expander { Header = "Дерево доказательства и условий", Content = tree });
        body.Children.Add(new Expander { Header = "Технические сведения и полная трасса", Content = Text(nameof(ResultExplanationViewModel.Technical)) });
        Border surface = new Border { Child = body, Padding = new Thickness(16), CornerRadius = new CornerRadius(8) };
        surface.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush")); Content = surface;
    }
    private static TextBlock Text(string path, bool bold = false) {
        TextBlock text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        text.Bind(TextBlock.TextProperty, new ReflectionBinding(path)); return text;
    }
    public static ContentControl For(string property) {
        ContentControl content = new ContentControl { ContentTemplate = new FuncDataTemplate<ResultExplanationViewModel>((model, _) => model is null ? new TextBlock() : new ResultExplanationView { DataContext = model }) };
        content.Bind(ContentControl.ContentProperty, new ReflectionBinding(property)); return content;
    }
}
