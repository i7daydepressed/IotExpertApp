using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;

namespace IotExpertApp.Services.Knowledge;

// общие ограничения применяются и к БЗ, и к исходным фактам
public static class KnowledgeJson {
    public const int MaximumBytes = 1024 * 1024;

    public static byte[] ReadBounded(string path) {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[MaximumBytes + 1];
        int count = 0;
        while (count < buffer.Length) {
            int read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        Require(count <= MaximumBytes, "файл больше 1 МиБ");
        return buffer.AsSpan(0, count).ToArray();
    }

    public static JsonDocument Parse(byte[] bytes) {
        Require(bytes.Length <= MaximumBytes, "файл больше 1 МиБ");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
        JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        try { CheckDuplicates(document.RootElement, "$ "); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void CheckDuplicates(JsonElement value, string path) {
        if (value.ValueKind == JsonValueKind.Object) {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject()) {
                Require(names.Add(property.Name), path + ": повтор поля " + property.Name);
                CheckDuplicates(property.Value, path + "." + property.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in value.EnumerateArray()) CheckDuplicates(child, path + "[]");
    }

    public static void Shape(JsonElement value, string path, string[] required, params string[] optional) {
        Require(value.ValueKind == JsonValueKind.Object, path + ": нужен объект");
        HashSet<string> allowed = new HashSet<string>(required.Concat(optional), StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            Require(allowed.Contains(property.Name), path + ": неизвестное поле " + property.Name);
        foreach (string name in required)
            Require(value.TryGetProperty(name, out _), path + ": нет поля " + name);
    }

    public static string Text(JsonElement value, string path) {
        Require(value.ValueKind == JsonValueKind.String, path + ": нужна строка");
        string text = value.GetString()!;
        Require(!string.IsNullOrWhiteSpace(text) && text.Length <= 4096, path + ": пустая или слишком длинная строка");
        return text;
    }

    public static string Id(JsonElement value, string path) {
        string text = Text(value, path);
        Require(text.Length <= 64 && text.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.'),
            path + ": ID должен содержать 1..64 ASCII-буквы, цифры, _, - или .");
        return text;
    }

    public static decimal Number(JsonElement value, string path) {
        Require(value.ValueKind == JsonValueKind.Number, path + ": нужно число");
        string raw = value.GetRawText();
        Require(raw.Length <= 128 && value.TryGetDecimal(out _), path + ": число вне поддерживаемого decimal");
        decimal number = value.GetDecimal();
        // проверка не дает decimal незаметно округлить слишком точный JSON-токен
        Require(Normalize(raw) == Normalize(number.ToString(CultureInfo.InvariantCulture)),
            path + ": число нельзя представить как decimal без округления");
        return number;
    }

    // представляем число как знак, значащие цифры и степень десяти: 24.0 и 2.40e1 дают одинаковое 24e0
    // степень учитывает точку и убранные хвостовые нули, а отдельный ноль не зависит от знака или масштаба
    // сравнение таких форм до и после decimal обнаруживает округление, не проходя через double
    private static string Normalize(string token) {
        string[] parts = token.ToLowerInvariant().Split('e');
        int exponent = 0;
        if (parts.Length == 2)
            Require(int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                && exponent >= -1000 && exponent <= 1000, "неподдерживаемая экспонента числа");
        string mantissa = parts[0];
        bool negative = mantissa.StartsWith("-", StringComparison.Ordinal);
        if (negative) mantissa = mantissa.Substring(1);
        int point = mantissa.IndexOf('.');
        int power = exponent - (point < 0 ? 0 : mantissa.Length - point - 1);
        string digits = mantissa.Replace(".", "").TrimStart('0');
        if (digits.Length == 0) return "0";
        int before = digits.Length;
        digits = digits.TrimEnd('0');
        power += before - digits.Length;
        return (negative ? "-" : "") + digits + "e" + power.ToString(CultureInfo.InvariantCulture);
    }

    public static KnowledgeValue Value(JsonElement value, FactDefinition definition, string path) {
        switch (definition.Type) {
            case FactType.Boolean:
                Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, path + ": нужен bool");
                return new KnowledgeValue(FactType.Boolean, value.GetBoolean() ? "true" : "false");
            case FactType.Enum:
                string text = Text(value, path);
                Require(definition.Options.Contains(text, StringComparer.Ordinal), path + ": значение вне enum");
                return new KnowledgeValue(FactType.Enum, text);
            default:
                decimal number = Number(value, path);
                Require(definition.Type != FactType.Integer || decimal.Truncate(number) == number, path + ": нужно целое число");
                Require((!definition.Minimum.HasValue || number >= definition.Minimum.Value)
                    && (!definition.Maximum.HasValue || number <= definition.Maximum.Value), path + ": число вне диапазона");
                return new KnowledgeValue(definition.Type, number.ToString("G29", CultureInfo.InvariantCulture));
        }
    }

    public static void Require(bool condition, string message) {
        if (!condition) throw new InvalidDataException(message);
    }
}
