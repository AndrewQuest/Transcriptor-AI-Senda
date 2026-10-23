using System.Globalization;
using System.Text.Json;

namespace Transcriptor.Core;

/// <summary>Spanish source messages with an embedded English catalog, independent of speech language.</summary>
public static class UiText
{
    private static readonly IReadOnlyDictionary<string, string> English = LoadCatalog();
    private static string _language = "es";
    public static string Language => Volatile.Read(ref _language);
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public static IReadOnlyDictionary<string, string> Catalog => English;
    public static event Action? Changed;

    public static void SetLanguage(string? language)
    {
        var next = language == "en" ? "en" : "es";
        if (Interlocked.Exchange(ref _language, next) != next) Changed?.Invoke();
    }

    public static string Get(string source, params object[] arguments)
    {
        var text = Language == "en" && English.TryGetValue(source, out var translated) ? translated : source;
        return arguments.Length == 0 ? text : string.Format(Culture, text, arguments);
    }

    public static string TranslateDisplayed(string text)
    {
        if (English.ContainsKey(text)) return Get(text);
        var source = English.FirstOrDefault(entry => entry.Value == text).Key;
        return source is null ? text : Get(source);
    }

    private static IReadOnlyDictionary<string, string> LoadCatalog()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("Transcriptor.Core.Localization.en.json")
            ?? throw new InvalidOperationException("Missing embedded interface language catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
