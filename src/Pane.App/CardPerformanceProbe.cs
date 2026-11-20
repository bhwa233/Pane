using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace Pane;

public sealed partial class App {
    CardPerformanceProbe? cardPerformanceProbe;

    void StartCardPerformanceProbe() {
        if (!IsProduction && Environment.GetEnvironmentVariable("PANE_CARD_BENCHMARK") is { Length: > 0 } directory)
            cardPerformanceProbe = new(queue, Path.GetFullPath(directory), Shutdown, cancellation.Token);
    }
}

internal sealed class CardPerformanceProbe : IDisposable {
    readonly string directory;
    readonly DispatcherQueue dispatcher;
    readonly Action shutdown;
    readonly FileSystemWatcher watcher;
    readonly Window window = new() { Title = "Pane card benchmark" };
    readonly Grid surface = new() { RequestedTheme = ElementTheme.Dark };
    readonly TextBlock traffic = new() { Text = "ETW: waiting", FontSize = 14 };
    readonly TextBlock bar = new() { Text = "Desktop cards", FontSize = 17, Margin = new(12) };
    readonly RectInt32 workArea = DisplayArea.Primary.WorkArea;
    EventHandler<object>? renderPending;
    bool disposed;

    public CardPerformanceProbe(DispatcherQueue dispatcher, string directory, Action shutdown, CancellationToken token) {
        this.dispatcher = dispatcher;
        this.directory = directory;
        this.shutdown = shutdown;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        var cards = new Grid { ColumnSpacing = 8, Margin = new(8,44,8,8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        for (var column = 0; column < 3; column++) cards.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var image = new Image { Stretch = Stretch.Uniform, Height = 92 };
        image.ImageOpened += (_, _) => Record("image.ready", "decoded");
        image.ImageFailed += (_, failure) => Record("image.error", failure.ErrorMessage);
        image.Source = new BitmapImage(new Uri(Path.Combine(directory, "fixture.png")));
        AddCard(cards, 0, "Traffic", traffic);
        AddCard(cards, 1, "Benchmark note", new TextBox { Text = "Desktop benchmark fixture.\nSame text in both hosts.", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 92 });
        AddCard(cards, 2, "Screenshot pin", image);
        surface.Children.Add(cards);
        surface.Children.Add(bar);
        window.Content = surface;
        Present(false);
        watcher = new(directory, "*.request") { NotifyFilter = NotifyFilters.FileName };
        watcher.Created += (_, change) => dispatcher.TryEnqueue(() => Handle(change.Name));
        watcher.EnableRaisingEvents = true;
        _ = Task.Run(() => ReadTraffic(token), token);
        Record("host.ready", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    static void AddCard(Grid root, int column, string title, UIElement content) {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        body.Children.Add(content);
        var frame = new Border { Child = body, Padding = new(10), CornerRadius = new(6), Height = 138, Width = 276, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255,42,44,48)) };
        Grid.SetColumn(frame, column);
        root.Children.Add(frame);
    }

    void Present(bool expanded) {
        bar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        surface.Background = new SolidColorBrush(expanded ? Windows.UI.Color.FromArgb(255,28,30,33) : Windows.UI.Color.FromArgb(255,48,50,54));
        var bounds = expanded ? workArea : new RectInt32(workArea.X + 24, workArea.Y + 24, Math.Min(workArea.Width - 24, 1440), 345);
        window.AppWindow.MoveAndResize(bounds);
        window.AppWindow.Show(false);
    }

    void Handle(string? name) {
        if (disposed || name is null) return;
        if (name == "exit.request") { shutdown(); return; }
        bool expanded = name.StartsWith("open-", StringComparison.Ordinal);
        if (!expanded && !name.StartsWith("close-", StringComparison.Ordinal)) return;
        var clock = Stopwatch.StartNew();
        if (renderPending != null) CompositionTarget.Rendering -= renderPending;
        renderPending = (_, _) => {
            CompositionTarget.Rendering -= renderPending;
            renderPending = null;
            Record(Path.GetFileNameWithoutExtension(name) + ".ready", clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
        };
        CompositionTarget.Rendering += renderPending;
        Present(expanded);
    }

    void Record(string file, string value) => File.WriteAllText(Path.Combine(directory, file), value);

    async Task ReadTraffic(CancellationToken token) {
        try {
            var executable = Path.Combine(directory, "spark233.TrafficClient.exe");
            var start = new ProcessStartInfo(executable, "--bridge") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, WorkingDirectory = directory };
            using var bridge = Process.Start(start) ?? throw new IOException("Traffic bridge did not start");
            try {
                var session = Guid.NewGuid().ToString("N");
                var requestId = 0;
                async Task<JsonDocument> Send(string type, string? command = null) {
                    var id = (++requestId).ToString(CultureInfo.InvariantCulture);
                    var request = new JsonObject { ["protocol"] = 1, ["type"] = type, ["sessionId"] = session, ["requestId"] = id,
                        ["owner"] = new JsonObject { ["pluginId"] = "spark233.traffic", ["version"] = "bench", ["instanceId"] = session } };
                    if (command != null) {
                        var parameters = new JsonObject { ["scope"] = "own" };
                        if (command == "snapshot") parameters["limit"] = 1;
                        request["activation"] = new JsonObject { ["commandId"] = command, ["parameters"] = parameters };
                    }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(8));
                    await bridge.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), deadline.Token);
                    var reply = JsonDocument.Parse(await bridge.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("Traffic bridge closed"));
                    if (reply.RootElement.GetProperty("requestId").GetString() == id && reply.RootElement.GetProperty("ok").GetBoolean()) return reply;
                    reply.Dispose();
                    throw new IOException($"Traffic bridge rejected {type}/{command}");
                }
                using (await Send("init")) { }
                using (var status = await Send("query", "status")) {
                    if (!status.RootElement.GetProperty("value").GetProperty("collecting").GetBoolean()) throw new IOException("ETW is not collecting in own scope");
                }
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                var samples = 0;
                do {
                    using var snapshot = await Send("query", "snapshot");
                    var value = snapshot.RootElement.GetProperty("value");
                    if (value.GetProperty("source").GetString() != "etw") throw new IOException("Unexpected traffic source");
                    var totals = value.GetProperty("totals");
                    var quality = value.GetProperty("quality").GetProperty("state").GetString();
                    var summary = $"Down {Rate(totals, "rxBytesPerSec")}\nUp {Rate(totals, "txBytesPerSec")}\nETW / {quality}";
                    dispatcher.TryEnqueue(() => { if (!disposed) traffic.Text = summary; });
                    if (++samples == 1 || samples % 10 == 0) Record("traffic.ready", $"{bridge.Id};{samples};{quality}");
                } while (await timer.WaitForNextTickAsync(token));
            } finally { bridge.StandardInput.Close(); }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Record("traffic.error", error.Message); }
    }

    static string Rate(JsonElement totals, string key) => totals.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        ? $"{value.GetDouble() / 1024:N1} KiB/s" : "unavailable";

    public void Dispose() {
        disposed = true;
        watcher.Dispose();
        if (renderPending != null) CompositionTarget.Rendering -= renderPending;
        window.Close();
    }
}
