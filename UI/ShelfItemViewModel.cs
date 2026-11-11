using System;
using System.IO;
using System.Windows.Media.Imaging;
using BetterQuickSettings.Core;

namespace BetterQuickSettings.UI;

public sealed class ShelfItemViewModel : Observable
{
    private BitmapSource? thumbnail;
    public ShelfEntry Entry { get; }
    public string FullPath { get; }
    public string Name => Entry.Name;
    public bool IsScreenshot => Entry.Kind == ShelfItemKind.Screenshot;
    public string Glyph => Entry.Kind == ShelfItemKind.Folder ? "\uE8B7" : "\uE8A5";
    public string Details => Entry.Kind == ShelfItemKind.Folder ? "Folder" :
        $"{Path.GetExtension(Name).TrimStart('.').ToUpperInvariant()} · {FormatSize(Entry.Bytes)}";
    public string TimeLabel => Entry.AddedAt.ToLocalTime().ToString("HH:mm");
    public BitmapSource? Thumbnail { get => thumbnail; set { Set(ref thumbnail, value); Changed(nameof(HasThumbnail)); } }
    public bool HasThumbnail => Thumbnail is not null;

    public ShelfItemViewModel(ShelfEntry entry, string path) { Entry = entry; FullPath = path; }

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / (1024d * 1024):0.#} MB";
}
