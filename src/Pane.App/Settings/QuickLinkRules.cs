using System.Text.RegularExpressions;

namespace Pane.Settings;

internal static partial class QuickLinkRules
{
    internal const string QueryField = "query";
    internal const int EntryLimit = 200;
    internal const int QueryLimit = 4096;

    [GeneratedRegex(@"\{([\p{L}_][\p{L}\p{N}_-]{0,39})\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholders();

    internal static bool NeedsQuery(QuickLink link) => Template(link).Contains("{query}", StringComparison.Ordinal);
    internal static string[] Fields(QuickLink link) => Placeholders().Matches(Template(link)).Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal).ToArray();
    static string Template(QuickLink link) => link.Kind == QuickLinkKind.Web ? link.Target : link.Arguments;

    internal static string Expand(string template, IReadOnlyDictionary<string, string> values, bool encode) =>
        Placeholders().Replace(template, match => encode ? Uri.EscapeDataString(values[match.Groups[1].Value]) : values[match.Groups[1].Value]);

    internal static IEnumerable<string> ReservedKeywords(LauncherSettings settings) => settings.SearchEngines.Select(engine => engine.Keyword)
        .Concat(settings.TranslateKeywords).Concat(settings.AskKeywords).Concat(settings.FilesKeywords);

    internal static bool ValidEntries(LauncherSettings settings)
    {
        var entries = settings.QuickLinks;
        if (entries.Length == 0) return true;
        if (entries.Length > EntryLimit) return false;
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        return entries.All(entry => Valid(entry) && ids.Add(entry.Id) && keywords.Add(entry.Keyword));
    }

    static bool Valid(QuickLink? link)
    {
        if (link == null || link.Id == Guid.Empty || link.Name is not { Length: > 0 and <= 80 } name || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ||
            link.Keyword is not { Length: > 0 and <= 32 } keyword || keyword.StartsWith('>') || keyword.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)) ||
            link.Target is not { Length: > 0 and <= 4096 } target || link.Arguments == null || link.WorkingDirectory == null)
            return false;
        if (!ValidTemplate(Template(link)) || Fields(link).Length > 16) return false;
        if (link.Kind == QuickLinkKind.Web)
            return link.Arguments.Length == 0 && link.WorkingDirectory.Length == 0 && ValidUrlTemplate(target);
        return link.Kind == QuickLinkKind.Program && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && LocalPath(target) &&
            (link.WorkingDirectory.Length == 0 || LocalPath(link.WorkingDirectory)) && link.Arguments.Length <= 4096 &&
            ArgumentLines(link.Arguments) is { Length: <= 32 } args && args.All(arg => !arg.Any(char.IsControl));
    }

    static bool LocalPath(string value) => value.Length is >= 3 and <= 4096 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
        value[2] == '\\' && value.AsSpan(2).IndexOfAny('"', ':') < 0 && !value.Any(char.IsControl) &&
        !value.Contains('{') && !value.Contains('}');

    internal static string[] ArgumentLines(string value) => value.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    static bool ValidTemplate(string template) => Placeholders().Replace(template, "").IndexOfAny(['{', '}']) < 0;

    static bool ValidUrlTemplate(string template)
    {
        var scheme = template.IndexOf(':');
        if (scheme < 1 || template.AsSpan(0, scheme).Contains('{')) return false;
        var remainder = template.AsSpan(scheme + 1);
        var hierarchical = remainder.StartsWith("//", StringComparison.Ordinal);
        if (!hierarchical && !template.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return false;
        var authority = hierarchical ? remainder[2..] : remainder;
        var delimiter = authority.IndexOfAny('/', '?', '#');
        if ((delimiter < 0 ? authority : authority[..delimiter]).IndexOf('{') >= 0) return false;
        return TryUrl(Placeholders().Replace(template, "pane"), out _);
    }

    internal static bool TryUrl(string value, out Uri? uri)
    {
        uri = null;
        if (value.Length > 16384 || value.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch) || ch == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !parsed.IsWellFormedOriginalString() || parsed.UserInfo.Length != 0)
            return false;
        if (parsed.Scheme is "http" or "https" ? parsed.Host.Length == 0 :
            parsed.Scheme is not ("vscode" or "vscode-insiders" or "obsidian" or "notion" or "zotero" or "mailto")) return false;
        var authorityStart = value.IndexOf(':') + 1;
        if (value.AsSpan(authorityStart).StartsWith("//", StringComparison.Ordinal))
        {
            var content = value[(authorityStart + 2)..];
            var pathStart = content.IndexOfAny(['/', '?', '#']);
            if (pathStart >= 0 && content[pathStart] == '/')
            {
                var path = content[pathStart..].Split(['?', '#'], 2)[0];
                if (path.Split('/').Any(segment => segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase) is "." or "..")) return false;
            }
        }
        uri = parsed;
        return true;
    }
}
