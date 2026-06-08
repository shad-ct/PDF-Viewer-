using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyPdfViewer.Helpers;
using MyPdfViewer.Pages;
using System;
using System.Threading.Tasks;

namespace MyPdfViewer;

public sealed partial class MainWindow : Window
{
    // ─── Singleton access so pages can trigger a nav refresh ─────────────────
    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Load folder sub-items and navigate to the default page
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        await RefreshFolderNavItemsAsync();
        NavFrame.Navigate(typeof(AllPdfsPage));
    }

    // ─── Public API: called by FoldersPage after folder create/delete ─────────

    /// <summary>Rebuilds the Virtual Folders sub-items in the nav pane from the DB.</summary>
    public async Task RefreshFolderNavItemsAsync()
    {
        // Keep only the static "Recently Opened" child + dynamic folder children
        NavItemFolders.MenuItems.Clear();

        // Static "Recently Opened" pinned at the top
        NavItemFolders.MenuItems.Add(new NavigationViewItem
        {
            Content = "🕐  Recently Opened",
            Tag = "recent",
            Icon = new FontIcon { Glyph = "\uE823" }
        });

        // Dynamic folder items
        var folders = await DatabaseManager.Instance.GetFoldersAsync();
        foreach (var f in folders)
        {
            NavItemFolders.MenuItems.Add(new NavigationViewItem
            {
                Content = $"{f.Emoji}  {f.FolderName}",
                Tag = $"folder:{f.FolderId}",
                Icon = new FontIcon { Glyph = "\uE8B7" }
            });
        }
    }

    // ─── TitleBar handlers ────────────────────────────────────────────────────

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack) NavFrame.GoBack();
    }

    // ─── Navigation routing ───────────────────────────────────────────────────

    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        string tag = item.Tag as string ?? string.Empty;

        switch (tag)
        {
            case "allpdfs":
                NavFrame.Navigate(typeof(AllPdfsPage));
                break;

            case "folders":
                NavFrame.Navigate(typeof(FoldersPage));
                break;

            case "recent":
                NavFrame.Navigate(typeof(FoldersPage), new FolderPageParam(ShowRecent: true));
                break;

            case "bookmarks":
                NavFrame.Navigate(typeof(BookmarksPage));
                break;

            case "settings":
                NavFrame.Navigate(typeof(SettingsPage));
                break;

            case string t when t.StartsWith("folder:"):
                if (long.TryParse(t[7..], out long folderId))
                    NavFrame.Navigate(typeof(FoldersPage), new FolderPageParam(FolderId: folderId));
                break;
        }
    }
}
