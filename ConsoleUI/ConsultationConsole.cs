using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Consultation;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.ConsoleUI;

public static class ConsultationConsole {
    public static int Run(string[] args) => Run(args, Console.In, Console.Out, Console.Error);

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error) {
        if (!TryOptions(args, out string knowledgePath, out string? factsPath)) {
            error.WriteLine("использование: --consult [--knowledge <БЗ.json>] [--facts <входы.json>]");
            return 2;
        }
        ConsultationService session;
        try { session = LoadSession(); }
        catch (Exception exception) when (IsDataError(exception)) { error.WriteLine("ошибка загрузки: " + exception.Message); return 1; }
        PrintHeader();
        while (true) {
            FactDefinition? question = session.NextQuestion;
            if (question is not null) {
                output.WriteLine("Вопрос " + question.Id + ": " + question.Question);
                if (question.Type == FactType.Boolean) output.WriteLine("  да / нет / не знаю");
                else if (question.Type == FactType.Enum) {
                    for (int index = 0; index < question.Options.Length; index++) output.WriteLine("  " + (index + 1) + ") " + question.Options[index]);
                    output.WriteLine("  не знаю — значение останется неизвестным");
                }
                else {
                    output.WriteLine("  число в JSON-формате с точкой; не знаю — неизвестное значение");
                    output.WriteLine("  диапазон: " + (question.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "без нижней границы")
                        + " .. " + (question.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "без верхней границы"));
                }
            }
            else PrintSummary();
            output.Write("> ");
            string? line = input.ReadLine();
            if (line is null) { output.WriteLine("Ввод завершен; сеанс закрыт"); return 0; }
            string text = line.Trim();
            string command = text.ToLowerInvariant();
            if (command is "отмена" or "cancel") { session.Cancel(); output.WriteLine("Консультация отменена"); return 0; }
            if (command is "выход" or "exit" or "завершить") return 0;
            if (command is "почему" or "почему?" or "why") { output.WriteLine(session.ExplainWhy()); continue; }
            if (command is "как" or "how") { output.WriteLine(session.ExplainHow()); continue; }
            if (command is "факты" or "facts") {
                foreach (string id in session.Knowledge.QuestionOrder) {
                    string value = session.Answers.TryGetValue(id, out InputFact? fact)
                        ? (fact.Value?.Canonical ?? "неизвестно (ответ получен)") : "ответа нет";
                    output.WriteLine(id + " = " + value + (fact is null ? "" : " | " + fact.Origin));
                }
                continue;
            }
            if (command is "новая" or "new") {
                try {
                    ConsultationService replacement = LoadSession();
                    session = replacement;
                    PrintHeader();
                }
                catch (Exception exception) when (IsDataError(exception)) {
                    error.WriteLine("новая БЗ не загружена, прежний сеанс сохранен: " + exception.Message);
                }
                continue;
            }
            string[] edit = text.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (edit.Length > 0 && (edit[0].Equals("изменить", StringComparison.OrdinalIgnoreCase) || edit[0].Equals("edit", StringComparison.OrdinalIgnoreCase))) {
                if (edit.Length != 3) { error.WriteLine("использование: изменить <ID> <ответ>"); continue; }
                if (!session.TryAnswer(edit[1], edit[2], out string editError)) error.WriteLine("ответ не принят: " + editError);
                else output.WriteLine("Ответ " + edit[1] + " изменен; производные факты пересчитаны");
                continue;
            }
            if (question is null) { error.WriteLine("доступны: как, факты, изменить <ID> <ответ>, новая, выход"); continue; }
            if (!session.TryAnswer(question.Id, text, out string answerError)) error.WriteLine("ответ не принят: " + answerError);
        }

        ConsultationService LoadSession() {
            KnowledgeBase knowledge = new KnowledgeBaseService().Load(knowledgePath);
            ImmutableDictionary<string, InputFact>? facts = factsPath is null ? null : new InputFactService().Load(factsPath, knowledge);
            return new ConsultationService(knowledge, facts);
        }

        void PrintHeader() {
            output.WriteLine("Ручная консультация: диагностическое заключение и ограничения задает оператор; UART не используется");
            if (session.Knowledge.Synthetic) output.WriteLine("ИСКУССТВЕННАЯ БЗ — проверка алгоритма");
            output.WriteLine("БЗ " + session.Knowledge.Id + ", версия " + session.Knowledge.Version + ", SHA256 " + session.Knowledge.Sha256);
            output.WriteLine("Снимок сеанса: " + session.StartedAt.ToString("O"));
            output.WriteLine("Команды: почему, как, факты, изменить <ID> <ответ>, новая, отмена, выход");
        }

        void PrintSummary() {
            ConsultationOutcome? outcome = session.Outcome;
            if (outcome is not null) {
                string label = outcome.Kind switch {
                    ConsultationOutcomeKind.Strategy => "Рекомендация",
                    ConsultationOutcomeKind.Clarification => "Требуется уточнение",
                    _ => "Отказ от рекомендации"
                };
                output.WriteLine(label + ": " + outcome.Value + " — " + outcome.Title);
                output.WriteLine(outcome.Description);
            }
            else {
                output.WriteLine("Исход вывода: " + session.Result.Status);
                if (session.Result.MissingInputs.Length > 0) output.WriteLine("Недостающие данные: " + string.Join(", ", session.Result.MissingInputs));
                output.WriteLine("Подробности и фактическая трасса: команда как");
            }
        }
    }

    private static bool IsDataError(Exception exception) => exception is IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException;

    private static bool TryOptions(string[] args, out string knowledge, out string? facts) {
        knowledge = Path.Combine(AppContext.BaseDirectory, "Knowledge", "lr1-strategies.json");
        facts = null;
        if (args.Length == 0 || args[0] != "--consult" || args.Length % 2 != 1) return false;
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2) {
            string option = args[index];
            if (!seen.Add(option) || string.IsNullOrWhiteSpace(args[index + 1])) return false;
            if (option == "--knowledge") knowledge = args[index + 1];
            else if (option == "--facts") facts = args[index + 1];
            else return false;
        }
        return true;
    }
}
