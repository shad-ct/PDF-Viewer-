using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyPdfViewer.Helpers;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MyPdfViewer.Pages;

public sealed partial class AllPdfsPage : Page
{
    private readonly ObservableCollection<FileViewModel> _files = new();

    public AllPdfsPage()
    {
        InitializeComponent();
        PdfListView.ItemsSource = _files;
        _ = LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
    {
        _files.Clear();
        var all = await DatabaseManager.Instance.GetAllFilesAsync();
        foreach (var f in all)
            _files.Add(FileViewModel.From(f));
    }

    // ─── Open a new PDF via file picker ──────────────────────────────────────

    private async void BtnAddPdf_Click(object sender, RoutedEventArgs e)
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

        long fileId = await DatabaseManager.Instance.EnsureFileTrackedAsync(file.Path);
        await DatabaseManager.Instance.UpdateFileLastOpenedAsync(fileId);
        await LoadFilesAsync();

        // Navigate to viewer
        Frame.Navigate(typeof(ViewerPage), file.Path);
    }

    // ─── Click on a listed file → open in viewer ─────────────────────────────

    private async void PdfListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FileViewModel fvm) return;

        // Update recently-opened timestamp
        await DatabaseManager.Instance.UpdateFileLastOpenedAsync(fvm.FileId);
        Frame.Navigate(typeof(ViewerPage), fvm.FilePath);
    }

    // ─── Per-item "Add to Folder" ─────────────────────────────────────────────

    private async void AddToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long fileId) return;
        await ShowAddToFolderDialogAsync(fileId);
    }

    // ─── Add-to-Folder ContentDialog ─────────────────────────────────────────

    internal async Task ShowAddToFolderDialogAsync(long fileId)
    {
        var folders = await DatabaseManager.Instance.GetFoldersAsync();

        // Build a simple ComboBox for folder selection
        var combo = new ComboBox
        {
            PlaceholderText = "Select a folder…",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var f in folders)
            combo.Items.Add(new ComboBoxItem { Content = $"{f.Emoji}  {f.FolderName}", Tag = f.FolderId });

        var dialogContent = folders.Count > 0
            ? (UIElement)new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Choose a virtual folder for this PDF:" },
                    combo
                }
            }
            : new TextBlock { Text = "No folders yet. Create one in Virtual Folders." };

        var dialog = new ContentDialog
        {
            Title = "Add to Folder",
            Content = dialogContent,
            PrimaryButtonText = folders.Count > 0 ? "Add" : "",
            SecondaryButtonText = "Remove from Folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary
            && combo.SelectedItem is ComboBoxItem selected
            && selected.Tag is long fid)
        {
            await DatabaseManager.Instance.AssignFileToFolderAsync(fileId, fid);
            await LoadFilesAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await DatabaseManager.Instance.AssignFileToFolderAsync(fileId, null);
            await LoadFilesAsync();
        }
    }
}