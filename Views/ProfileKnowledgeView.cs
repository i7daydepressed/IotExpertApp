using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class ProfileKnowledgeView : UserControl {
    public ProfileKnowledgeView() {
        StackPanel page = new StackPanel { Spacing = 16, Margin = new Thickness(0, 8, 0, 0) };
        page.Children.Add(new TextBlock { Text = "Как система решает", FontSize = 24, FontWeight = FontWeight.SemiBold });
        page.Children.Add(new TextBlock { Text = "Справочник активного профиля: правила, признаки и результаты. Выбор правила не означает, что оно сработало.", TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        page.Children.Add(Text(nameof(ProfileKnowledgeViewModel.ProfileSummary), true));
        page.Children.Add(Text(nameof(ProfileKnowledgeViewModel.LoadStatus)));
        WrapPanel actions = new WrapPanel(); actions.Children.Add(Button("Загрузить профиль", nameof(ProfileKnowledgeViewModel.LoadCommand)));
        actions.Children.Add(Button("Открыть редактор БЗ", nameof(ProfileKnowledgeViewModel.OpenEditorCommand))); page.Children.Add(actions);
        page.Children.Add(new TextBlock { Text = "Редактор открывает прежние инструменты рабочей копии общей БЗ. Переход не загружает выбранную группу и не сохраняет файл.", TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        ListBox groups = new ListBox {
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = new FuncDataTemplate<KnowledgeGroupViewModel>((group, _) => {
                StackPanel item = new StackPanel { DataContext = group, Spacing = 4, Width = 300, Margin = new Thickness(4) };
                item.Children.Add(Text(nameof(KnowledgeGroupViewModel.Title), true)); item.Children.Add(Text(nameof(KnowledgeGroupViewModel.Summary))); return item;
            })
        };
        Bind(groups, ItemsControl.ItemsSourceProperty, nameof(ProfileKnowledgeViewModel.Groups));
        Bind(groups, ListBox.SelectedItemProperty, nameof(ProfileKnowledgeViewModel.SelectedGroup), BindingMode.TwoWay); page.Children.Add(groups);
        WrapPanel kinds = new WrapPanel();
        ComboBox kind = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 12, 8) };
        Bind(kind, ItemsControl.ItemsSourceProperty, nameof(ProfileKnowledgeViewModel.Kinds)); Bind(kind, ComboBox.SelectedItemProperty, nameof(ProfileKnowledgeViewModel.SelectedKind), BindingMode.TwoWay);
        kinds.Children.Add(kind); kinds.Children.Add(Button("Сбросить поиск", nameof(ProfileKnowledgeViewModel.ClearSearchCommand))); page.Children.Add(kinds);
        TextBox search = new TextBox { PlaceholderText = "Поиск по ID, названию или тексту правила" };
        Bind(search, TextBox.TextProperty, nameof(ProfileKnowledgeViewModel.Search), BindingMode.TwoWay); page.Children.Add(search);
        page.Children.Add(Text(nameof(ProfileKnowledgeViewModel.Found)));
        ListBox entries = new ListBox { MaxHeight = 380, MinHeight = 120,
            ItemTemplate = new FuncDataTemplate<KnowledgeEntry>((entry, _) => new TextBlock { Text = entry?.Title, TextWrapping = TextWrapping.Wrap, FontSize = 14 }) };
        ScrollViewer.SetHorizontalScrollBarVisibility(entries, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Bind(entries, ItemsControl.ItemsSourceProperty, nameof(ProfileKnowledgeViewModel.Entries)); Bind(entries, ListBox.SelectedItemProperty, nameof(ProfileKnowledgeViewModel.SelectedEntry), BindingMode.TwoWay);
        StackPanel selected = new StackPanel { Spacing = 10 };
        TextBlock title = Text(nameof(ProfileKnowledgeViewModel.SelectedTitle), true); title.FontSize = 18; selected.Children.Add(title);
        selected.Children.Add(Text(nameof(ProfileKnowledgeViewModel.SelectedBody)));
        selected.Children.Add(new Expander { Header = "Технические сведения элемента", Content = Text(nameof(ProfileKnowledgeViewModel.SelectedTechnical)) });
        Border details = Surface(selected);
        Grid content = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), ColumnSpacing = 16, RowSpacing = 12 };
        content.Children.Add(entries); content.Children.Add(details); Grid.SetColumn(details, 1);
        bool wide = true;
        content.SizeChanged += (_, _) => {
            bool next = content.Bounds.Width >= 950;
            if (wide == next) return; wide = next;
            content.ColumnDefinitions = new ColumnDefinitions(wide ? "2*,3*" : "*");
            content.RowDefinitions = new RowDefinitions(wide ? "Auto" : "Auto,Auto");
            Grid.SetColumn(details, wide ? 1 : 0); Grid.SetRow(details, wide ? 0 : 1);
        };
        page.Children.Add(content);
        page.Children.Add(new Expander { Header = "Состояния и границы текущей модели", Content = Text(nameof(ProfileKnowledgeViewModel.PolicyMemo)) });
        StackPanel technical = new StackPanel { Spacing = 10 }; technical.Children.Add(Text(nameof(ProfileKnowledgeViewModel.ProfileDetails)));
        TextBox path = new TextBox { PlaceholderText = "Путь к профилю устройства" }; Bind(path, TextBox.TextProperty, "Live.ProfilePath", BindingMode.TwoWay); technical.Children.Add(path);
        technical.Children.Add(Text("Live.LoadMessage"));
        page.Children.Add(new Expander { Header = "Технические подробности профиля и путь загрузки", Content = technical });
        Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
    private static Border Surface(Control child) {
        Border surface = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(8), Child = child };
        surface.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush")); return surface;
    }
    private static TextBlock Text(string path, bool bold = false) {
        TextBlock text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        Bind(text, TextBlock.TextProperty, path); return text;
    }
    private static Button Button(string title, string path) {
        Button button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 8) }; Bind(button, Avalonia.Controls.Button.CommandProperty, path); return button;
    }
    private static void Bind(Control control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay) => control.Bind(property, new ReflectionBinding(path) { Mode = mode });
}
