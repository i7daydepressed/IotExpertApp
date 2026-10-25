using System;
using System.Threading;
using System.Threading.Tasks;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Telemetry;

namespace IotExpertApp.ConsoleUI;

public static class TelemetryConsole
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || (args[1] != "--file" && args[1] != "--port"))
        {
            Console.WriteLine("--console --file <путь> | --console --port <имя>");
            Console.WriteLine("это технический приемник; экспертная консультация появится отдельным шагом");
            return 2;
        }
        await using TelemetryReceiverService receiver = new TelemetryReceiverService(new ObservationStore());
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (args[1] == "--file") await receiver.StartFileAsync(args[2]);
            else await receiver.StartSerialAsync(args[2]);
            string previous = "";
            ReceiverState state;
            do
            {
                await Task.Delay(200);
                state = receiver.GetState();
                string text = $"{state.Status}; принято={state.Valid}, ошибки={state.Invalid}, порядок={state.RejectedOrder}, пропуски={state.Missing}, логи={state.Logs}, длинные={state.Oversized}, хвосты={state.Tails}; {state.LastMessage}";
                if (text != previous) { Console.WriteLine(text); previous = text; }
                if (state.Status is "ошибка источника" or "файл прочитан (быстрый режим)") break;
            } while (!cancellation.IsCancellationRequested);
            await receiver.StopAsync();
            state = receiver.GetState();
            if (state.Snapshot is not null)
                foreach (Observation item in state.Snapshot.Packet.Observations)
                    Console.WriteLine($"{item.SourceId}: {item.Status}; {item.Values?.GetRawText() ?? "нет значения"}; {item.ErrorDetail}");
            return state.Status == "ошибка источника" || state.Invalid > 0 || state.RejectedOrder > 0 || state.Tails > 0 || state.Valid == 0 ? 1 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
