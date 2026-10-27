using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ConsoleUI;

public static class KnowledgeConsole {
    public static int Run(string[] args) {
        if (args.Length != 5 || args[0] != "--infer" || args[1] != "--knowledge" || args[3] != "--facts"
            || string.IsNullOrWhiteSpace(args[2]) || string.IsNullOrWhiteSpace(args[4])) {
            Console.Error.WriteLine("использование: --infer --knowledge <БЗ.json> --facts <факты.json>");
            return 2;
        }
        try {
            KnowledgeBase knowledge = new KnowledgeBaseService().Load(args[2]);
            ImmutableDictionary<string, InputFact> inputs = new InputFactService().Load(args[4], knowledge);
            InferenceResult result = new InferenceService().Run(knowledge, inputs);
            Console.Write(new ExplanationService().Describe(result));
            return result.Status == InferenceStatus.Success ? 0 : result.Status is InferenceStatus.InsufficientData or InferenceStatus.NoMatch ? 3 : 4;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or InvalidOperationException) {
            Console.Error.WriteLine("ошибка БЗ или входных данных: " + exception.Message);
            return 1;
        }
    }
}
