using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using IotExpertApp.Services.Telemetry;
using IotExpertApp.ViewModels;
using IotExpertApp.Views;

namespace IotExpertApp;

public class App : Application {
    public override void Initialize() {
        RequestedThemeVariant = ThemeVariant.Default;
        DataTemplates.Add(new ViewLocator());
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            // создаем один приемник на время жизни окна и передаем его во ViewModel
            ObservationStore store = new ObservationStore();
            TelemetryReceiverService receiver = new TelemetryReceiverService(store);
            MainViewModel viewModel = new MainViewModel(receiver);
            MainWindow window = new MainWindow { DataContext = viewModel };
            bool shutdownComplete = false;
            bool shutdownStarted = false;
            window.Closing += async (_, eventArgs) => {
                if (shutdownComplete) return;
                eventArgs.Cancel = true;
                // повторное закрытие ждет уже начатую остановку, не запускаем второй вызов ShutdownAsync
                if (shutdownStarted) return;
                shutdownStarted = true;
                await viewModel.ShutdownAsync();
                shutdownComplete = true;
                window.Close();
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}