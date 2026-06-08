using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyPdfViewer.Helpers;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace MyPdfViewer.Pages;

public sealed partial class FoldersPage : Page
{
    private record FolderItem(long Id, string Name);
    private readonly ObservableCollection<FolderItem> _folders = new();

    public FoldersPage()
    {
        InitializeComponent();
        FolderListView.ItemsSource = _folders;
        _ = LoadFoldersAsync();
    }

    private async Task LoadFoldersAsync()
    {
        _folders.Clear();
        string cs = GetConnectionString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FolderId, FolderName FROM Folders ORDER BY FolderName;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            _folders.Add(new FolderItem(reader.GetInt64(0), reader.GetString(1)));
    }

    private async void BtnCreateFolder_Click(object sender, RoutedEventArgs e)
    {
        string name = FolderNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        FolderNameBox.Text = string.Empty;
        await DatabaseManager.Instance.CreateFolderAsync(name);
        await LoadFoldersAsync();
    }

    private async void BtnDeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long id)
        {
            await DatabaseManager.Instance.DeleteFolderAsync(id);
            await LoadFoldersAsync();
        }
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
}
