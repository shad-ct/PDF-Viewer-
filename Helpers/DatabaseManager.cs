using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MyPdfViewer.Helpers;

/// <summary>
/// Singleton database manager. Call InitializeAsync() once at app startup.
/// Provides async CRUD operations for Folders, Files, Bookmarks, and Annotations.
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
        // Store the DB next to the executable so the unpackaged app can always write to it.
        string dbPath = Path.Combine(
            AppContext.BaseDirectory,
            "mypdfviewer.db");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    // ─── Public entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Creates all tables if they do not already exist. Must be called once before
    /// any other DB operations (typically in App.OnLaunched).
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // Enable WAL mode for better concurrent read performance
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;");
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;");

        // ── Folders ──────────────────────────────────────────────────────────
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS Folders (
                FolderId   INTEGER PRIMARY KEY AUTOINCREMENT,
                FolderName TEXT    NOT NULL
            );
            """);

        // ── Files ─────────────────────────────────────────────────────────────
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS Files (
                FileId   INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath TEXT    UNIQUE NOT NULL,
                FolderId INTEGER REFERENCES Folders(FolderId) ON DELETE SET NULL
            );
            """);

        // ── Bookmarks ─────────────────────────────────────────────────────────
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS Bookmarks (
                BookmarkId    INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId        INTEGER NOT NULL REFERENCES Files(FileId) ON DELETE CASCADE,
                PageNumber    INTEGER NOT NULL,
                ScrollYOffset REAL    NOT NULL DEFAULT 0.0
            );
            """);

        // ── Annotations ───────────────────────────────────────────────────────
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS Annotations (
                AnnotationId   INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId         INTEGER NOT NULL REFERENCES Files(FileId) ON DELETE CASCADE,
                PageNumber     INTEGER,
                AnnotationType TEXT,        -- 'COMMENT' | 'HIGHLIGHT' | 'RECTANGLE' | 'FREEHAND'
                CoordinateX    REAL,
                CoordinateY    REAL,
                Width          REAL,
                Height         REAL,
                TextContent    TEXT         -- comment body or highlighted excerpt
            );
            """);
    }

    // ─── Files ────────────────────────────────────────────────────────────────

    /// <summary>Registers a PDF file in the tracked Files table (idempotent).</summary>
    public async Task<long> EnsureFileTrackedAsync(string filePath)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;");

        // Upsert — INSERT OR IGNORE preserves existing FileId
        await ExecuteNonQueryAsync(connection, """
            INSERT OR IGNORE INTO Files (FilePath) VALUES ($path);
            """,
            ("$path", filePath));

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FileId FROM Files WHERE FilePath = $path;";
        cmd.Parameters.AddWithValue("$path", filePath);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result!);
    }

    // ─── Bookmarks ────────────────────────────────────────────────────────────

    public async Task AddBookmarkAsync(long fileId, int pageNumber, double scrollYOffset)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;");
        await ExecuteNonQueryAsync(connection, """
            INSERT INTO Bookmarks (FileId, PageNumber, ScrollYOffset)
            VALUES ($fileId, $page, $offset);
            """,
            ("$fileId", fileId),
            ("$page", pageNumber),
            ("$offset", scrollYOffset));
    }

    public async Task DeleteBookmarkAsync(long bookmarkId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection,
            "DELETE FROM Bookmarks WHERE BookmarkId = $id;",
            ("$id", bookmarkId));
    }

    // ─── Annotations ─────────────────────────────────────────────────────────

    public async Task<long> SaveAnnotationAsync(
        long fileId,
        int pageNumber,
        string annotationType,
        double x, double y,
        double width, double height,
        string? textContent = null)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;");

        await ExecuteNonQueryAsync(connection, """
            INSERT INTO Annotations
                (FileId, PageNumber, AnnotationType, CoordinateX, CoordinateY, Width, Height, TextContent)
            VALUES
                ($fileId, $page, $type, $x, $y, $w, $h, $text);
            """,
            ("$fileId", fileId),
            ("$page", pageNumber),
            ("$type", annotationType),
            ("$x", x),
            ("$y", y),
            ("$w", width),
            ("$h", height),
            ("$text", (object?)textContent ?? DBNull.Value));

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid();";
        var id = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(id!);
    }

    public async Task DeleteAnnotationAsync(long annotationId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection,
            "DELETE FROM Annotations WHERE AnnotationId = $id;",
            ("$id", annotationId));
    }

    // ─── Folders ─────────────────────────────────────────────────────────────

    public async Task<long> CreateFolderAsync(string folderName)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO Folders (FolderName) VALUES ($name);",
            ("$name", folderName));

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid();";
        var id = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(id!);
    }

    public async Task DeleteFolderAsync(long folderId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;");
        await ExecuteNonQueryAsync(connection,
            "DELETE FROM Folders WHERE FolderId = $id;",
            ("$id", folderId));
    }

    // ─── Internal helpers ─────────────────────────────────────────────────────

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        params (string name, object? value)[] parameters)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
