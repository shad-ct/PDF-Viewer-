using Microsoft.Data.Sqlite;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using MyPdfViewer.Helpers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;

namespace MyPdfViewer.Pages;

// ─── Tool mode ────────────────────────────────────────────────────────────────

internal enum AnnotationTool
{
    None,
    Selection,
    Highlight,
    Underline,
    Freehand,
    Rectangle,
    Comment
}

// ─── Per-page canvas state ────────────────────────────────────────────────────

internal sealed class PageAnnotationState
{
    public int PageIndex { get; init; }
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }

    /// <summary>The WinUI Canvas that receives pointer events and hosts drawn shapes.</summary>
    public Canvas AnnotationCanvas { get; init; } = null!;

    // Freehand / underline stroke being built
    public Polyline? ActivePolyline { get; set; }

    // Rectangle draw state
    public bool IsDrawingRect { get; set; }
    public Point RectAnchor { get; set; }
    public Rectangle? RectPreview { get; set; }
}

// ─── Page ─────────────────────────────────────────────────────────────────────

/// <summary>
/// PDF viewer page with a three-layer Z-index design:
///   Layer 0 — Image (rasterised PDF bitmap)
///   Layer 1 — Grid  (transparent interaction overlay, IsHitTestVisible=false)
///   Layer 2 — Canvas (annotation surface: Polyline, Rectangle, highlight overlays)
/// </summary>
public sealed partial class ViewerPage : Page
{
    // ─── State ────────────────────────────────────────────────────────────────

    private PdfDocument? _pdfDoc;
    private string? _currentFilePath;
    private long _currentFileId;

    private AnnotationTool _activeTool = AnnotationTool.None;

    private readonly List<PageAnnotationState> _pageStates = new();

    // Comment drop-point info
    private Point _commentDropPoint;
    private int _commentDropPage;

    // ─── Constructor ─────────────────────────────────────────────────────────

    public ViewerPage()
    {
        InitializeComponent();
    }

    // ─── Navigation ──────────────────────────────────────────────────────────

    protected override async void OnNavigatedTo(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string filePath && !string.IsNullOrEmpty(filePath))
            await LoadPdfAsync(filePath);
    }

    // ─── File open ────────────────────────────────────────────────────────────

    private async void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".pdf");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            (Application.Current as App)!.MainWindowHandle);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await LoadPdfAsync(file.Path);
    }

    // ─── PDF rendering engine ─────────────────────────────────────────────────

    /// <summary>
    /// Loads and rasterises every page of the PDF at <paramref name="filePath"/>.
    /// Each page gets a three-layer Grid (bitmap / interaction overlay / annotation canvas).
    /// </summary>
    public async Task LoadPdfAsync(string filePath)
    {
        _currentFilePath = filePath;
        _currentFileId = await DatabaseManager.Instance.EnsureFileTrackedAsync(filePath);

        StorageFile sf = await StorageFile.GetFileFromPathAsync(filePath);
        _pdfDoc = await PdfDocument.LoadFromFileAsync(sf);

        _pageStates.Clear();
        PdfPageContainer.Children.Clear();
        EmptyStatePlaceholder.Visibility = Visibility.Collapsed;

        for (uint i = 0; i < _pdfDoc.PageCount; i++)
        {
            using PdfPage pdfPage = _pdfDoc.GetPage(i);

            var renderOptions = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)pdfPage.Size.Width,
                DestinationHeight = (uint)pdfPage.Size.Height
            };

            using var stream = new InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream, renderOptions);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            double pw = pdfPage.Size.Width;
            double ph = pdfPage.Size.Height;

            Grid pageGrid = BuildPageGrid(bitmap, pw, ph, (int)i);
            PdfPageContainer.Children.Add(pageGrid);
        }
    }

    // ─── Layer stack factory ──────────────────────────────────────────────────

    private Grid BuildPageGrid(BitmapImage bitmap, double pw, double ph, int pageIndex)
    {
        // Outer container — drop-shadow via ThemeShadow
        var outer = new Grid
        {
            Width = pw,
            Height = ph,
            Tag = pageIndex,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 8)
        };

        // ── LAYER 0: PDF raster image ──────────────────────────────────────
        var img = new Image
        {
            Source = bitmap,
            Width = pw,
            Height = ph,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        Canvas.SetZIndex(img, 0);
        outer.Children.Add(img);

        // ── LAYER 1: Transparent interaction overlay (hit-test passthrough) ─
        var overlay = new Grid
        {
            Width = pw,
            Height = ph,
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false
        };
        Canvas.SetZIndex(overlay, 1);
        outer.Children.Add(overlay);

        // ── LAYER 2: Annotation canvas ──────────────────────────────────────
        var annotCanvas = new Canvas
        {
            Width = pw,
            Height = ph,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        Canvas.SetZIndex(annotCanvas, 2);

        var pageState = new PageAnnotationState
        {
            PageIndex = pageIndex,
            PageWidth = pw,
            PageHeight = ph,
            AnnotationCanvas = annotCanvas
        };
        _pageStates.Add(pageState);

        // Wire pointer events on the annotation canvas
        annotCanvas.PointerPressed += (s, e) => AnnotCanvas_PointerPressed(pageState, e);
        annotCanvas.PointerMoved += (s, e) => AnnotCanvas_PointerMoved(pageState, e);
        annotCanvas.PointerReleased += (s, e) => AnnotCanvas_PointerReleased(pageState, e);

        outer.Children.Add(annotCanvas);
        return outer;
    }

    // ─── Pointer handlers (per annotation canvas) ─────────────────────────────

    private void AnnotCanvas_PointerPressed(PageAnnotationState ps, PointerRoutedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(ps.AnnotationCanvas).Position;

        switch (_activeTool)
        {
            case AnnotationTool.Freehand:
            {
                var polyline = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 30, 120, 220)),
                    StrokeThickness = 3,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                polyline.Points.Add(pos);
                ps.AnnotationCanvas.Children.Add(polyline);
                ps.ActivePolyline = polyline;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            case AnnotationTool.Highlight:
            {
                var polyline = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(100, 255, 220, 0)),
                    StrokeThickness = 14,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Flat,
                    StrokeEndLineCap = PenLineCap.Flat
                };
                polyline.Points.Add(pos);
                ps.AnnotationCanvas.Children.Add(polyline);
                ps.ActivePolyline = polyline;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            case AnnotationTool.Underline:
            {
                var polyline = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 220, 50, 50)),
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                polyline.Points.Add(pos);
                ps.AnnotationCanvas.Children.Add(polyline);
                ps.ActivePolyline = polyline;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            case AnnotationTool.Rectangle:
            {
                ps.IsDrawingRect = true;
                ps.RectAnchor = pos;

                // Create a preview rectangle shape
                var preview = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 80, 0)),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(20, 255, 80, 0)),
                    Width = 0,
                    Height = 0
                };
                Canvas.SetLeft(preview, pos.X);
                Canvas.SetTop(preview, pos.Y);
                ps.AnnotationCanvas.Children.Add(preview);
                ps.RectPreview = preview;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            case AnnotationTool.Comment:
            {
                _commentDropPoint = pos;
                _commentDropPage = ps.PageIndex;
                CommentInputTip.IsOpen = true;
                break;
            }
        }

        e.Handled = true;
    }

    private void AnnotCanvas_PointerMoved(PageAnnotationState ps, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(ps.AnnotationCanvas).Properties.IsLeftButtonPressed) return;
        Point pos = e.GetCurrentPoint(ps.AnnotationCanvas).Position;

        switch (_activeTool)
        {
            case AnnotationTool.Freehand:
            case AnnotationTool.Highlight:
            case AnnotationTool.Underline:
                ps.ActivePolyline?.Points.Add(pos);
                break;

            case AnnotationTool.Rectangle when ps.IsDrawingRect && ps.RectPreview is not null:
            {
                double x = Math.Min(pos.X, ps.RectAnchor.X);
                double y = Math.Min(pos.Y, ps.RectAnchor.Y);
                double w = Math.Abs(pos.X - ps.RectAnchor.X);
                double h = Math.Abs(pos.Y - ps.RectAnchor.Y);
                Canvas.SetLeft(ps.RectPreview, x);
                Canvas.SetTop(ps.RectPreview, y);
                ps.RectPreview.Width = w;
                ps.RectPreview.Height = h;
                break;
            }
        }

        e.Handled = true;
    }

    private async void AnnotCanvas_PointerReleased(PageAnnotationState ps, PointerRoutedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(ps.AnnotationCanvas).Position;
        ps.AnnotationCanvas.ReleasePointerCapture(e.Pointer);

        switch (_activeTool)
        {
            case AnnotationTool.Freehand:
            case AnnotationTool.Highlight:
            case AnnotationTool.Underline:
            {
                // Persist as annotation if we have a file loaded
                if (_currentFileId > 0 && ps.ActivePolyline?.Points.Count > 0)
                {
                    var first = ps.ActivePolyline.Points[0];
                    string type = _activeTool == AnnotationTool.Highlight ? "HIGHLIGHT"
                        : _activeTool == AnnotationTool.Underline ? "UNDERLINE"
                        : "FREEHAND";
                    await DatabaseManager.Instance.SaveAnnotationAsync(
                        _currentFileId, ps.PageIndex + 1, type,
                        first.X, first.Y, 0, 0);
                }
                ps.ActivePolyline = null;
                break;
            }

            case AnnotationTool.Rectangle when ps.IsDrawingRect:
            {
                ps.IsDrawingRect = false;
                if (ps.RectPreview is null) break;

                double x = Canvas.GetLeft(ps.RectPreview);
                double y = Canvas.GetTop(ps.RectPreview);
                double w = ps.RectPreview.Width;
                double h = ps.RectPreview.Height;

                if (w < 4 || h < 4)
                {
                    // Too small — discard
                    ps.AnnotationCanvas.Children.Remove(ps.RectPreview);
                }
                else if (_currentFileId > 0)
                {
                    await DatabaseManager.Instance.SaveAnnotationAsync(
                        _currentFileId, ps.PageIndex + 1, "RECTANGLE",
                        x, y, w, h);
                }
                ps.RectPreview = null;
                break;
            }
        }

        e.Handled = true;
    }

    // ─── CommandBar tool switching ────────────────────────────────────────────

    private void AnnotationTool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not AppBarToggleButton btn) return;
        UncheckAllToolsExcept(btn);

        _activeTool = (btn.Tag as string) switch
        {
            "selection" => AnnotationTool.Selection,
            "highlight" => AnnotationTool.Highlight,
            "underline" => AnnotationTool.Underline,
            "freehand" => AnnotationTool.Freehand,
            "rectangle" => AnnotationTool.Rectangle,
            "comment" => AnnotationTool.Comment,
            _ => AnnotationTool.None
        };

        // Enable hit-testing on all annotation canvases when a tool is active
        foreach (var ps in _pageStates)
            ps.AnnotationCanvas.IsHitTestVisible = _activeTool != AnnotationTool.None
                                                  && _activeTool != AnnotationTool.Selection;
    }

    private void AnnotationTool_Unchecked(object sender, RoutedEventArgs e)
    {
        _activeTool = AnnotationTool.None;
        foreach (var ps in _pageStates)
            ps.AnnotationCanvas.IsHitTestVisible = false;
    }

    private void UncheckAllToolsExcept(AppBarToggleButton active)
    {
        foreach (var btn in new[] { BtnSelection, BtnHighlight, BtnUnderline,
                                    BtnFreehand, BtnRectangle, BtnComment })
        {
            if (btn != active && btn.IsChecked == true)
                btn.IsChecked = false;
        }
    }

    // ─── Clear annotations ────────────────────────────────────────────────────

    private void BtnClearInk_Click(object sender, RoutedEventArgs e)
    {
        foreach (var ps in _pageStates)
            ps.AnnotationCanvas.Children.Clear();
    }

    // ─── Bookmark ─────────────────────────────────────────────────────────────

    private async void BtnBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFileId <= 0) return;
        double offset = PdfScrollViewer.VerticalOffset;
        await DatabaseManager.Instance.AddBookmarkAsync(_currentFileId, 1, offset);
    }

    // ─── Comment ─────────────────────────────────────────────────────────────

    private async void CommentSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string text = CommentTextBox.Text.Trim();
        CommentInputTip.IsOpen = false;
        CommentTextBox.Text = string.Empty;

        if (string.IsNullOrEmpty(text) || _currentFileId <= 0) return;

        // Draw a comment marker on the canvas
        var ps = _pageStates[_commentDropPage];
        var marker = new Ellipse
        {
            Width = 20,
            Height = 20,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 180, 0)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 200, 130, 0)),
            StrokeThickness = 1.5
        };
        Canvas.SetLeft(marker, _commentDropPoint.X - 10);
        Canvas.SetTop(marker, _commentDropPoint.Y - 10);
        ToolTipService.SetToolTip(marker, text);
        ps.AnnotationCanvas.Children.Add(marker);

        await DatabaseManager.Instance.SaveAnnotationAsync(
            _currentFileId,
            _commentDropPage + 1,
            "COMMENT",
            _commentDropPoint.X,
            _commentDropPoint.Y,
            0, 0,
            text);
    }

    // ─── Zoom ─────────────────────────────────────────────────────────────────

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) =>
        _ = PdfScrollViewer.ChangeView(null, null,
            Math.Min(PdfScrollViewer.ZoomFactor * 1.25f, 5.0f));

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) =>
        _ = PdfScrollViewer.ChangeView(null, null,
            Math.Max(PdfScrollViewer.ZoomFactor * 0.8f, 0.25f));

    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) =>
        _ = PdfScrollViewer.ChangeView(null, null, 1.0f);
}
