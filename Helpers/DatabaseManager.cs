using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MyPdfViewer.Helpers;

// ─── Domain records ───────────────────────────────────────────────────────────

public record FolderRecord(long FolderId, string FolderName, string Emoji);
public record FileRecord(long FileId, string FilePath, string FileName, long? FolderId, DateTime? LastOpenedAt);

// ─── DatabaseManager ──────────────────────────────────────────────────────────

/// <summary>
/// Singleton SQLite database manager. Call InitializeAsync() once at app startup.
/// Handles schema creation, migration, and all CRUD for the 4 core tables.
/// </summary>
public sealed class DatabaseManager
{
    // ─── Singleton ────────────────────────────────────────────────────────────
    private static readonly Lazy<DatabaseManager> _instance =
        new(() => new DatabaseManager());

    public static DatabaseManager Instance => _instance.Value;

    // ─── Connection ───────────────────────────────────────────────────────────
    private readonly string _connectionString;

    private DatabaseManager()
    {
        string dbPath = Path.Combine(AppContext.BaseDirectory, "mypdfviewer.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    // ─── Initialisation ───────────────────────────────────────────────────────

    /// <summary>Creates tables and runs column migrations. Safe to call on every startup.</summary>
    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await ExecuteNonQueryAsync(conn, "PRAGMA journal_mode=WAL;");
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys=ON;");

        // ── Folders ──────────────────────────────────────────────────────────
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS Folders (
                FolderId   INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderName TEXT    NOT NULL,
                FolderEmoji TEXT   NOT NULL DEFAULT '📁'
            );
            """);
        // migration for older DBs without FolderEmoji
        await EnsureColumnExistsAsync(conn, "Folders", "FolderEmoji", "TEXT NOT NULL DEFAULT '📁'");

        // ── Files ─────────────────────────────────────────────────────────────
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS Files (
                FileId       INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath     TEXT    UNIQUE NOT NULL,
                FolderId     INTEGER REFERENCES Folders(FolderId) ON DELETE SET NULL,
                LastOpenedAt TEXT
            );
            """);
        await EnsureColumnExistsAsync(conn, "Files", "LastOpenedAt", "TEXT");
        await EnsureColumnExistsAsync(conn, "Files", "FolderId",
            "INTEGER REFERENCES Folders(FolderId) ON DELETE SET NULL");

        // ── Bookmarks ─────────────────────────────────────────────────────────
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS Bookmarks (
                BookmarkId    INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId        INTEGER NOT NULL REFERENCES Files(FileId) ON DELETE CASCADE,
                PageNumber    INTEGER NOT NULL,
                ScrollYOffset REAL    NOT NULL DEFAULT 0.0
            );
            """);

        // ── Annotations ───────────────────────────────────────────────────────
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS Annotations (
                AnnotationId   INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId         INTEGER NOT NULL REFERENCES Files(FileId) ON DELETE CASCADE,
                PageNumber     INTEGER,
                AnnotationType TEXT,
                CoordinateX    REAL,
                CoordinateY    REAL,
                Width          REAL,
                Height         REAL,
                TextContent    TEXT
            );
            """);
    }

    // ─── Schema migration helper ───────────────────────────────────────────────

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection conn, string table, string column, string definition)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        bool found = false;
        await using (var reader = await check.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
        }
        if (!found)
            await ExecuteNonQueryAsync(conn,
                $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    // ─── Files ────────────────────────────────────────────────────────────────

    /// <summary>Idempotently registers a file path and returns its FileId.</summary>
    public async Task<long> EnsureFileTrackedAsync(string filePath)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys=ON;");

        await ExecuteNonQueryAsync(conn,
            "INSERT OR IGNORE INTO Files (FilePath) VALUES ($path);",
            ("$path", filePath));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileId FROM Files WHERE FilePath = $path;";
        cmd.Parameters.AddWithValue("$path", filePath);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task UpdateFileLastOpenedAsync(long fileId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn,
            "UPDATE Files SET LastOpenedAt = $ts WHERE FileId = $id;",
            ("$ts", DateTime.UtcNow.ToString("o")),
            ("$id", fileId));
    }

    public async Task<List<FileRecord>> GetAllFilesAsync()
    {
        var list = new List<FileRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileId, FilePath, FolderId, LastOpenedAt FROM Files ORDER BY FileId DESC;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(ToFileRecord(r));
        return list;
    }

    public async Task<List<FileRecord>> GetRecentFilesAsync(int limit = 20)
    {
        var list = new List<FileRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FileId, FilePath, FolderId, LastOpenedAt
            FROM Files
            WHERE LastOpenedAt IS NOT NULL
            ORDER BY LastOpenedAt DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(ToFileRecord(r));
        return list;
    }

    public async Task<List<FileRecord>> GetFilesInFolderAsync(long folderId)
    {
        var list = new List<FileRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FileId, FilePath, FolderId, LastOpenedAt
            FROM Files
            WHERE FolderId = $fid
            ORDER BY LastOpenedAt DESC;
            """;
        cmd.Parameters.AddWithValue("$fid", folderId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(ToFileRecord(r));
        return list;
    }

    public async Task AssignFileToFolderAsync(long fileId, long? folderId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys=ON;");
        await ExecuteNonQueryAsync(conn,
            "UPDATE Files SET FolderId = $fid WHERE FileId = $id;",
            ("$fid", (object?)folderId ?? DBNull.Value),
            ("$id", fileId));
    }

    private static FileRecord ToFileRecord(SqliteDataReader r)
    {
        long fileId = r.GetInt64(0);
        string path = r.GetString(1);
        long? folderId = r.IsDBNull(2) ? null : r.GetInt64(2);
        DateTime? opened = r.IsDBNull(3) ? null
            : DateTime.TryParse(r.GetString(3), out var dt) ? dt : null;
        return new FileRecord(fileId, path, Path.GetFileName(path), folderId, opened);
    }

    // ─── Folders ─────────────────────────────────────────────────────────────

    public async Task<long> CreateFolderAsync(string folderName, string emoji = "📁")
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn,
            "INSERT INTO Folders (FolderName, FolderEmoji) VALUES ($name, $emoji);",
            ("$name", folderName),
            ("$emoji", string.IsNullOrWhiteSpace(emoji) ? "📁" : emoji));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<FolderRecord>> GetFoldersAsync()
    {
        var list = new List<FolderRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FolderId, FolderName, FolderEmoji FROM Folders ORDER BY FolderName;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new FolderRecord(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public async Task DeleteFolderAsync(long folderId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys=ON;");
        await ExecuteNonQueryAsync(conn,
            "DELETE FROM Folders WHERE FolderId = $id;", ("$id", folderId));
    }

    public async Task RenameFolderAsync(long folderId, string newName, string newEmoji)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn,
            "UPDATE Folders SET FolderName = $name, FolderEmoji = $emoji WHERE FolderId = $id;",
            ("$name", newName),
            ("$emoji", string.IsNullOrWhiteSpace(newEmoji) ? "📁" : newEmoji),
            ("$id", folderId));
    }

    // ─── Bookmarks ────────────────────────────────────────────────────────────

    public async Task AddBookmarkAsync(long fileId, int pageNumber, double scrollYOffset)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys=ON;");
        await ExecuteNonQueryAsync(conn,
            "INSERT INTO Bookmarks (FileId, PageNumber, ScrollYOffset) VALUES ($fid, $page, $off);",
            ("$fid", fileId), ("$page", pageNumber), ("$off", scrollYOffset));
    }

    public async Task DeleteBookmarkAsync(long bookmarkId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn,
            "DELETE FROM Bookmarks WHERE BookmarkId = $id;", ("$id", bookmarkId));
    }

    // ─── Annotations ─────────────────────────────────────────────────────────

    public async Task<long> SaveAnnotationAsync(
        long fileId, int pageNumber, string annotationType,
        double x, double y, double width, double height,
        string? textContent = null)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys=ON;");
        await ExecuteNonQueryAsync(conn, """
            INSERT INTO Annotations
                (FileId, PageNumber, AnnotationType, CoordinateX, CoordinateY, Width, Height, TextContent)
            VALUES ($fid, $page, $type, $x, $y, $w, $h, $text);
            """,
            ("$fid", fileId), ("$page", pageNumber), ("$type", annotationType),
            ("$x", x), ("$y", y), ("$w", width), ("$h", height),
            ("$text", (object?)textContent ?? DBNull.Value));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // ─── Shared helper ────────────────────────────────────────────────────────

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection conn, string sql,
        params (string name, object? value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
