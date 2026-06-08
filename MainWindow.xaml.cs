using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyPdfViewer.Pages;

namespace MyPdfViewer;

/// <summary>
/// Shell window. Owns the TitleBar + NavigationView and routes page navigation.
/// PDF-specific state lives entirely in ViewerPage.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // ── Extend content into the custom title bar ──────────────────────────
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate to the default landing page
        NavFrame.Navigate(typeof(AllPdfsPage));
    }

    // ─── TitleBar handlers ────────────────────────────────────────────────────

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
            NavFrame.GoBack();
    }

    // ─── NavigationView selection ─────────────────────────────────────────────

    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag as string)
            {
                case "allpdfs":
                    NavFrame.Navigate(typeof(AllPdfsPage));
                    break;
                case "folders":
                    NavFrame.Navigate(typeof(FoldersPage));
                    break;
                case "bookmarks":
                    NavFrame.Navigate(typeof(BookmarksPage));
                    break;
                case "settings":
                    NavFrame.Navigate(typeof(SettingsPage));
                    break;
            }
        }
    }
}
