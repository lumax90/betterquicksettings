using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterQuickSettings.Core;

namespace BetterQuickSettings.Services;

/// <summary>A bounded, local notebook of clipboard text and owned image copies.</summary>
public sealed class ClipboardHistoryStore
{
    public const int ItemLimit = 50;
    public const long ByteLimit = 32 * 1024 * 1024;
    public const int TextCharacterLimit = 200_000;
    public const int ImageByteLimit = 8 * 1024 * 1024;
    public const long ImagePixelLimit = 16_777_216;
    private readonly LocalStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<ClipboardEntry> entries = new();
    private bool loaded;
    public string ImageDirectory { get; }

    public ClipboardHistoryStore(LocalStore store)
    {
        this.store = store;
        ImageDirectory = Path.Combine(store.DirectoryPath, "clipboard-images");
    }

    public string ImagePath(Guid id) => Path.Combine(ImageDirectory, id.ToString("N") + ".png");

    public Task<IReadOnlyList<ClipboardEntry>> LoadAsync() => RunAsync<IReadOnlyList<ClipboardEntry>>(() =>
        entries.OrderByDescending(item => item.CopiedAt).ToArray());

    public Task<ClipboardEntry?> AddTextAsync(string text) => RunAsync<ClipboardEntry?>(() =>
    {
        if (text.Length == 0 || text.Length > TextCharacterLimit) return null;
        var bytes = Encoding.UTF8.GetBytes(text);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var existing = entries.FirstOrDefault(item => item.Kind == ClipboardItemKind.Text && item.Fingerprint == fingerprint);
        var entry = new ClipboardEntry(existing?.Id ?? Guid.NewGuid(), ClipboardItemKind.Text,
            DateTimeOffset.UtcNow, bytes.Length, fingerprint, text);
        CommitBounded(entries.Where(item => item.Id != entry.Id).Prepend(entry));
        return entry;
    });

    public Task<ClipboardEntry?> AddImageAsync(Stream png, string fingerprint, int width, int height) => RunAsync<ClipboardEntry?>(() =>
    {
        if (png.Length <= 0 || png.Length > ImageByteLimit || width <= 0 || height <= 0 ||
            (long)width * height > ImagePixelLimit) return null;
        var existing = entries.FirstOrDefault(item => item.Kind == ClipboardItemKind.Image && item.Fingerprint == fingerprint);
        if (existing is not null && File.Exists(ImagePath(existing.Id)))
        {
            var promoted = existing with { CopiedAt = DateTimeOffset.UtcNow };
            CommitBounded(entries.Where(item => item.Id != existing.Id).Prepend(promoted));
            return promoted;
        }
        var entry = new ClipboardEntry(Guid.NewGuid(), ClipboardItemKind.Image, DateTimeOffset.UtcNow,
            png.Length, fingerprint, PixelWidth: width, PixelHeight: height);
        try
        {
            Directory.CreateDirectory(ImageDirectory);
            using (var file = new FileStream(ImagePath(entry.Id), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                png.Position = 0;
                png.CopyTo(file);
                file.Flush(flushToDisk: true);
            }
            CommitBounded(entries.Where(item => item.Id != existing?.Id).Prepend(entry));
            return entry;
        }
        catch { DeleteImage(entry.Id); throw; }
    });

    public Task RemoveAsync(Guid id) => RunAsync(() =>
    {
        CommitBounded(entries.Where(item => item.Id != id));
        return true;
    });

    public Task ClearAsync() => RunAsync(() =>
    {
        CommitBounded(Array.Empty<ClipboardEntry>());
        return true;
    });

    public Task PromoteAsync(Guid id) => RunAsync(() =>
    {
        var current = entries.FirstOrDefault(item => item.Id == id);
        if (current is not null)
            CommitBounded(entries.Where(item => item.Id != id).Prepend(current with { CopiedAt = DateTimeOffset.UtcNow }));
        return true;
    });

    private Task<T> RunAsync<T>(Func<T> action) => Task.Run(async () =>
    {
        await gate.WaitAsync();
        try
        {
            if (!loaded)
            {
                var saved = store.LoadClipboardHistory();
                if (saved.Any(item => item is null || !Valid(item)) ||
                    saved.Select(item => item.Id).Distinct().Count() != saved.Count)
                    throw new InvalidDataException("The clipboard history is invalid. Existing data has been preserved.");
                entries = saved;
                loaded = true;
                if (saved.Count > ItemLimit || saved.Sum(item => item.Bytes) > ByteLimit)
                    CommitBounded(saved.OrderByDescending(item => item.CopiedAt));
            }
            return action();
        }
        finally { gate.Release(); }
    });

    private static bool Valid(ClipboardEntry item) => item.Id != Guid.Empty && Enum.IsDefined(item.Kind) &&
        !string.IsNullOrEmpty(item.Fingerprint) && item.Bytes > 0 && item.Bytes <= ByteLimit &&
        (item.Kind == ClipboardItemKind.Text
            ? item.Text is { Length: > 0 and <= TextCharacterLimit } && Encoding.UTF8.GetByteCount(item.Text) == item.Bytes
            : item.Text is null && item.Bytes <= ImageByteLimit && item.PixelWidth > 0 && item.PixelHeight > 0 &&
                (long)item.PixelWidth * item.PixelHeight <= ImagePixelLimit);

    private void CommitBounded(IEnumerable<ClipboardEntry> proposed)
    {
        var next = new List<ClipboardEntry>();
        long bytes = 0;
        foreach (var item in proposed)
        {
            if (next.Count == ItemLimit || bytes + item.Bytes > ByteLimit) break;
            next.Add(item);
            bytes += item.Bytes;
        }
        var retained = next.Select(item => item.Id).ToHashSet();
        var retired = entries.Where(item => item.Kind == ClipboardItemKind.Image && !retained.Contains(item.Id)).ToArray();
        // Publish the complete index first. A failed write never deletes the previous history.
        store.SaveClipboardHistory(next);
        entries = next;
        foreach (var item in retired) DeleteImage(item.Id);
    }

    private void DeleteImage(Guid id)
    {
        try { File.Delete(ImagePath(id)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
