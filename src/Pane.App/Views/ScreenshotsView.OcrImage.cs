using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Pane.Services.Screenshots;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;

namespace Pane.Views;

public sealed partial class ScreenshotsView
{
    const int OcrPreviewMaxEdge = 1600;
    OcrRecognition? ocrGeometry;
    OcrSpan? ocrSelectionAnchor;
    OcrSpan? ocrSelectionExtent;
    (int Start, int Length)? ocrDisplayedSelection;
    uint? ocrDragPointer;
    bool ocrFitPending;

    static async Task<BitmapImage> DecodeOcrPreviewAsync(byte[] image, OcrRecognition geometry)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(image);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var preview = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
        if (geometry.Width >= geometry.Height && geometry.Width > OcrPreviewMaxEdge) preview.DecodePixelWidth = OcrPreviewMaxEdge;
        else if (geometry.Height > OcrPreviewMaxEdge) preview.DecodePixelHeight = OcrPreviewMaxEdge;
        await preview.SetSourceAsync(stream);
        return preview;
    }

    (double Scale, double Left, double Top) OcrProjection(OcrRecognition geometry)
    {
        var width = OcrSurface.Width;
        var height = OcrSurface.Height;
        var scale = Math.Min(width / geometry.Width, height / geometry.Height);
        return (scale, (width - scale * geometry.Width) / 2, (height - scale * geometry.Height) / 2);
    }

    void OcrViewportSizeChanged(object sender, SizeChangedEventArgs args) => LayoutOcrViewport();

    void LayoutOcrViewport()
    {
        if (disposed || ocrGeometry == null || OcrScroller.ActualWidth < 1 || OcrScroller.ActualHeight < 1) return;
        OcrSurface.Width = Math.Floor(OcrScroller.ActualWidth);
        OcrSurface.Height = Math.Floor(OcrScroller.ActualHeight);
        LayoutOcrImage();
        if (ocrFitPending) FitOcrImage();
    }

    void OcrImageViewChanged(object sender, ScrollViewerViewChangedEventArgs args) => RefreshOcrZoomButtons();

    void RefreshOcrZoomButtons()
    {
        if (OcrZoomIn == null || OcrZoomOut == null || OcrFit == null) return;
        OcrZoomIn.IsEnabled = OcrScroller.ZoomFactor < OcrScroller.MaxZoomFactor;
        OcrFit.IsEnabled = OcrZoomOut.IsEnabled = OcrScroller.ZoomFactor > OcrScroller.MinZoomFactor;
    }

    void OcrZoomInClicked(object sender, RoutedEventArgs args) => ZoomOcrImage(1.5f);
    void OcrZoomOutClicked(object sender, RoutedEventArgs args) => ZoomOcrImage(1f / 1.5f);
    void OcrFitClicked(object sender, RoutedEventArgs args) => FitOcrImage();

    void ZoomOcrImage(float factor)
    {
        var current = OcrScroller.ZoomFactor;
        var next = Math.Clamp(current * factor, OcrScroller.MinZoomFactor, OcrScroller.MaxZoomFactor);
        if (next == OcrScroller.MinZoomFactor) { FitOcrImage(); return; }
        if (next == current) return;
        var ratio = next / current;
        var halfWidth = OcrScroller.ViewportWidth / 2;
        var halfHeight = OcrScroller.ViewportHeight / 2;
        var left = Math.Max(0, (OcrScroller.HorizontalOffset + halfWidth) * ratio - halfWidth);
        var top = Math.Max(0, (OcrScroller.VerticalOffset + halfHeight) * ratio - halfHeight);
        OcrScroller.ChangeView(left, top, next, disableAnimation: true);
    }

    void FitOcrImage()
    {
        var minimum = OcrScroller.MinZoomFactor;
        ocrFitPending = !(OcrScroller.ZoomFactor == minimum && OcrScroller.HorizontalOffset == 0 && OcrScroller.VerticalOffset == 0) &&
            !OcrScroller.ChangeView(0, 0, minimum, disableAnimation: true);
        RefreshOcrZoomButtons();
    }

    void LayoutOcrImage()
    {
        if (ocrGeometry is not { } geometry) return;
        var (scale, left, top) = OcrProjection(geometry);
        if (!double.IsFinite(scale) || scale <= 0) return;
        OcrPreview.Width = geometry.Width * scale;
        OcrPreview.Height = geometry.Height * scale;
        if (ocrSelectionAnchor is not { } anchor || ocrSelectionExtent is not { } extent) return;
        var highlight = new PathGeometry();
        foreach (var quad in geometry.HighlightRange(anchor, extent))
        {
            var figure = new PathFigure { StartPoint = Place(quad.First), IsClosed = true, IsFilled = false };
            figure.Segments.Add(new PolyLineSegment { Points = new PointCollection { Place(quad.Second), Place(quad.Third), Place(quad.Fourth) } });
            highlight.Figures.Add(figure);
        }
        OcrHighlight.Data = highlight;

        Point Place(OcrPoint point) => new(left + point.X * scale, top + point.Y * scale);
    }

    OcrSpan? HitOcrImage(Point point)
    {
        if (busy || editingOcr || disposed || ocrGeometry is not { } geometry) return null;
        var (scale, left, top) = OcrProjection(geometry);
        return scale > 0 ? geometry.Hit(new((point.X - left) / scale, (point.Y - top) / scale)) : null;
    }

    void OcrImagePressed(object sender, PointerRoutedEventArgs args)
    {
        var pointer = args.GetCurrentPoint(OcrSurface);
        if (ocrDragPointer != null || pointer.PointerDeviceType == PointerDeviceType.Touch || !pointer.Properties.IsLeftButtonPressed ||
            HitOcrImage(pointer.Position) is not { } target) return;
        var anchor = args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) ? ocrSelectionAnchor ?? target : target;
        OcrResult.Focus(FocusState.Programmatic);
        if (!SelectOcrImageRange(anchor, target)) return;
        if (OcrSurface.CapturePointer(args.Pointer)) ocrDragPointer = args.Pointer.PointerId;
        args.Handled = true;
    }

    void OcrImageTapped(object sender, TappedRoutedEventArgs args)
    {
        if (args.PointerDeviceType != PointerDeviceType.Touch || HitOcrImage(args.GetPosition(OcrSurface)) is not { } target) return;
        if (SelectOcrImageRange(target, target)) args.Handled = true;
    }

    void OcrImageMoved(object sender, PointerRoutedEventArgs args)
    {
        if (ocrDragPointer != args.Pointer.PointerId) return;
        if (ocrSelectionAnchor is { } anchor && HitOcrImage(args.GetCurrentPoint(OcrSurface).Position) is { } target)
            SelectOcrImageRange(anchor, target);
        args.Handled = true;
    }

    void OcrImageReleased(object sender, PointerRoutedEventArgs args)
    {
        if (ocrDragPointer != args.Pointer.PointerId) return;
        OcrImageMoved(sender, args);
        ocrDragPointer = null;
        OcrSurface.ReleasePointerCapture(args.Pointer);
    }

    void OcrImageCaptureEnded(object sender, PointerRoutedEventArgs args)
    {
        if (ocrDragPointer == args.Pointer.PointerId) ocrDragPointer = null;
    }

    bool SelectOcrImageRange(OcrSpan anchor, OcrSpan extent)
    {
        if (ocrSelectionAnchor == anchor && ocrSelectionExtent == extent) return true;
        if (ocrGeometry?.DisplayRange(OcrResult.Text, anchor, extent) is not { } range) return false;
        ocrSelectionAnchor = anchor;
        ocrSelectionExtent = extent;
        ocrDisplayedSelection = range;
        OcrResult.Select(range.Start, range.Length);
        LayoutOcrImage();
        return true;
    }

    void ResetOcrImageSelection()
    {
        if (ocrDragPointer != null)
        {
            ocrDragPointer = null;
            OcrSurface.ReleasePointerCaptures();
        }
        ocrSelectionAnchor = ocrSelectionExtent = null;
        ocrDisplayedSelection = null;
        OcrHighlight.Data = null;
    }

    void OcrTextSelectionChanged(object sender, RoutedEventArgs args)
    {
        if (ocrDisplayedSelection is { } range && range != (OcrResult.SelectionStart, OcrResult.SelectionLength))
            ResetOcrImageSelection();
        RefreshOcrSelectionAction();
    }

    void RefreshOcrSelectionAction()
    {
        if (CopyOcrSelectionItem != null) CopyOcrSelectionItem.IsEnabled = !busy && OcrResult.SelectionLength > 0;
    }
}
