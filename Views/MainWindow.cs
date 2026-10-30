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
        Title = "IotExpertApp — состояние стенда";
        Width = 1280;
        Height = 800;
        MinWidth = 620;
        MinHeight = 500;
        using (Stream iconStream = AssetLoader.Open(new Uri("avares://IotExpertApp/Assets/avalonia-logo.ico"))) {
            Icon = new WindowIcon(iconStream);
        }
        Content = CreateLayout();
    }

    private static Control CreateLayout() {
        ConsultationView consultation = new ConsultationView();
        BindProperty(consultation, DataContextProperty, nameof(MainViewModel.Consultation));
        KnowledgeEditorView editor = new KnowledgeEditorView();
        BindProperty(editor, DataContextProperty, nameof(MainViewModel.KnowledgeEditor));
        return new TabControl {
            Margin = new Thickness(16),
            ItemsSource = new TabItem[] {
                new TabItem { Header = "Стенд", Content = CreateMonitorPanel() },
                new TabItem { Header = "Виртуальный стенд", Content = CreateVirtualWorkspace(consultation) },
                new TabItem { Header = "База знаний", Content = CreateKnowledgeWorkspace(editor) }
            }
        };
    }

    private static Control CreateVirtualWorkspace(ConsultationView consultation) {
        Grid host = new Grid();
        VirtualStandView stand = new VirtualStandView();
        BindProperty(stand, DataContextProperty, nameof(MainViewModel.VirtualStand));
        BindProperty(stand, IsVisibleProperty, nameof(VirtualStandViewModel.IsStandOpen));
        DockPanel extra = new DockPanel { LastChildFill = true };
        Button back = new Button { Content = "Вернуться к виртуальному стенду", Margin = new Thickness(0, 8, 0, 12) };
        BindProperty(back, Button.CommandProperty, "VirtualStand.CloseConsultationCommand");
        DockPanel.SetDock(back, Dock.Top); extra.Children.Add(back); extra.Children.Add(consultation);
        BindProperty(extra, IsVisibleProperty, "VirtualStand.IsConsultationOpen");
        host.Children.Add(stand); host.Children.Add(extra); return host;
    }

    private static Control CreateKnowledgeWorkspace(KnowledgeEditorView editor) {
        Grid host = new Grid();
        ProfileKnowledgeView reference = new ProfileKnowledgeView();
        BindProperty(reference, DataContextProperty, nameof(MainViewModel.ProfileKnowledge));
        // видимость читаем у собственной VM справочника после смены DataContext
        BindProperty(reference, IsVisibleProperty, nameof(ProfileKnowledgeViewModel.IsReferenceOpen));
        DockPanel editorHost = new DockPanel { LastChildFill = true };
        Button back = new Button { Content = "Вернуться к справочнику", Margin = new Thickness(0, 8, 0, 12) };
        BindProperty(back, Button.CommandProperty, "ProfileKnowledge.OpenReferenceCommand"); DockPanel.SetDock(back, Dock.Top);
        editorHost.Children.Add(back); editorHost.Children.Add(editor);
        BindProperty(editorHost, IsVisibleProperty, "ProfileKnowledge.IsEditorOpen");
        host.Children.Add(reference); host.Children.Add(editorHost); return host;
    }

    private static ScrollViewer CreateMonitorPanel() {
        // вся страница прокручивается вместе: верхние поля не отнимают высоту у карточек
        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        StackPanel stand = new StackPanel { Spacing = 12 };
        stand.Children.Add(new TextBlock { Text = "Диагностика стенда ESP32", FontSize = 22, FontWeight = FontWeight.SemiBold });
        stand.Children.Add(CreatePortControls());
        stand.Children.Add(CreateBoundText(nameof(MainViewModel.Status)));
        stand.Children.Add(CreateBoundText(nameof(MainViewModel.StandIdentity)));
        LiveMonitorView live = new LiveMonitorView();
        BindProperty(live, DataContextProperty, nameof(MainViewModel.Live)); stand.Children.Add(live);
        ModuleChecksView modules = new ModuleChecksView();
        BindProperty(modules, DataContextProperty, nameof(MainViewModel.Modules)); stand.Children.Add(modules);
        Border surface = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), Child = stand };
        surface.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush"));
        panel.Children.Add(surface);
        OnlineDataView online = new OnlineDataView();
        BindProperty(online, DataContextProperty, nameof(MainViewModel.Online)); panel.Children.Add(online);
        LiveMonitorView explanation = new LiveMonitorView(detailsOnly: true);
        BindProperty(explanation, DataContextProperty, nameof(MainViewModel.Live));
        Expander selectedExplanation = new Expander { Header = "Объяснение автоматической стратегии и профиль", Content = explanation };
        BindProperty(selectedExplanation, Expander.IsExpandedProperty, "Live.ExplanationOpen", BindingMode.TwoWay);
        MainViewModel? attached = null;
        void ShowExplanation() => Avalonia.Threading.Dispatcher.UIThread.Post(() => selectedExplanation.BringIntoView());
        panel.DataContextChanged += (_, _) => {
            if (attached is not null) attached.Live.ExplanationRequested -= ShowExplanation;
            attached = panel.DataContext as MainViewModel;
            if (attached is not null) attached.Live.ExplanationRequested += ShowExplanation;
        };
        panel.Children.Add(selectedExplanation);
        panel.Children.Add(CreateBoundText(nameof(MainViewModel.Message)));
        StackPanel files = new StackPanel { Spacing = 8 };
        files.Children.Add(CreatePathInput(nameof(MainViewModel.CapturePath),
            "Новый файл записи при подключении (пусто — без записи), лимит 10 МиБ"));
        files.Children.Add(CreateReplayControls());
        files.Children.Add(CreateReplayFileControls());
        panel.Children.Add(new Expander { Header = "Запись и воспроизведение", Content = files });
        StackPanel details = new StackPanel { Spacing = 8 };
        details.Children.Add(CreateStatePanel());
        details.Children.Add(CreateObservationsPanel());
        panel.Children.Add(new Expander { Header = "Технические сведения и все источники", Content = details });
        return new ScrollViewer { Content = panel };
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

    private static ItemsControl CreateObservationsPanel() {
        ItemsControl items = new ItemsControl {
            ItemTemplate = new FuncDataTemplate<ObservationRow>((row, _) => CreateObservationCard(row))
        };
        BindProperty(items, ItemsControl.ItemsSourceProperty, nameof(MainViewModel.Observations));
        return items;
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