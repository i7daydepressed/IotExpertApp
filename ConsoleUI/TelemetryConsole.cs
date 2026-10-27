using System;
using System.Threading;
using System.Threading.Tasks;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Telemetry;

namespace IotExpertApp.ConsoleUI;

public static class TelemetryConsole {
    public static async Task<int> RunAsync(string[] args) {
        if (args.Length < 3 || (args[1] != "--file" && args[1] != "--port")) return Usage();
        bool serial = args[1] == "--port";
        bool timed = false;
        string? record = null;
        int seconds = 0;
        for (int index = 3; index < args.Length; index++) {
            if (args[index] == "--timed" && !serial) timed = true;
            else if (args[index] == "--record" && serial && index + 1 < args.Length) record = args[++index];
            else if (args[index] == "--seconds" && index + 1 < args.Length &&
                int.TryParse(args[++index], out int parsed) && parsed is >= 1 and <= 300) seconds = parsed;
            else return Usage();
        }
        await using TelemetryReceiverService receiver = new TelemetryReceiverService(new ObservationStore());
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        if (seconds > 0) cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try {
            if (serial) await receiver.StartSerialAsync(args[2], record);
            else await receiver.StartFileAsync(args[2], timed);
            string previous = "";
            ReceiverState state;
            do {
                await Task.Delay(200);
                state = receiver.GetState();
                string text = $"{state.Status}; принято={state.Valid}, ошибки={state.Invalid}, наблюдения={state.TemporalRejected}, порядок={state.RejectedOrder}, пропуски={state.Missing}, логи={state.Logs}, длинные={state.Oversized}, хвосты={state.Tails}; {state.LastMessage}; {state.Capture}";
                if (text != previous) { Console.WriteLine(text); previous = text; }
                if (!state.Running) break;
            } while (!cancellation.IsCancellationRequested);
            await receiver.StopAsync();
            state = receiver.GetState();
            Console.WriteLine("итог: " + state.Status + "; " + state.Capture);
            if (state.Snapshot is not null)
                foreach (Observation item in state.Snapshot.Packet.Observations)
                    Console.WriteLine($"{item.SourceId}: {item.Status}; {item.Values?.GetRawText() ?? "нет значения"}; {item.ErrorDetail}");
            bool recordingError = state.Capture.StartsWith("запись не начата", StringComparison.Ordinal) ||
                state.Capture.StartsWith("ошибка", StringComparison.Ordinal);
            return state.Status == "ошибка источника" || recordingError || state.Invalid > 0 || state.TemporalRejected > 0 ||
                state.RejectedOrder > 0 || state.Tails > 0 || state.Valid == 0 ? 1 : 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static int Usage() {
        Console.WriteLine("--console --file <путь> [--timed] [--seconds 1..300]");
        Console.WriteLine("--console --port <имя> [--record <новый файл>] [--seconds 1..300]");
        Console.WriteLine("это технический приемник; экспертная консультация появится отдельным шагом");
        return 2;
    }
}
