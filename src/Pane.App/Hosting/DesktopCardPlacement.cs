using System.Drawing;

namespace Pane.Hosting;

internal static class DesktopCardPlacement
{
    internal static (Point Origin, long OccupiedArea) Find(Rectangle work, Size card, int edge, int gap, IReadOnlyList<Rectangle> occupied)
    {
        var left = work.Left + edge;
        var top = work.Top + edge;
        var right = Math.Max(left, work.Right - edge - card.Width);
        var bottom = Math.Max(top, work.Bottom - edge - card.Height);
        var obstacles = occupied.Where(rectangle => rectangle.IntersectsWith(work)).Select(rectangle => Rectangle.Inflate(rectangle, gap, gap)).ToArray();
        var columns = obstacles.SelectMany(rectangle => new[] { rectangle.Left - card.Width, rectangle.Right })
            .Append(left).Append(right).Select(value => Math.Clamp(value, left, right)).Distinct().OrderDescending().ToArray();
        var rows = obstacles.SelectMany(rectangle => new[] { rectangle.Top - card.Height, rectangle.Bottom })
            .Append(top).Append(bottom).Select(value => Math.Clamp(value, top, bottom)).Distinct().Order().ToArray();

        var best = new Point(right, top);
        var bestArea = long.MaxValue;
        foreach (var column in columns)
            foreach (var row in rows)
            {
                var region = new Rectangle(column, row, card.Width, card.Height);
                long area = 0;
                foreach (var obstacle in obstacles)
                {
                    var intersection = Rectangle.Intersect(region, obstacle);
                    area += (long)intersection.Width * intersection.Height;
                    if (area >= bestArea) break;
                }
                if (area == 0) return (region.Location, 0);
                if (area < bestArea) { best = region.Location; bestArea = area; }
            }

        return (best, bestArea);
    }
}
