using System;
using System.IO;
using System.Text.Json;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.Services.Checks;

public static class CheckPreferences {
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IotExpertApp", "check-preferences.json");
    public static int Parse(string text) {
        CheckProtocol.Require(int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds)
            && seconds is >= 5 and <= 300, "Время на ответ: целое число 5…300 с");
        return seconds;
    }
    public static (int Seconds, string Message) Load(string path) {
        try {
            if (!File.Exists(path)) return (30, "Время на ответ: 30 с; настройки еще не сохранены");
            using FileStream input = File.OpenRead(path);
            CheckProtocol.Require(input.Length <= 4096, "слишком большой файл настроек");
            byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            using JsonDocument json = KnowledgeJson.Parse(bytes);
            KnowledgeJson.Shape(json.RootElement, "настройки", new[] { "response_seconds" });
            int seconds = json.RootElement.GetProperty("response_seconds").GetInt32(); Parse(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return (seconds, "Срок ответа загружен");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException) {
            return (30, "Используется 30 с: " + e.Message);
        }
    }
    public static void Save(string path, string text) {
        int seconds = Parse(text);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { response_seconds = seconds }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
