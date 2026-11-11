using System;

namespace BetterQuickSettings.Core;

public enum ClipboardItemKind { Text, Image }

public sealed record ClipboardEntry(Guid Id, ClipboardItemKind Kind, DateTimeOffset CopiedAt,
    long Bytes, string Fingerprint, string? Text = null, int PixelWidth = 0, int PixelHeight = 0);
