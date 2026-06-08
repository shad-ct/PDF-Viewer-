using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyPdfViewer.Helpers;
using System;
using System.Collections.ObjectModel;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MyPdfViewer.Pages;

public sealed partial class AllPdfsPage : Page
{
    private readonly ObservableCollection<string> _filePaths = new();

    public AllPdfsPage()
    {
        InitializeComponent();
        PdfListView.ItemsSource = _filePaths;
        _ = LoadTrackedFilesAsync();
    }

    private async System.Threading.Tasks.Task LoadTrackedFilesAsync()
    {
        _filePaths.Clear();

        // Read all tracked file paths from the Files table
        string cs = GetConnectionString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FilePath FROM Files ORDER BY FileId DESC;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            _filePaths.Add(reader.GetString(0));
    }

    private static string GetConnectionString()
    {
        string dbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "mypdfviewer.db");
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

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

        await DatabaseManager.Instance.EnsureFileTrackedAsync(file.Path);
        await LoadTrackedFilesAsync();

        // Navigate to the viewer with the selected file
        Frame.Navigate(typeof(ViewerPage), file.Path);
    }

    private void PdfListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string path)
            Frame.Navigate(typeof(ViewerPage), path);
    }
}
