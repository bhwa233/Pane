using Pane.Contracts;
using Pane.Services.Launcher;
using Pane.Settings;

static partial class Program
{
    static async Task QuickLinkTests()
    {
        var folder = TestResults.NewDirectory("quick-links-");
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, "Fixture Program.exe");
        File.WriteAllText(executable, "fixture, never launched");
        var fixedLink = new QuickLink(Guid.NewGuid(), "Dashboard", "dash", QuickLinkKind.Web, "https://example.test/dashboard");
        var templated = new QuickLink(Guid.NewGuid(), "Issues", "issues", QuickLinkKind.Web, "https://example.test/issues?q={query}");
        var repository = new QuickLink(Guid.NewGuid(), "Repository", "repo", QuickLinkKind.Web, "https://example.test/{owner}/{repo}?q={query}");
        const string fieldName = "\u6587\u4ef6";
        var program = new QuickLink(Guid.NewGuid(), "Viewer", "view-it", QuickLinkKind.Program, executable, "--find\r\n{query}\r\n{" + fieldName + "}");
        var settings = Defaults();
        var launcher = settings.Launcher with { QuickLinks = [fixedLink, templated, repository, program] };
        new SettingsStore(folder, settings).Save(settings with { Launcher = launcher });
        var store = new SettingsStore(folder, settings);
        store.Load();
        var executor = new FakeExecutor();
        var process = new Process037();
        using var search = new LauncherSearch(store, new FixtureIndex(new FakeCatalog()), new FakeFiles(), executor,
            new Environment037(), process, new TerminalPrograms([], []));
        await search.Indexing;

        async Task<LauncherItem> Find(string query) => (await search.SearchAsync(query, default)).Items.Single(row => row.Group == LauncherGroups.QuickLinks);
        var dashboard = await Find("DASH");
        const string argument = "one two & \"three\"";
        var issues = await Find("issues " + argument);
        var repositoryRow = await Find("repo filter");
        var blankQuery = await Find("issues");
        var viewer = await Find("view-it " + argument);
        Check(executor.Calls.Count == 0 && process.Requests.Count == 0, "quick link search only yields results");
        Check(search.GetQuickLinkPrompt(blankQuery) is { Query: "", Fields: ["query"] } && search.GetQuickLinkPrompt(issues) is null &&
            search.GetQuickLinkPrompt(repositoryRow) is { Query: "filter", Fields: ["owner", "repo", "query"] }, "only missing inputs require a prompt, with the typed query retained");
        await search.ExecuteAsync(dashboard, default);
        await search.ExecuteAsync(issues, default);
        Check(executor.Calls.SequenceEqual(["open:https://example.test/dashboard:False", "open:https://example.test/issues?q=one%20two%20%26%20%22three%22:False"]),
            "saved quick links survive reload and query stays inside its URL value");
        await search.ExecuteQuickLinkAsync(viewer, new Dictionary<string, string> { [fieldName] = "notes and letters.txt" }, default);
        var request = process.Requests.Single();
        Check(request.FileName == executable && request.WorkingDirectory == folder && request.Direct && !request.Elevated &&
            request.Arguments.SequenceEqual(["--find", argument, "notes and letters.txt"]), "URL-independent named values and typed query remain separate process arguments");

        var repositoryValues = new Dictionary<string, string> { ["owner"] = "a/b", ["repo"] = "my repo" };
        await search.ExecuteQuickLinkAsync(repositoryRow, repositoryValues, default);
        Check(executor.Calls.Last() == "open:https://example.test/a%2Fb/my%20repo?q=filter:False", "named URL values cannot introduce URL separators");
        repositoryValues["owner"] = "..";
        try { await search.ExecuteQuickLinkAsync(repositoryRow, repositoryValues, default); Check(false, "resolved dot segment navigated outside the fixed path"); }
        catch (LauncherActionException) { Check(executor.Calls.Count == 3, "dot path is rejected before opening the URL"); }

        try { await search.ExecuteAsync(viewer, default); Check(false, "missing named field launched EXE"); }
        catch (LauncherActionException) { Check(process.Requests.Count == 1, "missing named field never reaches the executor"); }

        store.Update(current => current with { Launcher = current.Launcher with { FilesKeywords = [..launcher.FilesKeywords, "ISSUES"] } });
        store.Load();
        Check(store.Current.Launcher.FilesKeywords.Contains("ISSUES") && store.Current.Launcher.QuickLinks.Contains(templated),
            "native file keyword and existing quick link both survive save and reload");
        Check((await search.SearchAsync("Issues", default)).Items.All(row => row.Group != LauncherGroups.QuickLinks) &&
            search.GetQuickLinkPrompt(blankQuery) is null, "conflicting quick link is unavailable by name, keyword or old prompt");
        try { await search.ExecuteAsync(issues, default); Check(false, "conflicting quick link executed from an old result"); }
        catch (LauncherActionException) { Check(executor.Calls.Count == 3, "native keyword blocks the old launcher action"); }
        store.Update(current => current with { Launcher = current.Launcher with { FilesKeywords = launcher.FilesKeywords } });
        Check((await Find("issues")).Title == templated.Name, "quick link returns when the native keyword is freed");

        store.Save(settings with { Launcher = launcher with { QuickLinks = [fixedLink, templated, repository with { Enabled = false }, program] } });
        try { await search.ExecuteQuickLinkAsync(repositoryRow, new Dictionary<string, string> { ["owner"] = "pane", ["repo"] = "repo" }, default); Check(false, "disabled quick link executed from an old form"); }
        catch (LauncherActionException) { Check(executor.Calls.Count == 3, "disabled quick link never reaches the executor"); }

        foreach (var invalid in new[] { templated with { Target = "https://{query}.example.test" }, templated with { Keyword = program.Keyword.ToUpperInvariant() } })
        {
            try { store.Save(settings with { Launcher = launcher with { QuickLinks = [invalid, program] } }); Check(false, "accepted an invalid quick link"); }
            catch (ArgumentException) { Check(store.Current.Launcher.QuickLinks.Any(link => link == program), "invalid quick link preserves saved settings"); }
        }
    }
}
