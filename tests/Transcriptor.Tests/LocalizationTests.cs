using System.Text.Json;
using System.Text.RegularExpressions;
using Transcriptor.Core;

namespace Transcriptor.Tests;

[CollectionDefinition("Interface language", DisableParallelization = true)]
public sealed class InterfaceLanguageCollection;

[Collection("Interface language")]
public sealed class LocalizationTests
{
    [Fact]
    public void InterfaceLanguageDoesNotChangeDictationLanguageOrShortcut()
    {
        var saved = new AppSettings { Language = "auto", Hotkey = "Ctrl + Shift + D", InterfaceLanguage = "en" };
        try
        {
            UiText.SetLanguage(saved.InterfaceLanguage);
            Assert.Equal("Settings", UiText.Get("Ajustes"));
            Assert.Equal("auto", saved.Language);
            Assert.True(HotkeyGesture.TryParse(saved.Hotkey, out var hotkey));
            Assert.Equal("Ctrl + Shift + D", hotkey.DisplayName);
            UiText.SetLanguage("es");
            Assert.Equal("Ajustes", UiText.Get("Ajustes"));
            Assert.Equal("Ctrl + Mayús + D", hotkey.DisplayName);
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!;
            Assert.Equal(saved, restored);
            Assert.Equal("es", JsonSerializer.Deserialize<AppSettings>("{}")!.InterfaceLanguage);
        }
        finally { UiText.SetLanguage("es"); }
    }

    [Fact]
    public void TranslationsKeepFormattingArgumentsAndChangeLiveLabels()
    {
        try
        {
            foreach (var (source, translated) in UiText.Catalog)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated));
                Assert.Equal(Regex.Matches(source, @"\{\d+(?::[^}]+)?\}").Select(m => m.Value),
                    Regex.Matches(translated, @"\{\d+(?::[^}]+)?\}").Select(m => m.Value));
            }
            UiText.SetLanguage("en");
            Assert.Equal("Listening…", UiText.TranslateDisplayed("Te escucho…"));
            Assert.Contains("1.5", UiText.Get("{0:0.0} s de voz  ·  {1:0.0} s de proceso  ·  {2}", 1.5, 0.2, "CPU"));
            Assert.Equal("Texto del usuario", UiText.TranslateDisplayed("Texto del usuario"));
            UiText.SetLanguage("es");
            Assert.Equal("Te escucho…", UiText.TranslateDisplayed("Listening…"));
        }
        finally { UiText.SetLanguage("es"); }
    }
}
