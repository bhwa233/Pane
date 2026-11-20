using System.Drawing;
using Pane.Hosting;

static class DesktopCardPlacementTests
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool ok, string scenario)
        {
            if (!ok) throw new InvalidOperationException(scenario);
            count++;
        }

        var work = new Rectangle(-800, 35, 800, 540);
        var largeCard = new Size(256, 170);
        var usable = Rectangle.Inflate(work, -8, -8);
        var occupied = new List<Rectangle> { new(-340, 48, 256, 124) };
        for (var index = 0; index < 4; index++)
        {
            var placement = DesktopCardPlacement.Find(work, largeCard, 8, 10, occupied);
            var rectangle = new Rectangle(placement.Origin, largeCard);
            Check(usable.Contains(rectangle) && occupied.All(existing => !Rectangle.Inflate(existing, 10, 10).IntersectsWith(rectangle)),
                "available slots must fit the negative-origin work area and avoid the manually moved card");
            occupied.Add(rectangle);
        }

        var narrow = new Rectangle(0, 0, 300, 250);
        var crowded = DesktopCardPlacement.Find(narrow, largeCard, 8, 10, [new(34, 8, 256, 170)]);
        Check(crowded.OccupiedArea > 0 && crowded.Origin == new Point(8, 72), "with no free slot, prefer the visible slot with least overlap");
        return count;
    }
}
