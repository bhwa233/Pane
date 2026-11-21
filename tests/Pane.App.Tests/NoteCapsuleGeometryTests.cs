using System.Drawing;
using Pane.Hosting;

// notes-mvp §7「几何与停靠」：三边选择、顶部默认居中、左右纵排 / 顶部横排、视窗裁切、
// 溢出滚动、插入索引、预览方向与窄通道、悬浮夹紧。负坐标屏幕用相对工作区直接覆盖。
static class NoteCapsuleGeometryTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var work = new RectangleF(0, 0, 1920, 1040);
        var negative = new RectangleF(-2560, 0, 2560, 1400);   // 左侧副屏，工作区带负坐标

        // ---------------- 三边选择 ----------------
        Check(NoteCapsuleGeometry.NearestEdge(work, new(0, 500)) == NoteEdge.Left, "a point at the left border picks the left edge");
        Check(NoteCapsuleGeometry.NearestEdge(work, new(1920, 500)) == NoteEdge.Right, "a point at the right border picks the right edge");
        Check(NoteCapsuleGeometry.NearestEdge(work, new(960, 0)) == NoteEdge.Top, "a point at the top border picks the top edge");
        Check(NoteCapsuleGeometry.NearestEdge(work, new(100, 4)) == NoteEdge.Top, "the nearest edge wins over the left edge");
        Check(NoteCapsuleGeometry.NearestEdge(negative, new(-2560, 700)) == NoteEdge.Left, "negative-coordinate screens pick edges correctly");

        // ---------------- 默认起点：顶部居中、左右靠上 ----------------
        Check(MathF.Abs(NoteCapsuleGeometry.DefaultStart(work, NoteEdge.Top) - (1920 - NoteCapsuleGeometry.MasterBaseWidth) / 2) < 0.01f,
            "the top queue starts centered");
        Check(NoteCapsuleGeometry.DefaultStart(work, NoteEdge.Left) == NoteCapsuleGeometry.Margin, "side queues start at the margin");
        Check(MathF.Abs(NoteCapsuleGeometry.DefaultStart(negative, NoteEdge.Top) - (2560 - NoteCapsuleGeometry.MasterBaseWidth) / 2) < 0.01f,
            "centering works on negative-coordinate screens");

        // ---------------- 主胶囊 / 成员矩形 ----------------
        var master = NoteCapsuleGeometry.MasterRect(work, NoteEdge.Left, 100, 12);
        Check(master.X == 0 && MathF.Abs(master.Y - 100) < 0.01f,
            "the left master is flush with the work area edge");
        Check(NoteCapsuleGeometry.MasterWidth(12) > NoteCapsuleGeometry.MasterWidth(3) &&
            NoteCapsuleGeometry.MasterWidth(3) == NoteCapsuleGeometry.MasterWidth(9),
            "the master widens only beyond one digit");
        var rightMaster = NoteCapsuleGeometry.MasterRect(work, NoteEdge.Right, 100, 0);
        Check(MathF.Abs(rightMaster.Right - work.Right) < 0.01f, "the right master hugs the right edge");
        var topMaster = NoteCapsuleGeometry.MasterRect(work, NoteEdge.Top, 200, 0);
        Check(topMaster.Y == work.Top, "the top master hugs the top edge");
        var member = NoteCapsuleGeometry.MemberRect(work, NoteEdge.Top, 200, 0, 0, 0);
        Check(MathF.Abs(member.X - (200 + NoteCapsuleGeometry.MasterWidth(0) + NoteCapsuleGeometry.Gap)) < 0.01f &&
            member.Width == NoteCapsuleGeometry.CapsuleWidth,
            "members follow the master along the edge");
        var scrolled = NoteCapsuleGeometry.MemberRect(work, NoteEdge.Top, 200, 40, 0, 0);
        Check(MathF.Abs(scrolled.X - member.X + 40) < 0.01f, "scrolling shifts members only");
        var verticalMember = NoteCapsuleGeometry.MemberRect(work, NoteEdge.Left, 100, 0, 1, 5);
        var first = NoteCapsuleGeometry.MemberRect(work, NoteEdge.Left, 100, 0, 0, 5);
        Check(MathF.Abs(verticalMember.Y - first.Y - NoteCapsuleGeometry.Step(NoteEdge.Left)) < 0.01f,
            "side queues stack vertically");

        // ---------------- 宿主长度、可见成员与滚动夹紧 ----------------
        int members = 40;   // 顶部横排 40 个远超 1920 宽度，必然溢出
        var hostLength = NoteCapsuleGeometry.HostLength(work, NoteEdge.Top, 0, members, members);
        var queueLength = NoteCapsuleGeometry.QueueLength(NoteEdge.Top, members, members);
        Check(hostLength < queueLength && MathF.Abs(hostLength - work.Width) < 0.01f,
            "an overflowing queue caps its host at the work area span");
        Check(NoteCapsuleGeometry.VisibleMembers(work, NoteEdge.Top, 0, 0, members, members) < members &&
            NoteCapsuleGeometry.VisibleMembers(work, NoteEdge.Top, 0, 0, members, members) > 0,
            "only fitting members stay visible");
        Check(NoteCapsuleGeometry.ClampScroll(work, NoteEdge.Top, 0, -20, members, members) == 0,
            "negative scroll clamps to zero");
        var maxScroll = NoteCapsuleGeometry.ClampScroll(work, NoteEdge.Top, 0, float.MaxValue, members, members);
        Check(maxScroll > 0 && NoteCapsuleGeometry.ClampScroll(work, NoteEdge.Top, 0, maxScroll + 100, members, members) == maxScroll,
            "scroll clamps to the overflow size");
        Check(NoteCapsuleGeometry.ClampScroll(work, NoteEdge.Top, 0, 10, 0, 0) == 0, "an empty queue never scrolls");
        Check(NoteCapsuleGeometry.VisibleMembers(work, NoteEdge.Top, 0, maxScroll, members, members) == members,
            "scrolling to the end exposes the last member");
        Check(MathF.Abs(NoteCapsuleGeometry.HostLength(work, NoteEdge.Top, 1500, 40, 40) - (1920 - 1500)) < 0.01f,
            "a late-start queue keeps its host inside the work area");
        Check(NoteCapsuleGeometry.HostLength(work, NoteEdge.Top, 1500, 0, 0) == NoteCapsuleGeometry.MasterWidth(0),
            "an empty queue's host is just the master");
        Check(NoteCapsuleGeometry.ClampStart(work, NoteEdge.Left, -50, 2, 2) == 0, "a negative start clamps to zero");
        Check(NoteCapsuleGeometry.ClampStart(work, NoteEdge.Left, float.MaxValue, 2, 2) ==
            NoteCapsuleGeometry.EdgeSpan(work, NoteEdge.Left) - NoteCapsuleGeometry.HostLength(work, NoteEdge.Left, 0, 2, 2),
            "a start beyond the span clamps so the queue stays reachable");
        var shortQueue = NoteCapsuleGeometry.HostLength(negative, NoteEdge.Left, 300, 1, 1);
        Check(shortQueue < 300 + 1000, "host lengths never exceed the available span");

        // ---------------- 插入索引 ----------------
        var step = NoteCapsuleGeometry.Step(NoteEdge.Top);
        var base0 = 200 + NoteCapsuleGeometry.MasterWidth(0) + NoteCapsuleGeometry.Gap;
        Check(NoteCapsuleGeometry.InsertIndex(NoteEdge.Top, 200, 0, base0 - 1, 3) == 0, "a position before the first member inserts at the head");
        Check(NoteCapsuleGeometry.InsertIndex(NoteEdge.Top, 200, 0, base0, 3) == 0, "a position at the first member inserts before it");
        Check(NoteCapsuleGeometry.InsertIndex(NoteEdge.Top, 200, 0, base0 + step / 2 + 1, 3) == 1, "a position past the first gap inserts in the middle");
        Check(NoteCapsuleGeometry.InsertIndex(NoteEdge.Top, 200, 0, base0 + 3 * step, 3) == 3, "a position beyond the end appends");
        Check(NoteCapsuleGeometry.InsertIndex(NoteEdge.Top, 200, 20, base0 + 20 + step / 2, 3) == 1, "insert positions account for scroll");

        // ---------------- 预览方向与窄通道 ----------------
        var leftCapsule = new RectangleF(0, 300, NoteCapsuleGeometry.CapsuleWidth, NoteCapsuleGeometry.CapsuleHeight);
        var leftPreview = NoteCapsuleGeometry.PreviewRect(work, NoteEdge.Left, leftCapsule, new(320, 240));
        Check(leftPreview.X == leftCapsule.Right, "a left-edge preview opens toward the screen interior, adjacent to its capsule");
        var rightCapsule = new RectangleF(work.Right - NoteCapsuleGeometry.CapsuleWidth, 300, NoteCapsuleGeometry.CapsuleWidth, NoteCapsuleGeometry.CapsuleHeight);
        var rightPreview = NoteCapsuleGeometry.PreviewRect(work, NoteEdge.Right, rightCapsule, new(320, 240));
        Check(MathF.Abs(rightPreview.Right - rightCapsule.Left) < 0.01f, "a right-edge preview opens leftward");
        var topCapsule = new RectangleF(900, 0, NoteCapsuleGeometry.CapsuleWidth, NoteCapsuleGeometry.CapsuleHeight);
        var topPreview = NoteCapsuleGeometry.PreviewRect(work, NoteEdge.Top, topCapsule, new(320, 240));
        Check(topPreview.Y == topCapsule.Bottom, "a top-edge preview opens downward");
        var clampedPreview = NoteCapsuleGeometry.PreviewRect(work, NoteEdge.Left, new(0, 900, 160, 32), new(320, 240));
        Check(clampedPreview.Y + clampedPreview.Height <= work.Bottom + 0.01f, "previews clamp inside the work area");

        // ---------------- 悬浮夹紧与队列键 ----------------
        var floated = NoteCapsuleGeometry.ClampFloat(work, -500, 900);
        Check(floated.X >= work.Left, "floating capsules clamp inside the work area");
        Check(floated.Y + NoteCapsuleGeometry.CapsuleHeight <= work.Bottom + 0.01f, "floating capsules clamp vertically");
        Check(NoteCapsuleGeometry.QueueKey("mon1", NoteEdge.Top) == "mon1|2", "queue keys combine the monitor and edge");
        Check(NoteCapsuleGeometry.QueueKey("mon1", NoteEdge.Left) != NoteCapsuleGeometry.QueueKey("mon2", NoteEdge.Left),
            "queue keys differ per monitor");
        return checks;
    }
}
