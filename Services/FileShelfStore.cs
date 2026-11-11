using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterQuickSettings.Core;

namespace BetterQuickSettings.Services;

/// <summary>Owns staged copies. The index is committed only after the complete copy exists.</summary>
public sealed class FileShelfStore
{
    private readonly LocalStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<ShelfEntry> entries = new();
    private bool loaded;
    public string Root { get; }
    public const int RecentScreenshotLimit = 24;

    public FileShelfStore(LocalStore store)
    {
        this.store = store;
        Root = Path.GetFullPath(Path.Combine(store.DirectoryPath, "shelf"));
    }

    public async Task<IReadOnlyList<ShelfEntry>> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!loaded)
            {
                var saved = await Task.Run(store.LoadShelf);
                if (saved.Any(item => item is null || !ValidName(item.Name) || !Enum.IsDefined(item.Kind)) ||
                    saved.Select(item => item.Id).Distinct().Count() != saved.Count)
                    throw new InvalidDataException("The file shelf index is invalid. Existing copies have been preserved.");
                entries = saved;
                loaded = true;
            }
            return entries.OrderByDescending(item => item.AddedAt).ToArray();
        }
        finally { gate.Release(); }
    }

    public string GetPath(ShelfEntry entry)
    {
        if (!ValidName(entry.Name)) throw new InvalidDataException("Invalid shelf filename.");
        return Path.Combine(Bucket(entry.Id), entry.Name);
    }

    public bool OwnsPath(string path) => Inside(Path.GetFullPath(path), Root);

    public Task<ShelfEntry> AddPathAsync(string source) => Task.Run(async () =>
    {
        await gate.WaitAsync();
        try { EnsureLoaded(); }
        finally { gate.Release(); }
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        if (Inside(source, Root) || (Directory.Exists(source) && Inside(Root, source)))
            throw new IOException("The shelf cannot be placed inside itself.");
        var folder = Directory.Exists(source);
        var name = Path.GetFileName(source);
        if (!ValidName(name)) throw new IOException("Choose a file or folder, not a drive root.");
        var entry = new ShelfEntry(Guid.NewGuid(), name, folder ? ShelfItemKind.Folder : ShelfItemKind.File,
            DateTimeOffset.UtcNow, 0);
        try
        {
            // A large dropped folder must not block screenshot ingestion or shelf reads.
            Directory.CreateDirectory(Bucket(entry.Id));
            var bytes = await CopyAsync(source, GetPath(entry), folder);
            entry = entry with { Bytes = bytes };
            await gate.WaitAsync();
            try { Commit(entries.Append(entry).ToList()); }
            finally { gate.Release(); }
            return entry;
        }
        catch { DeleteBucket(entry.Id); throw; }
    });

    public Task<ShelfEntry> AddScreenshotAsync(Stream png, string fingerprint) => Task.Run(async () =>
    {
        await gate.WaitAsync();
        try
        {
            EnsureLoaded();
            var existing = entries.FirstOrDefault(item => item.Fingerprint == fingerprint);
            if (existing is not null && File.Exists(GetPath(existing))) return existing;
            var entry = new ShelfEntry(Guid.NewGuid(), $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png",
                ShelfItemKind.Screenshot, DateTimeOffset.UtcNow, png.Length, fingerprint);
            try
            {
                Directory.CreateDirectory(Bucket(entry.Id));
                using (var output = new FileStream(GetPath(entry), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous))
                {
                    png.Position = 0;
                    await png.CopyToAsync(output);
                    output.Flush(flushToDisk: true);
                }
                var updated = entries.Where(item => item.Id != existing?.Id).Append(entry).ToList();
                var expired = updated.Where(item => item.Kind == ShelfItemKind.Screenshot)
                    .OrderByDescending(item => item.AddedAt).Skip(RecentScreenshotLimit).ToArray();
                var retired = expired.Select(item => item.Id).ToHashSet();
                Commit(updated.Where(item => !retired.Contains(item.Id)).ToList());
                foreach (var old in expired) DeleteBucket(old.Id);
                return entry;
            }
            catch { DeleteBucket(entry.Id); throw; }
        }
        finally { gate.Release(); }
    });

    public Task KeepAsync(Guid id) => MutateAsync(() =>
        Commit(entries.Select(item => item.Id == id && item.Kind == ShelfItemKind.Screenshot
            ? item with { Kind = ShelfItemKind.File } : item).ToList()));

    public Task RemoveAsync(Guid id) => MutateAsync(() =>
    {
        if (!entries.Any(item => item.Id == id)) return;
        Commit(entries.Where(item => item.Id != id).ToList());
        DeleteBucket(id);
    });

    private Task MutateAsync(Action action) => Task.Run(async () =>
    {
        await gate.WaitAsync();
        try { EnsureLoaded(); action(); }
        finally { gate.Release(); }
    });

    private void Commit(List<ShelfEntry> updated)
    {
        store.SaveShelf(updated);
        entries = updated;
    }

    private async Task<long> CopyAsync(string source, string destination, bool folder)
    {
        if (!folder)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await input.CopyToAsync(output);
            output.Flush(flushToDisk: true);
            return output.Length;
        }
        var info = new DirectoryInfo(source);
        if (info.LinkTarget is not null)
            throw new IOException("Drop the original folder instead of a linked folder.");
        Directory.CreateDirectory(destination);
        long size = 0;
        foreach (var child in info.EnumerateFileSystemInfos())
            size += await CopyAsync(child.FullName, Path.Combine(destination, child.Name), child is DirectoryInfo);
        return size;
    }

    private void EnsureLoaded()
    {
        if (!loaded) throw new InvalidOperationException("The file shelf is not ready.");
    }

    private string Bucket(Guid id) => Path.Combine(Root, id.ToString("N"));
    private void DeleteBucket(Guid id)
    {
        // Only application-owned, GUID-addressed copies are ever removed.
        try { if (Directory.Exists(Bucket(id))) Directory.Delete(Bucket(id), recursive: true); }
        catch (IOException) { /* A receiving app may still hold a read handle. Keep that copy. */ }
        catch (UnauthorizedAccessException) { }
    }
    private static bool Inside(string path, string parent) =>
        path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." &&
        name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
