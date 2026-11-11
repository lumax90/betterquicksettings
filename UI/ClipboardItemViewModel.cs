using System;
using System.Windows.Media.Imaging;
using BetterQuickSettings.Core;

namespace BetterQuickSettings.UI;

public sealed class ClipboardItemViewModel : Observable
{
    private bool copied;
    public ClipboardEntry Entry { get; }
    public BitmapSource? Thumbnail { get; init; }
    public bool IsImage => Entry.Kind == ClipboardItemKind.Image;
    public bool IsText => !IsImage;
    public string Preview { get; }
    public string Details => IsImage ? $"Image · {Entry.PixelWidth} × {Entry.PixelHeight}" : $"Text · {Entry.Text!.Length:N0} characters";
    public string TimeLabel => copied ? "Copied" : Entry.CopiedAt.ToLocalTime().ToString(
        Entry.CopiedAt.LocalDateTime.Date == DateTime.Today ? "HH:mm" : "MMM d");
    public string CopyLabel => copied ? "Copied" : "Copy to clipboard";
    public bool Copied { get => copied; set { if (Set(ref copied, value)) { Changed(nameof(TimeLabel)); Changed(nameof(CopyLabel)); } } }

    public ClipboardItemViewModel(ClipboardEntry entry)
    {
        Entry = entry;
        var text = entry.Text ?? "Image";
        Preview = text.Length <= 300 ? text : text[..300] + "…";
    }
}
