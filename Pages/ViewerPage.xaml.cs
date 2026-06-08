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

// ─── Annotation tool enum ─────────────────────────────────────────────────────

internal enum AnnotationTool
{
    None, Selection, Highlight, Underline, Freehand, Line, Rectangle, Comment
}

// ─── Per-page annotation state ────────────────────────────────────────────────

internal sealed class PageAnnotationState
{
    public int PageIndex { get; init; }
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }
    public Canvas AnnotationCanvas { get; init; } = null!;

    // Freehand stroke in progress
    public Polyline? ActivePolyline { get; set; }

    // Drag-based shape in progress (highlight rect, underline line, line, rect, selection)
    public bool IsDragging { get; set; }
    public Point DragAnchor { get; set; }
    public Shape? DragShape { get; set; }

    // Stored selection region (from Selection tool) — used to apply highlight/underline
    public Rect? SelectedRegion { get; set; }
    public Rectangle? SelectionOverlay { get; set; }
}

// ─── Page ─────────────────────────────────────────────────────────────────────

public sealed partial class ViewerPage : Page
{
    // ─── State ────────────────────────────────────────────────────────────────

    private PdfDocument? _pdfDoc;
    private string? _currentFilePath;
    private long _currentFileId;

    private AnnotationTool _activeTool = AnnotationTool.None;

    private readonly List<PageAnnotationState> _pageStates = new();

    // Comment placement
    private Point _commentDropPoint;
    private int _commentDropPage;

    // ─── Current annotation colors (user-adjustable via ColorPicker flyouts) ──
    private Color _penColor = Color.FromArgb(255, 30, 120, 220);
    private Color _highlightColor = Color.FromArgb(140, 255, 220, 0);
    private Color _underlineColor = Color.FromArgb(255, 220, 50, 50);
    private Color _shapeColor = Color.FromArgb(255, 255, 80, 0);

    // ─── Constructor ─────────────────────────────────────────────────────────

    public ViewerPage()
    {
        InitializeComponent();

        // Initialise color pickers with defaults
        PenColorPicker.Color = _penColor;
        HighlightColorPicker.Color = _highlightColor;
        UnderlineColorPicker.Color = _underlineColor;
        ShapeColorPicker.Color = _shapeColor;
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

    // ─── Add to folder ────────────────────────────────────────────────────────

    private async void BtnAddToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFileId <= 0) return;

        // Reuse AllPdfsPage's dialog (or open a lightweight version directly)
        var folders = await DatabaseManager.Instance.GetFoldersAsync();

        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 280,
            ItemsSource = folders
        };

        var dialog = new ContentDialog
        {
            Title = "Add PDF to Folder",
            Content = folders.Count > 0
                ? (object)lv
                : new TextBlock { Text = "Create folders first in Virtual Folders." },
            PrimaryButtonText = folders.Count > 0 ? "Add" : "",
            SecondaryButtonText = "Remove from Folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && lv.SelectedItem is FolderRecord sel)
            await DatabaseManager.Instance.AssignFileToFolderAsync(_currentFileId, sel.FolderId);
        else if (result == ContentDialogResult.Secondary)
            await DatabaseManager.Instance.AssignFileToFolderAsync(_currentFileId, null);
    }

    // ─── PDF rendering engine ─────────────────────────────────────────────────

    public async Task LoadPdfAsync(string filePath)
    {
        _currentFilePath = filePath;
        _currentFileId = await DatabaseManager.Instance.EnsureFileTrackedAsync(filePath);
        await DatabaseManager.Instance.UpdateFileLastOpenedAsync(_currentFileId);

        StorageFile sf = await StorageFile.GetFileFromPathAsync(filePath);
        _pdfDoc = await PdfDocument.LoadFromFileAsync(sf);

        _pageStates.Clear();
        // Remove all page grids but keep the empty placeholder
        while (PdfPageContainer.Children.Count > 1)
            PdfPageContainer.Children.RemoveAt(PdfPageContainer.Children.Count - 1);
        EmptyStatePlaceholder.Visibility = Visibility.Collapsed;

        // Enable toolbar buttons now that a document is loaded
        BtnAddToFolder.IsEnabled = true;
        BtnBookmark.IsEnabled = true;

        for (uint i = 0; i < _pdfDoc.PageCount; i++)
        {
            using PdfPage pdfPage = _pdfDoc.GetPage(i);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)pdfPage.Size.Width,
                DestinationHeight = (uint)pdfPage.Size.Height
            };

            using var stream = new InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream, options);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            PdfPageContainer.Children.Add(
                BuildPageGrid(bitmap, pdfPage.Size.Width, pdfPage.Size.Height, (int)i));
        }
    }

    // ─── Three-layer page factory ─────────────────────────────────────────────

    private Grid BuildPageGrid(BitmapImage bitmap, double pw, double ph, int pageIndex)
    {
        var outer = new Grid
        {
            Width = pw,
            Height = ph,
            Tag = pageIndex,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 8)
        };

        // LAYER 0 — Raster bitmap
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

        // LAYER 1 — Transparent hit-test passthrough overlay
        var passthrough = new Grid
        {
            Width = pw,
            Height = ph,
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false
        };
        Canvas.SetZIndex(passthrough, 1);
        outer.Children.Add(passthrough);

        // LAYER 2 — Annotation canvas (pointer events + drawn shapes)
        var annotCanvas = new Canvas
        {
            Width = pw,
            Height = ph,
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false  // enabled only when a tool is active
        };
        Canvas.SetZIndex(annotCanvas, 2);

        var ps = new PageAnnotationState
        {
            PageIndex = pageIndex,
            PageWidth = pw,
            PageHeight = ph,
            AnnotationCanvas = annotCanvas
        };
        _pageStates.Add(ps);

        annotCanvas.PointerPressed += (_, e) => AnnotCanvas_PointerPressed(ps, e);
        annotCanvas.PointerMoved += (_, e) => AnnotCanvas_PointerMoved(ps, e);
        annotCanvas.PointerReleased += (_, e) => AnnotCanvas_PointerReleased(ps, e);

        outer.Children.Add(annotCanvas);
        return outer;
    }

    // ─── Pointer: PRESSED ─────────────────────────────────────────────────────

    private void AnnotCanvas_PointerPressed(PageAnnotationState ps, PointerRoutedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(ps.AnnotationCanvas).Position;

        switch (_activeTool)
        {
            // ── Freehand pen ──────────────────────────────────────────────────
            case AnnotationTool.Freehand:
            {
                var pl = new Polyline
                {
                    Stroke = new SolidColorBrush(_penColor),
                    StrokeThickness = 3,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                pl.Points.Add(pos);
                ps.AnnotationCanvas.Children.Add(pl);
                ps.ActivePolyline = pl;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            // ── Highlight — drag to create filled rectangle ───────────────────
            case AnnotationTool.Highlight:
            {
                ps.IsDragging = true;
                ps.DragAnchor = pos;
                var rect = new Rectangle
                {
                    Fill = new SolidColorBrush(_highlightColor),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, pos.X);
                Canvas.SetTop(rect, pos.Y);
                ps.AnnotationCanvas.Children.Add(rect);
                ps.DragShape = rect;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            // ── Underline — drag creates a line at the BOTTOM of the drag area ─
            case AnnotationTool.Underline:
            {
                ps.IsDragging = true;
                ps.DragAnchor = pos;
                var line = new Line
                {
                    Stroke = new SolidColorBrush(_underlineColor),
                    StrokeThickness = 2,
                    X1 = pos.X, Y1 = pos.Y,
                    X2 = pos.X, Y2 = pos.Y
                };
                ps.AnnotationCanvas.Children.Add(line);
                ps.DragShape = line;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            // ── Straight line ─────────────────────────────────────────────────
            case AnnotationTool.Line:
            {
                ps.IsDragging = true;
                ps.DragAnchor = pos;
                var line = new Line
                {
                    Stroke = new SolidColorBrush(_shapeColor),
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    X1 = pos.X, Y1 = pos.Y,
                    X2 = pos.X, Y2 = pos.Y
                };
                ps.AnnotationCanvas.Children.Add(line);
                ps.DragShape = line;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            // ── Rectangle ─────────────────────────────────────────────────────
            case AnnotationTool.Rectangle:
            {
                ps.IsDragging = true;
                ps.DragAnchor = pos;
                var rect = new Rectangle
                {
                    Stroke = new SolidColorBrush(_shapeColor),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(18, _shapeColor.R, _shapeColor.G, _shapeColor.B)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, pos.X);
                Canvas.SetTop(rect, pos.Y);
                ps.AnnotationCanvas.Children.Add(rect);
                ps.DragShape = rect;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            // ── Selection region ──────────────────────────────────────────────
            case AnnotationTool.Selection:
            {
                // Remove previous selection overlay if any
                if (ps.SelectionOverlay is not null)
                    ps.AnnotationCanvas.Children.Remove(ps.SelectionOverlay);

                ps.IsDragging = true;
                ps.DragAnchor = pos;
                var selRect = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(60, 0, 120, 215)),
                    Stroke = new SolidColorBrush(Color.FromArgb(200, 0, 120, 215)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(selRect, pos.X);
                Canvas.SetTop(selRect, pos.Y);
                ps.AnnotationCanvas.Children.Add(selRect);
                ps.DragShape = selRect;
                ps.SelectionOverlay = selRect;
                ps.AnnotationCanvas.CapturePointer(e.Pointer);
                break;
            }

            // ── Comment pin ───────────────────────────────────────────────────
            case AnnotationTool.Comment:
                _commentDropPoint = pos;
                _commentDropPage = ps.PageIndex;
                CommentInputTip.IsOpen = true;
                break;
        }

        e.Handled = true;
    }

    // ─── Pointer: MOVED ───────────────────────────────────────────────────────

    private void AnnotCanvas_PointerMoved(PageAnnotationState ps, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(ps.AnnotationCanvas).Properties.IsLeftButtonPressed) return;
        Point pos = e.GetCurrentPoint(ps.AnnotationCanvas).Position;

        switch (_activeTool)
        {
            case AnnotationTool.Freehand:
                ps.ActivePolyline?.Points.Add(pos);
                break;

            case AnnotationTool.Highlight:
            case AnnotationTool.Rectangle:
            case AnnotationTool.Selection:
                if (ps.IsDragging && ps.DragShape is Rectangle rect)
                    UpdateRectShape(rect, ps.DragAnchor, pos);
                break;

            case AnnotationTool.Underline:
                if (ps.IsDragging && ps.DragShape is Line uline)
                {
                    // Underline: horizontal line at the bottom (max Y) of the drag area
                    uline.X1 = Math.Min(ps.DragAnchor.X, pos.X);
                    uline.Y1 = Math.Max(ps.DragAnchor.Y, pos.Y);
                    uline.X2 = Math.Max(ps.DragAnchor.X, pos.X);
                    uline.Y2 = Math.Max(ps.DragAnchor.Y, pos.Y);
                }
                break;

            case AnnotationTool.Line:
                if (ps.IsDragging && ps.DragShape is Line sline)
                {
                    sline.X2 = pos.X;
                    sline.Y2 = pos.Y;
                }
                break;
        }

        e.Handled = true;
    }

    // ─── Pointer: RELEASED ───────────────────────────────────────────────────

    private async void AnnotCanvas_PointerReleased(PageAnnotationState ps, PointerRoutedEventArgs e)
    {
        Point pos = e.GetCurrentPoint(ps.AnnotationCanvas).Position;
        ps.AnnotationCanvas.ReleasePointerCapture(e.Pointer);

        switch (_activeTool)
        {
            case AnnotationTool.Freehand:
                if (_currentFileId > 0 && ps.ActivePolyline?.Points.Count > 0)
                {
                    var first = ps.ActivePolyline.Points[0];
                    await DatabaseManager.Instance.SaveAnnotationAsync(
                        _currentFileId, ps.PageIndex + 1, "FREEHAND",
                        first.X, first.Y, 0, 0);
                }
                ps.ActivePolyline = null;
                break;

            case AnnotationTool.Highlight:
                if (ps.IsDragging)
                {
                    ps.IsDragging = false;
                    if (ps.DragShape is Rectangle hr && hr.Width >= 4 && hr.Height >= 4)
                    {
                        if (_currentFileId > 0)
                            await DatabaseManager.Instance.SaveAnnotationAsync(
                                _currentFileId, ps.PageIndex + 1, "HIGHLIGHT",
                                Canvas.GetLeft(hr), Canvas.GetTop(hr), hr.Width, hr.Height);
                    }
                    else if (ps.DragShape is Rectangle small && small.Width < 4)
                    {
                        ps.AnnotationCanvas.Children.Remove(small);
                    }
                    ps.DragShape = null;
                }
                break;

            case AnnotationTool.Underline:
            case AnnotationTool.Line:
                if (ps.IsDragging)
                {
                    ps.IsDragging = false;
                    if (ps.DragShape is Line ln && Math.Abs(ln.X2 - ln.X1) >= 4)
                    {
                        string type = _activeTool == AnnotationTool.Underline ? "UNDERLINE" : "LINE";
                        if (_currentFileId > 0)
                            await DatabaseManager.Instance.SaveAnnotationAsync(
                                _currentFileId, ps.PageIndex + 1, type,
                                ln.X1, ln.Y1, ln.X2 - ln.X1, ln.Y2 - ln.Y1);
                    }
                    else
                    {
                        ps.AnnotationCanvas.Children.Remove(ps.DragShape);
                    }
                    ps.DragShape = null;
                }
                break;

            case AnnotationTool.Rectangle:
                if (ps.IsDragging)
                {
                    ps.IsDragging = false;
                    if (ps.DragShape is Rectangle rct && rct.Width >= 4 && rct.Height >= 4)
                    {
                        if (_currentFileId > 0)
                            await DatabaseManager.Instance.SaveAnnotationAsync(
                                _currentFileId, ps.PageIndex + 1, "RECTANGLE",
                                Canvas.GetLeft(rct), Canvas.GetTop(rct), rct.Width, rct.Height);
                    }
                    else
                    {
                        ps.AnnotationCanvas.Children.Remove(ps.DragShape);
                    }
                    ps.DragShape = null;
                }
                break;

            case AnnotationTool.Selection:
                if (ps.IsDragging)
                {
                    ps.IsDragging = false;
                    if (ps.DragShape is Rectangle selRect && selRect.Width >= 4 && selRect.Height >= 4)
                    {
                        ps.SelectedRegion = new Rect(
                            Canvas.GetLeft(selRect), Canvas.GetTop(selRect),
                            selRect.Width, selRect.Height);
                        SelectionInfoBar.IsOpen = true;
                    }
                    else
                    {
                        ps.AnnotationCanvas.Children.Remove(ps.DragShape);
                        ps.SelectionOverlay = null;
                    }
                    ps.DragShape = null;
                }
                break;
        }

        e.Handled = true;
    }

    // ─── Rectangle shape updater ──────────────────────────────────────────────

    private static void UpdateRectShape(Rectangle rect, Point anchor, Point current)
    {
        double x = Math.Min(anchor.X, current.X);
        double y = Math.Min(anchor.Y, current.Y);
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = Math.Abs(current.X - anchor.X);
        rect.Height = Math.Abs(current.Y - anchor.Y);
    }

    // ─── CommandBar tool switching ────────────────────────────────────────────

    private readonly AppBarToggleButton[] _allToggles = [];  // initialised in AnnotationTool_Checked

    private void AnnotationTool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not AppBarToggleButton btn) return;

        // Radio behaviour: uncheck all other toggles
        foreach (var t in new[] { BtnSelection, BtnHighlight, BtnUnderline,
                                   BtnFreehand, BtnLine, BtnRectangle, BtnComment })
        {
            if (t != btn && t.IsChecked == true)
                t.IsChecked = false;
        }

        _activeTool = (btn.Tag as string) switch
        {
            "selection" => AnnotationTool.Selection,
            "highlight" => AnnotationTool.Highlight,
            "underline" => AnnotationTool.Underline,
            "freehand" => AnnotationTool.Freehand,
            "line" => AnnotationTool.Line,
            "rectangle" => AnnotationTool.Rectangle,
            "comment" => AnnotationTool.Comment,
            _ => AnnotationTool.None
        };

        // Show selection hint when selection tool activated
        if (_activeTool == AnnotationTool.Selection)
            SelectionInfoBar.IsOpen = true;

        SetCanvasHitTest(true);
    }

    private void AnnotationTool_Unchecked(object sender, RoutedEventArgs e)
    {
        bool anyActive = BtnSelection.IsChecked == true || BtnHighlight.IsChecked == true
            || BtnUnderline.IsChecked == true || BtnFreehand.IsChecked == true
            || BtnLine.IsChecked == true || BtnRectangle.IsChecked == true
            || BtnComment.IsChecked == true;

        if (!anyActive)
        {
            _activeTool = AnnotationTool.None;
            SetCanvasHitTest(false);
        }
    }

    private void SetCanvasHitTest(bool enabled)
    {
        foreach (var ps in _pageStates)
            ps.AnnotationCanvas.IsHitTestVisible = enabled;
    }

    // ─── Color picker handlers ────────────────────────────────────────────────

    private void PenColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        => _penColor = args.NewColor;

    private void HighlightColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        => _highlightColor = args.NewColor;

    private void UnderlineColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        => _underlineColor = args.NewColor;

    private void ShapeColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        => _shapeColor = args.NewColor;

    // ─── Clear all ────────────────────────────────────────────────────────────

    private void BtnClearInk_Click(object sender, RoutedEventArgs e)
    {
        foreach (var ps in _pageStates)
        {
            ps.AnnotationCanvas.Children.Clear();
            ps.SelectedRegion = null;
            ps.SelectionOverlay = null;
        }
    }

    // ─── Bookmark ─────────────────────────────────────────────────────────────

    private async void BtnBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFileId <= 0) return;
        await DatabaseManager.Instance.AddBookmarkAsync(_currentFileId, 1, PdfScrollViewer.VerticalOffset);
    }

    // ─── Comment ─────────────────────────────────────────────────────────────

    private async void CommentSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string text = CommentTextBox.Text.Trim();
        CommentInputTip.IsOpen = false;
        CommentTextBox.Text = string.Empty;
        if (string.IsNullOrEmpty(text) || _currentFileId <= 0 || _commentDropPage >= _pageStates.Count) return;

        var ps = _pageStates[_commentDropPage];

        // Draw comment pin dot
        var dot = new Ellipse
        {
            Width = 22, Height = 22,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 175, 0)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 200, 130, 0)),
            StrokeThickness = 1.5
        };
        Canvas.SetLeft(dot, _commentDropPoint.X - 11);
        Canvas.SetTop(dot, _commentDropPoint.Y - 11);
        ToolTipService.SetToolTip(dot, text);
        ps.AnnotationCanvas.Children.Add(dot);

        await DatabaseManager.Instance.SaveAnnotationAsync(
            _currentFileId, _commentDropPage + 1, "COMMENT",
            _commentDropPoint.X, _commentDropPoint.Y, 0, 0, text);
    }

    // ─── Zoom ─────────────────────────────────────────────────────────────────

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) =>
        _ = PdfScrollViewer.ChangeView(null, null,
            Math.Min(PdfScrollViewer.ZoomFactor * 1.25f, 6.0f));

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) =>
        _ = PdfScrollViewer.ChangeView(null, null,
            Math.Max(PdfScrollViewer.ZoomFactor * 0.8f, 0.2f));

    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) =>
        _ = PdfScrollViewer.ChangeView(null, null, 1.0f);
}
