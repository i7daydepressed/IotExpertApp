using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class OnlineDataView : Border {
    public OnlineDataView() {
        Padding = new Thickness(16); CornerRadius = new CornerRadius(10);
        this.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush"));
        StackPanel panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Данные стенда онлайн", FontSize = 22, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Text(nameof(OnlineDataViewModel.Mode), true));
        panel.Children.Add(Text(nameof(OnlineDataViewModel.Identity)));
        panel.Children.Add(Text(nameof(OnlineDataViewModel.PacketInfo)));
        panel.Children.Add(Text(nameof(OnlineDataViewModel.Counters)));
        panel.Children.Add(Text(nameof(OnlineDataViewModel.Policy)));
        ItemsControl sources = new ItemsControl {
            ItemsPanel = new FuncTemplate<Panel?>(() => new ModuleTilePanel()),
            ItemTemplate = new FuncDataTemplate<OnlineSourceViewModel>((source, _) => Tile(source))
        };
        sources.Bind(ItemsControl.ItemsSourceProperty, new ReflectionBinding(nameof(OnlineDataViewModel.Sources)));
        panel.Children.Add(sources);
        panel.Children.Add(new Expander { Header = "Технические подробности данных", Content = Text(nameof(OnlineDataViewModel.Details)) });
        Child = panel;
    }
    private static Control Tile(OnlineSourceViewModel? source) {
        StackPanel panel = new StackPanel { Spacing = 6 };
        TextBlock title = Text(nameof(OnlineSourceViewModel.Title), true); title.FontSize = 17; panel.Children.Add(title);
        panel.Children.Add(Text(nameof(OnlineSourceViewModel.Status), true));
        panel.Children.Add(Text(nameof(OnlineSourceViewModel.Values)));
        panel.Children.Add(Text(nameof(OnlineSourceViewModel.Freshness)));
        panel.Children.Add(Text(nameof(OnlineSourceViewModel.Error)));
        panel.Children.Add(new Expander { Header = "Подробности", Content = Text(nameof(OnlineSourceViewModel.Details)) });
        Border tile = new Border { DataContext = source, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Child = panel };
        tile.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundAltHighBrush"));
        return tile;
    }
    private static TextBlock Text(string path, bool bold = false) {
        TextBlock text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        text.Bind(TextBlock.TextProperty, new ReflectionBinding(path)); return text;
    }
}
