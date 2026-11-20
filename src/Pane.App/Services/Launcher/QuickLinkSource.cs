using Pane.Contracts;
using Pane.Localization;
using Pane.Settings;
using static Pane.Settings.QuickLinkRules;

namespace Pane.Services.Launcher;

internal sealed record QuickLinkPrompt(string[] Fields, string Query);

internal sealed class QuickLinkSource(ILauncherExecutor executor, ILaunchProcess process)
    : LauncherSource(LauncherGroups.QuickLinks, 0, Prefix)
{
    const string Prefix = "quick:";

    public override int Limit(SourceContext context) => 6;

    public override Task<IReadOnlyList<LauncherItem>> SearchAsync(SourceContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var query = context.Query;
        if (query.Length == 0 || query.Length > QueryLimit || query.Any(char.IsControl)) return Rows([]);
        var split = query.IndexOf(' ');
        var keyword = split < 0 ? query : query[..split];
        var argument = split < 0 ? "" : query[(split + 1)..].Trim();
        var reserved = ReservedKeywords(context.Settings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabled = context.Settings.QuickLinks.Where(entry => entry.Enabled && !reserved.Contains(entry.Keyword));
        var exact = enabled.FirstOrDefault(entry => entry.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        if (exact != null && (argument.Length == 0 || NeedsQuery(exact))) return Rows([Item(exact, argument)]);
        return Rows(enabled.Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Keyword.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(Limit(context)).Select(entry => Item(entry, "")));
    }

    LauncherItem Item(QuickLink entry, string argument) => new(Prefix + entry.Id.ToString("N") + ":" + argument,
        LauncherItemKind.Action, Group, entry.Name, entry.Keyword,
        new(entry.Kind == QuickLinkKind.Web ? "globe" : "terminal"));

    static (QuickLink Link, string Query)? Resolve(LauncherItem item, LauncherSettings settings)
    {
        if (!item.Id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var key = item.Id.AsSpan(Prefix.Length);
        if (key.Length < 33 || key[32] != ':' || !Guid.TryParseExact(key[..32], "N", out var id) ||
            settings.QuickLinks.FirstOrDefault(entry => entry.Id == id && entry.Enabled) is not { } link ||
            ReservedKeywords(settings).Contains(link.Keyword, StringComparer.OrdinalIgnoreCase)) return null;
        return (link, key[33..].ToString());
    }

    internal static QuickLinkPrompt? Prompt(LauncherItem item, LauncherSettings settings)
    {
        if (Resolve(item, settings) is not { } entry) return null;
        var fields = Fields(entry.Link);
        return fields.Any(field => field != QueryField || entry.Query.Length == 0) ? new(fields, entry.Query) : null;
    }

    public override Task ExecuteAsync(LauncherItem item, SourceContext context, CancellationToken token) =>
        ExecuteAsync(item, context.Settings, null, token);

    internal Task ExecuteAsync(LauncherItem item, LauncherSettings settings, IReadOnlyDictionary<string, string>? supplied, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (link, query) = Resolve(item, settings) ?? throw new LauncherActionException(Strings.Launcher_ActionUnavailable);
        var fields = Fields(link);
        if (supplied?.Keys.Any(key => !fields.Contains(key, StringComparer.Ordinal)) == true)
            throw new LauncherActionException(Strings.QuickLinks_Invalid);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var value = supplied?.GetValueOrDefault(field) ?? (field == QueryField ? query : "");
            if (value.Length > QueryLimit || value.Any(char.IsControl)) throw new LauncherActionException(Strings.QuickLinks_Invalid);
            if (string.IsNullOrWhiteSpace(value)) throw new LauncherActionException(Strings.QuickLinks_FieldsRequired);
            values.Add(field, value);
        }

        if (link.Kind == QuickLinkKind.Web)
        {
            var expanded = Expand(link.Target, values, encode: true);
            if (!TryUrl(expanded, out var address)) throw new LauncherActionException(Strings.QuickLinks_Invalid);
            return executor.OpenAsync(address!.AbsoluteUri, false, token);
        }

        var working = link.WorkingDirectory.Length == 0 ? Path.GetDirectoryName(link.Target)! : link.WorkingDirectory;
        if (!File.Exists(link.Target) || !Directory.Exists(working)) throw new LauncherActionException(Strings.Launcher_ActionUnavailable);
        var arguments = ArgumentLines(link.Arguments).Select(value => Expand(value, values, encode: false)).ToArray();
        if (arguments.Sum(value => value.Length + 3L) > 16000) throw new LauncherActionException(Strings.QuickLinks_Invalid);
        return process.StartAsync(new(link.Target, arguments, working, Direct: true), token);
    }
}
