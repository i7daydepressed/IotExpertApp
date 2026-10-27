using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Platform;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public class MainWindow : Window {
    public MainWindow() {
        Title = "IotExpertApp — монитор стенда";
        Width = 1100;
        Height = 800;
        MinWidth = 760;
        MinHeight = 500;
        using (Stream iconStream = AssetLoader.Open(new Uri("avares://IotExpertApp/Assets/avalonia-logo.ico"))) {
            Icon = new WindowIcon(iconStream);
        }
        Content = CreateLayout();
    }

    private static Grid CreateLayout() {
        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(16),
            RowSpacing = 12
        };
        layout.Children.Add(CreateControlsPanel());
        StackPanel state = CreateStatePanel();
        Grid.SetRow(state, 1);
        layout.Children.Add(state);
        ScrollViewer observations = CreateObservationsPanel();
        Grid.SetRow(observations, 2);
        layout.Children.Add(observations);
        TextBlock message = CreateBoundText(nameof(MainViewModel.Message));
        message.MaxHeight = 90;
        Grid.SetRow(message, 3);
        layout.Children.Add(message);
        return layout;
    }

    private static StackPanel CreateControlsPanel() {
        StackPanel panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock {
            Text = "Стенд ESP32 · первый этап v0.1.0",
            FontSize = 23
        });
        panel.Children.Add(CreatePortControls());
        panel.Children.Add(CreatePathInput(nameof(MainViewModel.CapturePath),
            "Новый файл записи при подключении (пусто — без записи), лимит 10 МиБ"));
        panel.Children.Add(CreateReplayControls());
        panel.Children.Add(CreateReplayFileControls());
        return panel;
    }

    private static WrapPanel CreatePortControls() {
        WrapPanel panel = new WrapPanel();
        ComboBox ports = new ComboBox { MinWidth = 240, Margin = new Thickness(0, 0, 8, 8) };
        BindProperty(ports, ItemsControl.ItemsSourceProperty, nameof(MainViewModel.Ports));
        BindProperty(ports, ComboBox.SelectedItemProperty, nameof(MainViewModel.SelectedPort), BindingMode.TwoWay);
        panel.Children.Add(ports);
        panel.Children.Add(CreateCommandButton("Обновить порты", nameof(MainViewModel.RefreshPortsCommand)));
        panel.Children.Add(CreateCommandButton("Подключиться", nameof(MainViewModel.ConnectCommand)));
        panel.Children.Add(CreateCommandButton("Остановить", nameof(MainViewModel.StopCommand)));
        return panel;
    }

    private static WrapPanel CreateReplayControls() {
        WrapPanel panel = new WrapPanel();
        panel.Children.Add(CreateCommandButton("Завершить запись", nameof(MainViewModel.StopCaptureCommand)));
        CheckBox timed = new CheckBox {
            Content = "Воспроизводить по времени",
            Margin = new Thickness(0, 0, 8, 8)
        };
        BindProperty(timed, CheckBox.IsCheckedProperty, nameof(MainViewModel.TimedReplay), BindingMode.TwoWay);
        panel.Children.Add(timed);
        Button pause = CreateCommandButton("", nameof(MainViewModel.TogglePauseCommand));
        BindProperty(pause, ContentControl.ContentProperty, nameof(MainViewModel.PauseText));
        panel.Children.Add(pause);
        return panel;
    }

    private static Grid CreateReplayFileControls() {
        Grid panel = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8
        };
        panel.Children.Add(CreatePathInput(nameof(MainViewModel.ReplayPath), "Абсолютный путь к UART-записи"));
        Button read = CreateCommandButton("Прочитать файл", nameof(MainViewModel.ReadFileCommand));
        read.Margin = new Thickness(0);
        Grid.SetColumn(read, 1);
        panel.Children.Add(read);
        return panel;
    }

    private static StackPanel CreateStatePanel() {
        StackPanel panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(CreateBoundText(nameof(MainViewModel.Status), bold: true));
        panel.Children.Add(CreateBoundText(nameof(MainViewModel.Identity)));
        panel.Children.Add(CreateBoundText(nameof(MainViewModel.LocalDiagnostic)));
        panel.Children.Add(CreateBoundText(nameof(MainViewModel.Statistics)));
        panel.Children.Add(CreateBoundText(nameof(MainViewModel.Playback)));
        panel.Children.Add(CreateBoundText(nameof(MainViewModel.Capture)));
        return panel;
    }

    private static ScrollViewer CreateObservationsPanel() {
        ItemsControl items = new ItemsControl {
            ItemTemplate = new FuncDataTemplate<ObservationRow>((row, _) => CreateObservationCard(row))
        };
        BindProperty(items, ItemsControl.ItemsSourceProperty, nameof(MainViewModel.Observations));
        return new ScrollViewer { Content = items };
    }

    private static Control CreateObservationCard(ObservationRow? observation) {
        if (observation is null) return new TextBlock();
        StackPanel fields = new StackPanel { Spacing = 5 };
        fields.Children.Add(CreateBoundText(nameof(ObservationRow.Title), bold: true, fontSize: 17, wrap: false));
        fields.Children.Add(CreateBoundText(nameof(ObservationRow.State)));
        fields.Children.Add(CreateBoundText(nameof(ObservationRow.Values)));
        fields.Children.Add(CreateBoundText(nameof(ObservationRow.Evidence)));
        // контекст карточки задается строкой наблюдения, контекст окна остается MainViewModel
        return new Border {
            DataContext = observation,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = fields
        };
    }

    private static TextBox CreatePathInput(string property, string placeholder) {
        TextBox input = new TextBox { PlaceholderText = placeholder };
        BindProperty(input, TextBox.TextProperty, property, BindingMode.TwoWay);
        return input;
    }

    private static Button CreateCommandButton(string text, string command) {
        Button button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        BindProperty(button, Button.CommandProperty, command);
        return button;
    }

    private static TextBlock CreateBoundText(string property, bool bold = false, double? fontSize = null, bool wrap = true) {
        TextBlock text = new TextBlock { TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (bold) text.FontWeight = FontWeight.SemiBold;
        if (fontSize.HasValue) text.FontSize = fontSize.Value;
        BindProperty(text, TextBlock.TextProperty, property);
        return text;
    }

    private static void BindProperty(Control control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay) {
        // обычная привязка наследует DataContext и продолжает получать изменения свойств и CanExecute
        control.Bind(property, new ReflectionBinding(path) { Mode = mode });
    }
}