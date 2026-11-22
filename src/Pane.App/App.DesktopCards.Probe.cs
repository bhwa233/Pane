using Pane.Hosting;

namespace Pane;

public sealed partial class App
{
    bool DesktopCardControls()
    {
        if (Environment.GetEnvironmentVariable("PANE_VERIFY") != "1") return false;
        if (demo && Take("show-desktop-cards", out _))
        {
            ShowDesktopCards();
            pendingAck = "shown-desktop-cards";
            return true;
        }
        if (!Take("desktop-arrangement-check", out var source)) return false;
        var settingsPath = Path.Combine(Store.DirectoryPath, "settings.json");
        if (!string.IsNullOrWhiteSpace(source))
        {
            var fixture = GetNotes();
            fixture.Load();
            var probeNote = fixture.Create("Layout probe", "Layout edits do not change card content.");
            ToggleNotePin(probeNote.Id);
            SetTrafficPin(true);
            var image = GetScreenshots().Import(source.Trim());
            SaveScreenshotLayout(image, true, null);
            RefreshDesktopScreenshots();
        }

        var traffic = desktopTrafficCard ?? throw new InvalidOperationException("Expected pinned traffic.");
        var note = desktopNotes.Values.Single();
        var screenshot = desktopScreenshots.Values.Single().AppWindow;
        (int X, int Y)[] Origins() =>
            [traffic.Origin, (note.Bounds.X, note.Bounds.Y), (screenshot.Position.X, screenshot.Position.Y)];
        if (!string.IsNullOrWhiteSpace(source))
        {
            ShowDesktopCards();
            var originalFile = File.ReadAllBytes(settingsPath);
            var origins = Origins();
            var screenshotSize = screenshot.Size;
            var trafficAnchor = traffic.DesktopAnchor!;
            var workArea = trafficAnchor with { MonitorId = "Pane.Probe.UnavailableDisplay" };
            traffic.DesktopAnchor = workArea;
            GlassPanel.RestoreDesktopCards([traffic]);
            if (traffic.DesktopAnchor != workArea || traffic.DesktopPlacement?.MonitorId == workArea.MonitorId ||
                !origins.SequenceEqual(Origins()) ||
                !originalFile.SequenceEqual(File.ReadAllBytes(settingsPath)))
                throw new InvalidOperationException("Saved monitor IDs must not affect placement on the selected screen or alter the saved layout.");
            traffic.DesktopAnchor = trafficAnchor;
            GlassPanel.RestoreDesktopCards([traffic]);
            MoveAllCards();
            CancelDesktopArrangement();
            if (!origins.SequenceEqual(Origins()) || screenshot.Size != screenshotSize || !originalFile.SequenceEqual(File.ReadAllBytes(settingsPath)))
                throw new InvalidOperationException("Cancel did not restore the live layout without writing settings.");

            MoveAllCards();
            if (SaveDesktopArrangement() is { } error) throw new InvalidOperationException(error);
            var screenshotId = desktopScreenshots.Keys.Single();
            if (originalFile.SequenceEqual(File.ReadAllBytes(settingsPath)) || traffic.DesktopArranging ||
                screenshot.Size == screenshotSize || Store.Current.DesktopScreenshotSizes?.ContainsKey(screenshotId) != true)
                throw new InvalidOperationException("Save did not commit the staged layout and finish editing.");

            void MoveAllCards()
            {
                if (!BeginDesktopArrangement()) throw new InvalidOperationException("Layout editor did not open.");
                traffic.SimulateDrag(-48, 24);
                note.AppWindow.Move(new(note.Bounds.X - 48, note.Bounds.Y + 24));
                screenshot.MoveAndResize(new(screenshot.Position.X - 48, screenshot.Position.Y + 24,
                    screenshot.Size.Width + 48, screenshot.Size.Height + 24));
                if (Origins().Zip(origins).Any(pair => pair.First == pair.Second) ||
                    !originalFile.SequenceEqual(File.ReadAllBytes(settingsPath)))
                    throw new InvalidOperationException("A staged move did not move its window or wrote settings early.");
            }
        }

        File.WriteAllLines(output + ".desktop-arrangement-result", Origins().Select(point => FormattableString.Invariant($"{point.X},{point.Y}")));
        return true;
    }
}
