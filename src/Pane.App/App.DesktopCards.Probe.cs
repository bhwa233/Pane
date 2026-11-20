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
            fixture.Select(null);
            fixture.Edit("Layout probe", "Layout edits do not change card content.");
            if (!fixture.SaveDraft()) throw new InvalidOperationException("Missing layout probe note.");
            ToggleNotePin(fixture.Draft!.Id);
            SetTrafficPin(true);
            var image = GetScreenshots().Import(source.Trim());
            SaveScreenshotLayout(image, true, null);
            RefreshDesktopScreenshots();
        }

        var cards = DesktopCardPanels().ToArray();
        if (cards.Length != 3) throw new InvalidOperationException("Expected pinned traffic, note and screenshot cards.");
        if (!string.IsNullOrWhiteSpace(source))
        {
            ShowDesktopCards();
            var originalFile = File.ReadAllBytes(settingsPath);
            var origins = cards.Select(card => card.Origin).ToArray();
            var traffic = desktopTrafficCard!;
            var trafficAnchor = traffic.DesktopAnchor!;
            var workArea = trafficAnchor with { MonitorId = "Pane.Probe.UnavailableDisplay" };
            traffic.DesktopAnchor = workArea;
            GlassPanel.RestoreDesktopCards(cards);
            if (traffic.DesktopAnchor != workArea || traffic.DesktopPlacement?.MonitorId == workArea.MonitorId ||
                cards.Select(card => card.DesktopPlacement?.MonitorId).Distinct().Count() != 1 ||
                !origins.SequenceEqual(cards.Select(card => card.Origin)) ||
                !originalFile.SequenceEqual(File.ReadAllBytes(settingsPath)))
                throw new InvalidOperationException("Saved monitor IDs must not affect placement on the selected screen or alter the saved layout.");
            traffic.DesktopAnchor = trafficAnchor;
            GlassPanel.RestoreDesktopCards(cards);
            MoveAllCards();
            CancelDesktopArrangement();
            if (!origins.SequenceEqual(cards.Select(card => card.Origin)) || !originalFile.SequenceEqual(File.ReadAllBytes(settingsPath)))
                throw new InvalidOperationException("Cancel did not restore the live layout without writing settings.");

            MoveAllCards();
            if (SaveDesktopArrangement() is { } error) throw new InvalidOperationException(error);
            if (originalFile.SequenceEqual(File.ReadAllBytes(settingsPath)) || cards.Any(card => card.DesktopArranging))
                throw new InvalidOperationException("Save did not commit the staged layout and finish editing.");

            void MoveAllCards()
            {
                if (!BeginDesktopArrangement()) throw new InvalidOperationException("Layout editor did not open.");
                foreach (var card in cards) card.SimulateDrag(-48, 24);
                if (cards.Select((card, index) => card.Origin != origins[index]).Any(moved => !moved) ||
                    !originalFile.SequenceEqual(File.ReadAllBytes(settingsPath)))
                    throw new InvalidOperationException("A staged move did not move its window or wrote settings early.");
            }
        }

        File.WriteAllLines(output + ".desktop-arrangement-result", cards.Select(card => FormattableString.Invariant($"{card.Origin.X},{card.Origin.Y}")));
        return true;
    }
}
