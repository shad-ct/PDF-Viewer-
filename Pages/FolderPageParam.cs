namespace MyPdfViewer.Pages;

/// <summary>
/// Navigation parameter passed to <see cref="FoldersPage"/> to control display mode.
/// </summary>
/// <param name="FolderId">When set, display files inside this specific folder.</param>
/// <param name="ShowRecent">When true, display recently-opened files.</param>
public record FolderPageParam(long? FolderId = null, bool ShowRecent = false);
