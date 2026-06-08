using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using MyPdfViewer.Helpers;

namespace MyPdfViewer.Pages;

public sealed partial class ViewerPage : Page
{
    private long _currentFileId;

    public ViewerPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string filePath && !string.IsNullOrEmpty(filePath))
            await LoadPdfAsync(filePath);
    }

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

    public async Task LoadPdfAsync(string filePath)
    {
        EmptyStatePlaceholder.Visibility = Visibility.Collapsed;

        _currentFileId = await DatabaseManager.Instance.EnsureFileTrackedAsync(filePath);
        await DatabaseManager.Instance.UpdateFileLastOpenedAsync(_currentFileId);

        BtnAddToFolder.IsEnabled = true;
        BtnBookmark.IsEnabled = true;

        await PdfWebView.EnsureCoreWebView2Async();
        PdfWebView.CoreWebView2.Navigate(new Uri(filePath).AbsoluteUri);
    }

    private async void BtnAddToFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFileId <= 0) return;

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

    private async void BtnBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFileId <= 0) return;
        
        await DatabaseManager.Instance.AddBookmarkAsync(_currentFileId, 1, 0);
    }
}
