using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class ConsultationFormView : UserControl {
    public ConsultationFormView(ConsultationFormViewModel form) {
        DataContext = form;
        this.Bind(IsEnabledProperty, new ReflectionBinding(nameof(ConsultationFormViewModel.Enabled)));
        StackPanel panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = form.Editing ? "Исправление ответа; подтвердите новое значение" : "Текущий вопрос", FontSize = 17, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = form.Title, FontSize = 18, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = form.Hint, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        ItemsControl choices = new ItemsControl { ItemsSource = form.Choices, ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = new FuncDataTemplate<ConsultationChoice>((choice, _) => {
                Button button = new Button { Content = new TextBlock { Text = choice?.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, Margin = new Thickness(0, 0, 8, 8) };
                button.Click += (_, _) => form.ChooseCommand.Execute(choice); return button;
            }) };
        panel.Children.Add(choices);
        if (form.IsNumeric) {
            TextBox input = new TextBox { PlaceholderText = "Число с точкой", MaxWidth = 440, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            input.Bind(TextBox.TextProperty, new ReflectionBinding(nameof(ConsultationFormViewModel.InputText)) { Mode = BindingMode.TwoWay });
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { form.SendCommand.Execute(null); e.Handled = true; } };
            panel.Children.Add(input);
        }
        Button unknown = new Button { Content = "Не знаю", Margin = new Thickness(0, 0, 8, 8), Command = form.UnknownCommand }; panel.Children.Add(unknown);
        if (form.Editing) {
            TextBlock selected = new TextBlock { TextWrapping = TextWrapping.Wrap }; selected.Bind(TextBlock.TextProperty, new ReflectionBinding(nameof(ConsultationFormViewModel.PendingValue))); panel.Children.Add(selected);
        }
        if (form.Editing || form.IsNumeric) panel.Children.Add(new Button { Content = form.Editing ? "Применить исправление" : "Ответить", Command = form.SendCommand });
        TextBlock error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 }; error.Bind(TextBlock.TextProperty, new ReflectionBinding(nameof(ConsultationFormViewModel.Error))); panel.Children.Add(error);
        Border border = new Border { Child = panel, Padding = new Thickness(16), CornerRadius = new CornerRadius(8) };
        border.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush")); Content = border;
    }
}
