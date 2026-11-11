using System;

namespace BetterQuickSettings.Core;

public enum ShelfItemKind { File, Folder, Screenshot }

public sealed record ShelfEntry(Guid Id, string Name, ShelfItemKind Kind,
    DateTimeOffset AddedAt, long Bytes, string? Fingerprint = null);
