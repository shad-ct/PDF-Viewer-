using Microsoft.UI.Xaml;
using MyPdfViewer.Helpers;

namespace MyPdfViewer;

/// <summary>
/// Application entry point.
/// Initialises the SQLite database before presenting the main window.
/// </summary>
public partial class App : Application
{
    // Expose the main window handle so pages can initialise pickers with it
    internal Window? MainWindowHandle { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // ── 1. Initialise database (creates tables if not present) ────────────
        await DatabaseManager.Instance.InitializeAsync();

        // ── 2. Launch the shell window ────────────────────────────────────────
        MainWindowHandle = new MainWindow();
        MainWindowHandle.Activate();
    }
}
