using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyPdfViewer.Helpers;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MyPdfViewer.Pages;

// ─── Settings model ────────────────────────────────────────────────────────────

internal sealed class AppSettings
{
    public string Theme { get; set; } = "Default";
    public double DefaultZoom { get; set; } = 100;
    public string WatchedFolder { get; set; } = "";
    public bool IsListView { get; set; } = true;

    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            }
        }
        catch { /* ignore — use defaults */ }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this)); }
        catch { }
    }
}

// ─── Page ─────────────────────────────────────────────────────────────────────

public sealed partial class SettingsPage : Page
{
    private readonly AppSettings _settings;
    private bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        ApplySettings();
        _loaded = true;
    }

    // ─── Apply saved settings to UI ──────────────────────────────────────────

    private void ApplySettings()
    {
        // Theme combo
        foreach (ComboBoxItem item in ThemeCombo.Items)
        {
            if (item.Tag as string == _settings.Theme)
            {
                ThemeCombo.SelectedItem = item;
                break;
            }
        }

        // Zoom slider
        ZoomSlider.Value = Math.Clamp(_settings.DefaultZoom, 50, 200);
        ZoomValueLabel.Text = $"{(int)_settings.DefaultZoom}%";

        // Apply theme to current window immediately
        ApplyTheme(_settings.Theme);

        // Watched folder configuration
        if (!string.IsNullOrEmpty(_settings.WatchedFolder))
        {
            LibraryFolderDesc.Text = $"Active folder: {_settings.WatchedFolder}";
            BtnClearFolder.Visibility = Visibility.Visible;
        }
        else
        {
            LibraryFolderDesc.Text = "Scan and add all PDFs from a directory on your disk to 'All PDFs'.";
            BtnClearFolder.Visibility = Visibility.Collapsed;
        }
    }

    // ─── Theme ────────────────────────────────────────────────────────────────

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;

        string tag = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default";
        _settings.Theme = tag;
        _settings.Save();
        ApplyTheme(tag);
    }

    private static void ApplyTheme(string tag)
    {
        var theme = tag switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (MainWindow.Instance?.Content is FrameworkElement root)
            root.RequestedTheme = theme;
    }

    // ─── Zoom slider ──────────────────────────────────────────────────────────

    private void ZoomSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        int v = (int)e.NewValue;
        ZoomValueLabel.Text = $"{v}%";
        _settings.DefaultZoom = v;
        _settings.Save();
    }

    private void PenThicknessSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        int v = (int)e.NewValue;
        if (PenThicknessLabel is not null)
            PenThicknessLabel.Text = $"{v} px";
    }

    // ─── Clear recent history ─────────────────────────────────────────────────

    private async void BtnClearRecent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear Recently Opened",
            Content = "This will clear all LastOpenedAt timestamps. Files will remain tracked — only the recent history is erased.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await ClearRecentAsync();
    }

    private static async Task ClearRecentAsync()
    {
        string cs = DatabaseManager.Instance.ConnectionString;
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Files SET LastOpenedAt = NULL;";
        await cmd.ExecuteNonQueryAsync();
    }

    // ─── Library folder pick and scan ──────────────────────────────────────────

    private async void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            (Application.Current as App)!.MainWindowHandle);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        StorageFolder folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _settings.WatchedFolder = folder.Path;
        _settings.Save();

        ApplySettings();

        // Start import in background
        await ImportPdfsFromFolderAsync(folder.Path);
    }

    private void BtnClearFolder_Click(object sender, RoutedEventArgs e)
    {
        _settings.WatchedFolder = "";
        _settings.Save();
        ApplySettings();
    }

    private async Task ImportPdfsFromFolderAsync(string folderPath)
    {
        LibraryFolderDesc.Text = "Scanning folder for PDFs...";
        BtnSelectFolder.IsEnabled = false;
        BtnClearFolder.IsEnabled = false;

        try
        {
            var pdfFiles = await Task.Run(() =>
            {
                if (!Directory.Exists(folderPath)) return Array.Empty<string>();
                return Directory.GetFiles(folderPath, "*.pdf", SearchOption.AllDirectories);
            });

            LibraryFolderDesc.Text = $"Found {pdfFiles.Length} PDFs. Importing...";

            int imported = 0;
            foreach (var file in pdfFiles)
            {
                try
                {
                    await DatabaseManager.Instance.EnsureFileTrackedAsync(file);
                    imported++;
                }
                catch { }
            }

            LibraryFolderDesc.Text = $"Successfully imported {imported} PDFs.\nActive folder: {folderPath}";
        }
        catch (Exception ex)
        {
            LibraryFolderDesc.Text = $"Error scanning folder: {ex.Message}";
        }
        finally
        {
            BtnSelectFolder.IsEnabled = true;
            BtnClearFolder.IsEnabled = true;
            ApplySettings();
        }
    }
}
