using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MyPdfViewer.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MyPdfViewer.Pages;

// ─── View-model wrappers ──────────────────────────────────────────────────────

/// <summary>Wraps <see cref="FolderRecord"/> for XAML binding.</summary>
public sealed class FolderViewModel
{
    public long FolderId { get; init; }
    public string FolderName { get; init; } = "";
    public string Emoji { get; init; } = "📁";
}

/// <summary>Wraps <see cref="FileRecord"/> for XAML binding, adds display strings.</summary>
public sealed class FileViewModel
{
    public long FileId { get; init; }
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public long? FolderId { get; init; }
    public string LastOpenedDisplay { get; init; } = "";

    public static FileViewModel From(FileRecord r) => new()
    {
        FileId = r.FileId,
        FilePath = r.FilePath,
        FileName = r.FileName,
        FolderId = r.FolderId,
        LastOpenedDisplay = r.LastOpenedAt.HasValue
            ? r.LastOpenedAt.Value.ToLocalTime().ToString("MMM d, h:mm tt")
            : "Not yet opened"
    };
}

// ─── Page modes ───────────────────────────────────────────────────────────────

internal enum FolderPageMode { AllFolders, RecentFiles, FolderContents }

// ─── Page ─────────────────────────────────────────────────────────────────────

public sealed partial class FoldersPage : Page
{
    private FolderPageMode _mode = FolderPageMode.AllFolders;
    private long _currentFolderId;
    private bool _isListView = true;

    private readonly ObservableCollection<object> _items = new();

    public FoldersPage()
    {
        InitializeComponent();
    }

    // ─── Navigation parameter ─────────────────────────────────────────────────

    protected override async void OnNavigatedTo(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _isListView = AppSettings.Load().IsListView;
        BtnListViewToggle.IsChecked = _isListView;
        BtnGridViewToggle.IsChecked = !_isListView;
        UpdateViewVisibility();

        if (e.Parameter is FolderPageParam p)
        {
            if (p.ShowRecent)
            {
                _mode = FolderPageMode.RecentFiles;
            }
            else if (p.FolderId.HasValue)
            {
                _mode = FolderPageMode.FolderContents;
                _currentFolderId = p.FolderId.Value;
            }
            else
            {
                _mode = FolderPageMode.AllFolders;
            }
        }
        else
        {
            _mode = FolderPageMode.AllFolders;
        }

        await RefreshAsync();
    }

    // ─── Refresh ──────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        _items.Clear();

        switch (_mode)
        {
            case FolderPageMode.AllFolders:
                await LoadAllFoldersAsync();
                break;

            case FolderPageMode.RecentFiles:
                await LoadRecentFilesAsync();
                break;

            case FolderPageMode.FolderContents:
                await LoadFolderContentsAsync(_currentFolderId);
                break;
        }
    }

    // ─── All Folders mode ─────────────────────────────────────────────────────

    private async Task LoadAllFoldersAsync()
    {
        PageEmojiIcon.Text = "📁";
        PageTitle.Text = "Virtual Folders";
        RecentSection.Visibility = Visibility.Visible;
        CreateFolderExpander.Visibility = Visibility.Visible;
        FolderActionsPanel.Visibility = Visibility.Collapsed;

        SetContentTemplates(
            (DataTemplate)Resources["FolderListTemplate"],
            (DataTemplate)Resources["FolderGridTemplate"]);

        // Populate recently-opened horizontal strip
        RecentFilesPanel.Children.Clear();
        var recent = await DatabaseManager.Instance.GetRecentFilesAsync(12);
        foreach (var f in recent)
            RecentFilesPanel.Children.Add(BuildRecentCard(f));

        // Populate folder list/grid
        var folders = await DatabaseManager.Instance.GetFoldersAsync();
        foreach (var f in folders)
            _items.Add(new FolderViewModel
            {
                FolderId = f.FolderId,
                FolderName = f.FolderName,
                Emoji = f.Emoji
            });
    }

    // ─── Recent Files mode ────────────────────────────────────────────────────

    private async Task LoadRecentFilesAsync()
    {
        PageEmojiIcon.Text = "🕐";
        PageTitle.Text = "Recently Opened";
        RecentSection.Visibility = Visibility.Collapsed;
        CreateFolderExpander.Visibility = Visibility.Collapsed;
        FolderActionsPanel.Visibility = Visibility.Collapsed;

        SetContentTemplates(
            (DataTemplate)Resources["FileListTemplate"],
            (DataTemplate)Resources["FileGridTemplate"]);

        var files = await DatabaseManager.Instance.GetRecentFilesAsync(50);
        foreach (var f in files)
            _items.Add(FileViewModel.From(f));
    }

    // ─── Folder Contents mode ─────────────────────────────────────────────────

    private async Task LoadFolderContentsAsync(long folderId)
    {
        RecentSection.Visibility = Visibility.Collapsed;
        CreateFolderExpander.Visibility = Visibility.Collapsed;
        FolderActionsPanel.Visibility = Visibility.Visible;

        var folders = await DatabaseManager.Instance.GetFoldersAsync();
        var current = folders.Find(f => f.FolderId == folderId);
        PageEmojiIcon.Text = current?.Emoji ?? "📁";
        PageTitle.Text = current?.FolderName ?? "Folder";

        SetContentTemplates(
            (DataTemplate)Resources["FileListTemplate"],
            (DataTemplate)Resources["FileGridTemplate"]);

        var files = await DatabaseManager.Instance.GetFilesInFolderAsync(folderId);
        foreach (var f in files)
            _items.Add(FileViewModel.From(f));
    }

    // ─── Recently Opened horizontal card builder ──────────────────────────────

    private Border BuildRecentCard(FileRecord f)
    {
        var card = new Border
        {
            Width = 120,
            Height = 100,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Tag = f.FilePath
        };

        var stack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new Microsoft.UI.Xaml.Controls.FontIcon
        {
            Glyph = "\uEA90",
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
        });
        stack.Children.Add(new TextBlock
        {
            Text = f.FileName,
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2
        });

        card.Child = stack;
        card.PointerPressed += (_, _) => Frame.Navigate(typeof(ViewerPage), f.FilePath);
        return card;
    }

    // ─── Template switcher ────────────────────────────────────────────────────

    private void SetContentTemplates(DataTemplate listTemplate, DataTemplate gridTemplate)
    {
        ContentListView.ItemsSource = null;
        ContentGridView.ItemsSource = null;
        ContentListView.ItemTemplate = listTemplate;
        ContentGridView.ItemTemplate = gridTemplate;
        UpdateViewVisibility();
    }

    // ─── View visibility helper ───────────────────────────────────────────────

    private void UpdateViewVisibility()
    {
        if (_isListView)
        {
            ContentGridView.ItemsSource = null;
            ContentGridView.Visibility = Visibility.Collapsed;

            ContentListView.ItemsSource = _items;
            ContentListView.Visibility = Visibility.Visible;
        }
        else
        {
            ContentListView.ItemsSource = null;
            ContentListView.Visibility = Visibility.Collapsed;

            ContentGridView.ItemsSource = _items;
            ContentGridView.Visibility = Visibility.Visible;
        }
    }

    // ─── View toggle (List ↔ Grid) ────────────────────────────────────────────

    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BtnListViewToggle))
        {
            _isListView = true;
            BtnListViewToggle.IsChecked = true;
            BtnGridViewToggle.IsChecked = false;
        }
        else
        {
            _isListView = false;
            BtnGridViewToggle.IsChecked = true;
            BtnListViewToggle.IsChecked = false;
        }

        var settings = AppSettings.Load();
        settings.IsListView = _isListView;
        settings.Save();

        UpdateViewVisibility();
    }

    // ─── Content item click ───────────────────────────────────────────────────

    private void ContentItem_Click(object sender, ItemClickEventArgs e)
    {
        switch (e.ClickedItem)
        {
            case FileViewModel fvm:
                Frame.Navigate(typeof(ViewerPage), fvm.FilePath);
                break;

            case FolderViewModel folderVm:
                Frame.Navigate(typeof(FoldersPage),
                    new FolderPageParam(FolderId: folderVm.FolderId));
                break;
        }
    }

    // ─── Create folder ────────────────────────────────────────────────────────

    private async void BtnCreateFolder_Click(object sender, RoutedEventArgs e)
    {
        string name = FolderNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        string emoji = string.IsNullOrWhiteSpace(EmojiBox.Text) ? "📁" : EmojiBox.Text.Trim();
        FolderNameBox.Text = string.Empty;
        EmojiBox.Text = "📁";
        CreateFolderExpander.IsExpanded = false;

        await DatabaseManager.Instance.CreateFolderAsync(name, emoji);

        // Refresh nav pane sub-items
        if (MainWindow.Instance is not null)
            await MainWindow.Instance.RefreshFolderNavItemsAsync();

        await RefreshAsync();
    }

    // ─── Delete folder ────────────────────────────────────────────────────────

    private async void BtnDeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        long folderId = Convert.ToInt64(btn.Tag);

        var dialog = new ContentDialog
        {
            Title = "Delete Folder",
            Content = "Delete this folder? Files inside will NOT be deleted — they'll become unassigned.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await DatabaseManager.Instance.DeleteFolderAsync(folderId);

        if (MainWindow.Instance is not null)
            await MainWindow.Instance.RefreshFolderNavItemsAsync();

        await RefreshAsync();
    }

    // ─── Inner Folder Actions ─────────────────────────────────────────────────

    private async void BtnRemoveFromFileFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long fileId) return;
        await DatabaseManager.Instance.AssignFileToFolderAsync(fileId, null);
        await RefreshAsync();
    }

    private async void BtnRenameFolder_Click(object sender, RoutedEventArgs e)
    {
        var folders = await DatabaseManager.Instance.GetFoldersAsync();
        var current = folders.Find(f => f.FolderId == _currentFolderId);
        if (current is null) return;

        var nameBox = new TextBox { Text = current.FolderName, PlaceholderText = "Folder name…", HorizontalAlignment = HorizontalAlignment.Stretch };
        var emojiBox = new TextBox { Text = current.Emoji, PlaceholderText = "📁", MaxLength = 2, FontSize = 18, Width = 56, HorizontalAlignment = HorizontalAlignment.Left };

        var stack = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Emoji:" },
                emojiBox,
                new TextBlock { Text = "Name:" },
                nameBox
            }
        };

        var dialog = new ContentDialog
        {
            Title = "Rename Folder",
            Content = stack,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string newName = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        string newEmoji = string.IsNullOrWhiteSpace(emojiBox.Text) ? "📁" : emojiBox.Text.Trim();

        await DatabaseManager.Instance.RenameFolderAsync(_currentFolderId, newName, newEmoji);

        if (MainWindow.Instance is not null)
            await MainWindow.Instance.RefreshFolderNavItemsAsync();

        await RefreshAsync();
    }

    private async void BtnDeleteCurrentFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete Folder",
            Content = "Delete this folder? Files inside will NOT be deleted — they'll become unassigned.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await DatabaseManager.Instance.DeleteFolderAsync(_currentFolderId);

        if (MainWindow.Instance is not null)
            await MainWindow.Instance.RefreshFolderNavItemsAsync();

        _mode = FolderPageMode.AllFolders;
        await RefreshAsync();
    }

    private async void BtnImportToFolder_Click(object sender, RoutedEventArgs e)
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

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        foreach (var file in files)
        {
            long fileId = await DatabaseManager.Instance.EnsureFileTrackedAsync(file.Path);
            await DatabaseManager.Instance.AssignFileToFolderAsync(fileId, _currentFolderId);
        }

        await RefreshAsync();
    }
}
