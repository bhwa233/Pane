using System.Drawing;

namespace Pane.Hosting;

/// <summary>胶囊落点边缘（notes-mvp §6）。数值即 NoteCapsuleSlot.Edge 的持久化值。</summary>
internal enum NoteEdge { Left = 0, Right = 1, Top = 2, Float = 3 }

/// <summary>胶囊与贴边队列的纯布局逻辑（notes-mvp §7）：全部使用相对工作区的 DIP 坐标，
/// 不触碰真实窗口。负坐标屏幕、任务栏变化和跨屏混合 DPI 由调用方换算成工作区矩形后进来；
/// 本层只做几何。布局规则：左右纵排、顶部横排、主胶囊位于队列沿边起点一侧、
/// 溢出成员裁切、主胶囊始终可操作、透明间隙不占屏幕。</summary>
internal static class NoteCapsuleGeometry
{
    /// <summary>单张胶囊：约 160 DIP 宽、32 DIP 高（galaxy Docs/edge-capsules 已确认交互）。</summary>
    internal const float CapsuleWidth = 160f, CapsuleHeight = 32f;
    /// <summary>主胶囊：高 32 DIP、宽度 84 DIP 起，数量每位数字加宽 10 DIP。</summary>
    internal const float MasterHeight = 32f, MasterBaseWidth = 84f, MasterDigitWidth = 10f;
    /// <summary>胶囊间距；同时是胶囊与悬停预览之间的窄通道，保证连续悬停区域。</summary>
    internal const float Gap = 8f;
    /// <summary>队列离工作区角的默认边距。</summary>
    internal const float Margin = 8f;

    internal static bool IsVertical(NoteEdge edge) => edge is NoteEdge.Left or NoteEdge.Right;

    /// <summary>队列键（每屏每边独立排列）："{MonitorId}|{(int)Edge}"。</summary>
    internal static string QueueKey(string monitorId, NoteEdge edge) => $"{monitorId}|{(int)edge}";

    /// <summary>最近边缘：点到左 / 右 / 顶部工作区边界的距离最小者（工作区已避开任务栏）。</summary>
    internal static NoteEdge NearestEdge(RectangleF work, PointF point)
    {
        var left = Math.Abs(point.X - work.Left);
        var right = Math.Abs(work.Right - point.X);
        var top = Math.Abs(point.Y - work.Top);
        if (top <= left && top <= right) return NoteEdge.Top;
        return left <= right ? NoteEdge.Left : NoteEdge.Right;
    }

    /// <summary>首次折叠 / 整队迁移后的队列起点：顶部默认居中，左右默认靠上。</summary>
    internal static float DefaultStart(RectangleF work, NoteEdge edge) =>
        edge == NoteEdge.Top ? MathF.Max(0, (EdgeSpan(work, edge) - MasterBaseWidth) / 2) : Margin;

    /// <summary>沿边可用跨度（DIP）。</summary>
    internal static float EdgeSpan(RectangleF work, NoteEdge edge) =>
        IsVertical(edge) ? work.Height : work.Width;

    /// <summary>主胶囊宽度：84 DIP 起，数量每位数字加宽。</summary>
    internal static float MasterWidth(int count) =>
        MasterBaseWidth + MathF.Max(0, Digits(count) - 1) * MasterDigitWidth;

    internal static float MasterLength(NoteEdge edge, int count) =>
        IsVertical(edge) ? MasterHeight : MasterWidth(count);

    static int Digits(int count)
    {
        var digits = 0;
        for (var value = Math.Max(0, count); ; ) { digits++; value /= 10; if (value == 0) return digits; }
    }

    /// <summary>成员沿边步进：成员长度 + 间距（纵向边 32+8、顶部横向 160+8）。</summary>
    internal static float Step(NoteEdge edge) => (IsVertical(edge) ? CapsuleHeight : CapsuleWidth) + Gap;

    /// <summary>队列内容长度（DIP）：主胶囊 + 成员列；空队列只有主胶囊。</summary>
    internal static float QueueLength(NoteEdge edge, int members, int count) =>
        MasterLength(edge, count) + (members == 0 ? 0 : Gap + members * Step(edge));

    /// <summary>宿主窗口沿边长度：内容超出可用跨度时截断（溢出成员在窗内裁切，主胶囊始终完整）。</summary>
    internal static float HostLength(RectangleF work, NoteEdge edge, double start, int members, int count)
    {
        var span = EdgeSpan(work, edge) - (float)start;
        return Math.Clamp(QueueLength(edge, members, count), 0, MathF.Max(0, span));
    }

    /// <summary>夹紧队列起点：主胶囊拖动只改 Start；范围保证队列不越出工作区。</summary>
    internal static float ClampStart(RectangleF work, NoteEdge edge, double start, int members, int count) =>
        Math.Clamp((float)start, 0, MathF.Max(0, EdgeSpan(work, edge) - HostLength(work, edge, 0, members, count)));

    /// <summary>夹紧溢出滚动：只在内容真的溢出时有意义；收起再展开保留该值（notes-mvp §6）。</summary>
    public static float ClampScroll(RectangleF work, NoteEdge edge, double start, double scroll, int members, int count)
    {
        var host = HostLength(work, edge, start, members, count);
        var max = MathF.Max(0, QueueLength(edge, members, count) - host);
        return Math.Clamp((float)scroll, 0, max);
    }

    /// <summary>主胶囊矩形：位于队列沿边起点一侧。</summary>
    internal static RectangleF MasterRect(RectangleF work, NoteEdge edge, double start, int count)
    {
        var along = (float)start;
        var width = MasterWidth(count);
        return edge switch
        {
            NoteEdge.Left => new(work.Left, work.Top + along, width, MasterHeight),
            NoteEdge.Right => new(work.Right - width, work.Top + along, width, MasterHeight),
            NoteEdge.Top => new(work.Left + along, work.Top, width, MasterHeight),
            _ => new(work.Left + along, work.Top, width, MasterHeight),
        };
    }

    /// <summary>成员矩形（index 是按 Order 排列后的位次）：主胶囊之后沿边排开，滚动只移动成员。</summary>
    internal static RectangleF MemberRect(RectangleF work, NoteEdge edge, double start, double scroll, int index, int count)
    {
        var along = (float)(start + MasterLength(edge, count) + Gap + index * Step(edge) - scroll);
        return edge switch
        {
            NoteEdge.Left => new(work.Left, work.Top + along, CapsuleWidth, CapsuleHeight),
            NoteEdge.Right => new(work.Right - CapsuleWidth, work.Top + along, CapsuleWidth, CapsuleHeight),
            NoteEdge.Top => new(work.Left + along, work.Top, CapsuleWidth, CapsuleHeight),
            _ => new(work.Left + along, work.Top, CapsuleWidth, CapsuleHeight),
        };
    }

    /// <summary>队列宿主窗口矩形：从队列起点沿边覆盖宿主长度；窗口外天然不拦截桌面点击。</summary>
    internal static RectangleF HostRect(RectangleF work, NoteEdge edge, double start, double length)
    {
        var along = (float)start;
        var span = (float)length;
        return edge switch
        {
            NoteEdge.Left => new(work.Left, work.Top + along, CapsuleWidth, span),
            NoteEdge.Right => new(work.Right - CapsuleWidth, work.Top + along, CapsuleWidth, span),
            _ => new(work.Left + along, work.Top, span, MasterHeight),
        };
    }

    /// <summary>可见成员数：宿主长度内放得下的成员（其余在窗内裁切，滚动露出）。</summary>
    internal static int VisibleMembers(RectangleF work, NoteEdge edge, double start, double scroll, int members, int count)
    {
        if (members <= 0) return 0;
        var host = HostLength(work, edge, start, members, count);
        var available = host - MasterLength(edge, count) - Gap + (float)scroll;
        return Math.Clamp((int)MathF.Floor(available / Step(edge) + 1e-4f), 0, members);
    }

    /// <summary>拖动重排的插入位置：沿边坐标落到哪个成员间隙（最近者）。</summary>
    internal static int InsertIndex(NoteEdge edge, double start, double scroll, double position, int count)
    {
        var relative = position - start - MasterLength(edge, count) - Gap + scroll;
        return Math.Clamp((int)MathF.Floor((float)relative / Step(edge) + 0.5f), 0, count);
    }

    /// <summary>悬停预览矩形（notes-mvp §7）：顶部向下展开、左右向屏幕内侧展开；
    /// 与胶囊共享边界形成连续悬停区域（Gap 即窄通道），整体约束在工作区内。</summary>
    internal static RectangleF PreviewRect(RectangleF work, NoteEdge edge, RectangleF capsule, SizeF preview)
    {
        var rect = edge switch
        {
            NoteEdge.Left => new RectangleF(capsule.Right, capsule.Y, preview.Width, preview.Height),
            NoteEdge.Right => new RectangleF(capsule.Left - preview.Width, capsule.Y, preview.Width, preview.Height),
            _ => new RectangleF(capsule.X, capsule.Bottom, preview.Width, preview.Height),
        };
        var x = Math.Clamp(rect.X, work.Left, MathF.Max(work.Left, work.Right - rect.Width));
        var y = Math.Clamp(rect.Y, work.Top, MathF.Max(work.Top, work.Bottom - rect.Height));
        return new(x, y, rect.Width, rect.Height);
    }

    /// <summary>悬浮胶囊夹紧到工作区（屏幕拔除后找回时不落到屏幕外）。</summary>
    internal static PointF ClampFloat(RectangleF work, double x, double y) =>
        new(Math.Clamp((float)x, work.Left, MathF.Max(work.Left, work.Right - CapsuleWidth)),
            Math.Clamp((float)y, work.Top, MathF.Max(work.Top, work.Bottom - CapsuleHeight)));

    /// <summary>队列几何快照：协调层按它摆放窗口，不重复推导。</summary>
    internal sealed record QueueLayout(RectangleF Host, RectangleF Master, int VisibleMembers, float Start, float Scroll);
}
