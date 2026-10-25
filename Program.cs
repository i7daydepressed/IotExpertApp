using Avalonia;
using System;
using IotExpertApp.ConsoleUI;
using System.IO;

namespace IotExpertApp;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-synthetic-sample")
        {
            File.WriteAllText(args[1], "synthetic fixture; not a physical capture\n" + ReceiverChecks.CreateSyntheticPacket() + "\n");
            return 0;
        }
        if (args.Length > 0 && args[0] == "--self-test") return ReceiverChecks.Run();
        if (args.Length > 0 && args[0] == "--console")
            return TelemetryConsole.RunAsync(args).GetAwaiter().GetResult();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
