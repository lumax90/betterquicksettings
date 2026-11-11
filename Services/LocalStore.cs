using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BetterQuickSettings.Core;

namespace BetterQuickSettings.Services;

/// <summary>Local-only storage. Atomic replacement keeps the last complete note on interrupted writes.</summary>
public sealed class LocalStore
{
    public string DirectoryPath { get; }
    public string NotePath => Path.Combine(DirectoryPath, "quick-note.txt");
    public static string DefaultDirectoryPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var current = Path.Combine(local, "BetterQuickSettings");
            var legacy = Path.Combine(local, "Ada");
            // Reuse existing preview data in place, including owned shelf and clipboard images.
            return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
        }
    }

    public LocalStore(string? directory = null)
    {
        DirectoryPath = directory ?? DefaultDirectoryPath;
    }

    public Preferences LoadPreferences()
    {
        var path = Path.Combine(DirectoryPath, "preferences.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new();
    }

    public string LoadNote() => File.Exists(NotePath) ? File.ReadAllText(NotePath) : "";
    public void SaveNote(string note) => WriteAtomic(NotePath, note);
    public NotesDocument LoadNotes()
    {
        var path = Path.Combine(DirectoryPath, "notes.json");
        if (File.Exists(path))
        {
            var document = JsonSerializer.Deserialize<NotesDocument>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The notes file is invalid.");
            ValidateNotes(document);
            return document;
        }
        // Carry the existing scratchpad forward once. Its original file is kept intact.
        var previous = LoadNote();
        if (string.IsNullOrWhiteSpace(previous)) return new();
        var updated = new DateTimeOffset(File.GetLastWriteTimeUtc(NotePath));
        var imported = new NotesDocument { Entries = [new QuickNote(Guid.NewGuid(), previous, updated, updated)] };
        SaveNotes(imported);
        return imported;
    }
    public void SaveNotes(NotesDocument notes)
    {
        ValidateNotes(notes);
        WriteAtomic(Path.Combine(DirectoryPath, "notes.json"), JsonSerializer.Serialize(notes));
    }
    private static void ValidateNotes(NotesDocument notes)
    {
        if (notes.Draft is null || notes.Entries is null || notes.Entries.Any(note => note is null || note.Content is null) ||
            notes.Entries.Select(note => note.Id).Distinct().Count() != notes.Entries.Count)
            throw new InvalidDataException("The notes file is invalid. Existing data has been preserved.");
    }
    public void SavePreferences(Preferences preferences) => WriteAtomic(
        Path.Combine(DirectoryPath, "preferences.json"),
        JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));

    public List<ShelfEntry> LoadShelf()
    {
        var path = Path.Combine(DirectoryPath, "shelf.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<ShelfEntry>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The file shelf index is invalid.")
            : new();
    }

    public void SaveShelf(IReadOnlyList<ShelfEntry> entries) => WriteAtomic(
        Path.Combine(DirectoryPath, "shelf.json"), JsonSerializer.Serialize(entries));

    public List<ClipboardEntry> LoadClipboardHistory()
    {
        var path = Path.Combine(DirectoryPath, "clipboard.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("The clipboard history is invalid.")
            : new();
    }

    public void SaveClipboardHistory(IReadOnlyList<ClipboardEntry> entries) => WriteAtomic(
        Path.Combine(DirectoryPath, "clipboard.json"), JsonSerializer.Serialize(entries));

    private void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
