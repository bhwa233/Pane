using Pane.Localization;
using Pane.Services.Ai;
namespace Pane.Views.Controls;

public static class Languages
{
    public static readonly (string Code, string Name)[] All =
        [("zh", Strings.Translate_LanguageChinese), ("en", Strings.Translate_LanguageEnglish), ("ja", Strings.Translate_LanguageJapanese), ("ko", Strings.Translate_LanguageKorean), ("fr", Strings.Translate_LanguageFrench), ("de", Strings.Translate_LanguageGerman), ("es", Strings.Translate_LanguageSpanish), ("ru", Strings.Translate_LanguageRussian)];

    public static bool IsChinese(string text) => TranslationDirection.Detect(text) == "zh";

    public static string Detect(string text) => TranslationDirection.Detect(text) is { } code ? NameOf(code) : Strings.Translate_LanguageForeign;

    public static string AutoTarget(string text, string setting) => TranslationDirection.Target(text, setting);

    public static string NameOf(string code) => All.FirstOrDefault(l => l.Code == code).Name ?? code;
}
