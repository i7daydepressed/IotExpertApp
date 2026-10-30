using System;
using System.Collections.Immutable;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class ModuleChecksView : UserControl {
    public ModuleChecksView() {
        StackPanel panel = new StackPanel { Spacing = 12 };
        TextBlock summary = Text(nameof(ModuleChecksViewModel.Summary)); summary.FontWeight = FontWeight.SemiBold; panel.Children.Add(summary);
        WrapPanel preference = new WrapPanel();
        preference.Children.Add(new TextBlock { Text = "Время на ответ, с: ", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        TextBox seconds = new TextBox { Width = 90, Margin = new Thickness(0, 0, 8, 8) };
        Bind(seconds, TextBox.TextProperty, nameof(ModuleChecksViewModel.ResponseSecondsText), BindingMode.TwoWay); preference.Children.Add(seconds);
        preference.Children.Add(Button("Сохранить срок", nameof(ModuleChecksViewModel.SavePreferencesCommand)));
        preference.Children.Add(Button("Выключить оба реле", nameof(ModuleChecksViewModel.AllOffCommand)));
        panel.Children.Add(preference); panel.Children.Add(Text(nameof(ModuleChecksViewModel.SettingsMessage)));
        CheckBox readiness = new CheckBox { Content = new TextBlock { Text = "Готов испытать выбранное реле при известных текущих условиях нагрузки (затем отдельный запуск 900 мс)", TextWrapping = TextWrapping.Wrap } };
        Bind(readiness, CheckBox.IsCheckedProperty, nameof(ModuleChecksViewModel.RelayReady), BindingMode.TwoWay);
        Bind(readiness, IsEnabledProperty, nameof(ModuleChecksViewModel.Compatible)); panel.Children.Add(readiness);
        ItemsControl cards = new ItemsControl {
            ItemsPanel = new FuncTemplate<Panel?>(() => new ModuleTilePanel()),
            ItemTemplate = new FuncDataTemplate<ModuleCardViewModel>((card, _) => Tile(card))
        };
        Bind(cards, ItemsControl.ItemsSourceProperty, nameof(ModuleChecksViewModel.Cards));
        Border selected = ActiveCheck();
        ModuleCheckLayout composition = new ModuleCheckLayout(); composition.Children.Add(cards); composition.Children.Add(selected); panel.Children.Add(composition);
        ModuleChecksViewModel? attached = null;
        void FocusSelection() => Dispatcher.UIThread.Post(() => selected.BringIntoView());
        DataContextChanged += (_, _) => {
            if (attached is not null) attached.SelectionRequested -= FocusSelection;
            attached = DataContext as ModuleChecksViewModel;
            if (attached is not null) attached.SelectionRequested += FocusSelection;
        };
        StackPanel technical = new StackPanel { Spacing = 8 };
        technical.Children.Add(Text(nameof(ModuleChecksViewModel.Identity)));
        technical.Children.Add(Text(nameof(ModuleChecksViewModel.Message)));
        technical.Children.Add(Text(nameof(ModuleChecksViewModel.SettingsPath)));
        WrapPanel commands = new WrapPanel();
        commands.Children.Add(Button("Обновить возможности (read-only)", nameof(ModuleChecksViewModel.CapabilitiesCommand)));
        commands.Children.Add(Button("Status ожидающего ID", nameof(ModuleChecksViewModel.PendingStatusCommand)));
        Button save = new Button { Content = "Сохранить протокол в JSON" };
        save.Click += async (_, _) => {
            if (DataContext is not ModuleChecksViewModel model) return;
            try {
                IStorageFile? file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                    Title = "Сохранить протокол проверок", SuggestedFileName = "module-checks.json", DefaultExtension = "json",
                    FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } }
                });
                if (file is null) return;
                await using Stream stream = await file.OpenWriteAsync(); stream.SetLength(0); await model.ExportAsync(stream);
                model.Message = "Протокол сохранен";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { model.Message = "Не удалось сохранить: " + exception.Message; }
        };
        commands.Children.Add(save); technical.Children.Add(commands);
        panel.Children.Add(new Expander { Header = "История, команды и технические сведения", Content = technical });
        Content = panel;
    }
    private static Control Tile(ModuleCardViewModel? card) {
        if (card is null) return new TextBlock();
        StackPanel p = new StackPanel { Spacing = 6 };
        TextBlock title = Text(nameof(ModuleCardViewModel.Title)); title.FontSize = 16; title.FontWeight = FontWeight.SemiBold; p.Children.Add(title);
        TextBlock state = Text(nameof(ModuleCardViewModel.TileStatus)); state.FontWeight = FontWeight.SemiBold; p.Children.Add(state);
        p.Children.Add(Text(nameof(ModuleCardViewModel.Brief)));
        if (card.Source == "pir") p.Children.Add(Text(nameof(ModuleCardViewModel.ObservationLimitation)));
        TextBlock result = Text(nameof(ModuleCardViewModel.GuidedSummary)); result.FontSize = 12;
        if (card.Source != "pir") { result.MaxLines = 2; result.TextTrimming = TextTrimming.CharacterEllipsis; }
        TextBlock caption = Text(nameof(ModuleCardViewModel.CheckCaption)); caption.FontSize = 12;
        p.Children.Add(caption); p.Children.Add(result);
        p.Children.Add(Button("Проверить", nameof(ModuleCardViewModel.StartCommand)));
        Border tile = new Border { DataContext = card, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Child = p };
        tile.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundAltHighBrush"));
        return tile;
    }
    private static Border ActiveCheck() {
        StackPanel p = new StackPanel { Spacing = 8 };
        TextBlock title = Text(nameof(ModuleChecksViewModel.SelectedTitle)); title.FontSize = 18; title.FontWeight = FontWeight.SemiBold; p.Children.Add(title);
        p.Children.Add(Text(nameof(ModuleChecksViewModel.PhaseText))); p.Children.Add(Text(nameof(ModuleChecksViewModel.Instruction)));
        StackPanel window = new StackPanel { Spacing = 4 };
        window.Children.Add(new TextBlock { Text = "Измерительное окно GPIO, мс (отдельно от срока ответа)", TextWrapping = TextWrapping.Wrap });
        NumericUpDown duration = new NumericUpDown { Minimum = 100, Maximum = 10000, Increment = 100, Width = 150, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        Bind(duration, NumericUpDown.ValueProperty, nameof(ModuleChecksViewModel.WindowDuration), BindingMode.TwoWay); window.Children.Add(duration);
        Bind(window, IsVisibleProperty, nameof(ModuleChecksViewModel.IsObserveSelected)); p.Children.Add(window);
        Button begin = Button("Начать проверку / окно", nameof(ModuleChecksViewModel.BeginCommand));
        Bind(begin, IsEnabledProperty, nameof(ModuleChecksViewModel.CanBegin)); p.Children.Add(begin);
        p.Children.Add(Text(nameof(ModuleChecksViewModel.MachineText)));
        TextBlock question = Text(nameof(ModuleChecksViewModel.Question)); question.FontWeight = FontWeight.SemiBold; p.Children.Add(question);
        p.Children.Add(Text(nameof(ModuleChecksViewModel.Remaining)));
        WrapPanel answers = new WrapPanel();
        foreach (string answer in new[] { "Да", "Нет", "Неясно", "Пропустить" }) {
            Button button = Button(answer, nameof(ModuleChecksViewModel.GiveAnswerCommand)); button.CommandParameter = answer == "Пропустить" ? "Не проверял" : answer;
            Bind(button, IsEnabledProperty, nameof(ModuleChecksViewModel.CanAnswer)); answers.Children.Add(button);
        }
        p.Children.Add(answers); p.Children.Add(Text(nameof(ModuleChecksViewModel.HumanText)));
        TextBlock verdict = Text(nameof(ModuleChecksViewModel.Verdict)); verdict.FontWeight = FontWeight.SemiBold; p.Children.Add(verdict);
        p.Children.Add(Button("Почему такой результат?", nameof(ModuleChecksViewModel.ExplainCheckCommand)));
        StackPanel proof = new StackPanel { Spacing = 8 };
        proof.Children.Add(Text(nameof(ModuleChecksViewModel.ModuleExplanationHistory)));
        proof.Children.Add(Text(nameof(ModuleChecksViewModel.ModuleExplanationContext)));
        proof.Children.Add(ResultExplanationView.For(nameof(ModuleChecksViewModel.FrozenModuleExplanation)));
        proof.Children.Add(new Expander { Header = new TextBlock { Text = "Подробности закрепленной проверки", TextWrapping = TextWrapping.Wrap, MaxWidth = 240 }, Content = Text(nameof(ModuleChecksViewModel.ModuleExplanationDetails)) });
        Expander explanation = new Expander { Header = new TextBlock { Text = "Закрепленное объяснение проверки", TextWrapping = TextWrapping.Wrap, MaxWidth = 240 }, Content = proof };
        Bind(explanation, Expander.IsExpandedProperty, nameof(ModuleChecksViewModel.ModuleExplanationOpen), BindingMode.TwoWay); p.Children.Add(explanation);
        StackPanel details = new StackPanel { Spacing = 6 };
        details.Children.Add(Text(nameof(ModuleChecksViewModel.WorkflowDetails)));
        details.Children.Add(Button("Восстановить по status", nameof(ModuleChecksViewModel.RecoverCommand)));
        details.Children.Add(Button("Повторить finish после status", nameof(ModuleChecksViewModel.RetryFinishCommand)));
        p.Children.Add(new Expander { Header = new TextBlock { Text = "Параметры и восстановление", TextWrapping = TextWrapping.Wrap, MaxWidth = 240 }, Content = details });
        Border active = new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Child = p };
        active.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlBackgroundAltHighBrush")); return active;
    }
    private static TextBlock Text(string property) { TextBlock text = new TextBlock { TextWrapping = TextWrapping.Wrap }; Bind(text, TextBlock.TextProperty, property); return text; }
    private static Button Button(string text, string command) { Button button = new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 }, Margin = new Thickness(0, 0, 8, 8) }; Bind(button, Avalonia.Controls.Button.CommandProperty, command); return button; }
    private static void Bind(Control control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay) => control.Bind(property, new ReflectionBinding(path) { Mode = mode });
}
