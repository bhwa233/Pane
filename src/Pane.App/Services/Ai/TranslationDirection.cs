using System.Text;

namespace Pane.Services.Ai;

internal static class TranslationDirection
{
    internal static string? Detect(string text)
    {
        var hangul = false;
        int han = 0, letters = 0, asciiLetters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var codePoint = rune.Value;
            if (codePoint is >= 0x3041 and <= 0x3096 or >= 0x30A1 and <= 0x30FA or >= 0xFF66 and <= 0xFF9D)
                return "ja";
            if (codePoint is >= 0x1100 and <= 0x11FF or >= 0x3131 and <= 0x318E or >= 0xAC00 and <= 0xD7A3)
                hangul = true;
            if (codePoint is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF)
                han++;
            else if (Rune.IsLetter(rune))
            {
                letters++;
                if (rune.IsAscii) asciiLetters++;
            }
        }

        if (hangul) return "ko";
        if (han > 0 && han >= letters / 4) return "zh";
        return asciiLetters >= (letters + han + 1) / 2 ? "en" : null;
    }

    internal static string Target(string text, string setting) => setting is "auto" or ""
        ? Detect(text) == "zh" ? "en" : "zh"
        : setting;
}
