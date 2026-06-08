using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyPdfViewer.Helpers;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace MyPdfViewer.Pages;

public sealed partial class BookmarksPage : Page
{
    private record BookmarkItem(
        long BookmarkId,
        string FilePath,
        string FileName,
        int PageNumber,
        double ScrollYOffset);

    private readonly ObservableCollection<BookmarkItem> _bookmarks = new();

    public BookmarksPage()
    {
        InitializeComponent();
        BookmarkListView.ItemsSource = _bookmarks;
        _ = LoadBookmarksAsync();
    }

    private async Task LoadBookmarksAsync()
    {
        _bookmarks.Clear();
        string cs = GetConnectionString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.BookmarkId, f.FilePath, b.PageNumber, b.ScrollYOffset
            FROM Bookmarks b
            JOIN Files f ON b.FileId = f.FileId
            ORDER BY b.BookmarkId DESC;
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            long id = reader.GetInt64(0);
            string path = reader.GetString(1);
            int page = reader.GetInt32(2);
            double offset = reader.GetDouble(3);
            _bookmarks.Add(new BookmarkItem(id, path, Path.GetFileName(path), page, offset));
        }
    }

    private void BookmarkListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkItem bm)
            Frame.Navigate(typeof(ViewerPage), bm.FilePath);
    }

    private async void BtnRemoveBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long id)
        {
            await DatabaseManager.Instance.DeleteBookmarkAsync(id);
            await LoadBookmarksAsync();
        }
    }

    private static string GetConnectionString()
    {
        string dbPath = Path.Combine(AppContext.BaseDirectory, "mypdfviewer.db");
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }
}
