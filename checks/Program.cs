using BetterQuickSettings.Core;
using BetterQuickSettings.Services;
using BetterQuickSettings.UI;
using System.Windows.Threading;

var passed = 0;
void Check(string name, Action assertion)
{
    assertion();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Assert(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

var speaker = new AudioOutput("speaker", "Speakers", false, true);
var headphone = new AudioOutput("headphone", "任意の名前", true, false);
var hdmi = new AudioOutput("hdmi", "Headphone-looking HDMI name", false, false);
var previous = new[] { speaker };

Check("new headphones selected by hardware type, not language", () =>
    Assert(HeadphoneRouting.Choose(previous, [speaker, headphone], true, null) == headphone, "Expected headphones"));
Check("new monitor is not treated as headphones", () =>
    Assert(HeadphoneRouting.Choose(previous, [speaker, hdmi], true, null) is null, "HDMI must not steal sound"));
Check("disabled routing respects the user's choice", () =>
    Assert(HeadphoneRouting.Choose(previous, [speaker, headphone], false, null) is null, "Routing disabled"));
Check("already connected headphones do not override a manual selection", () =>
    Assert(HeadphoneRouting.Choose([speaker, headphone], [speaker, headphone], true, null) is null, "No new connection"));
Check("preferred endpoint supports devices misclassified by a driver", () =>
    Assert(HeadphoneRouting.Choose(previous, [speaker, hdmi], true, hdmi.Id) == hdmi, "Explicit preference wins"));
Check("other headphones do not override the preferred device", () =>
    Assert(HeadphoneRouting.Choose(previous, [speaker, headphone], true, "another") is null, "Respect preference"));
Check("a new endpoint already made default by Windows requires no write", () =>
    Assert(HeadphoneRouting.Choose(previous, [speaker, headphone with { IsDefault = true }], true, null) is null, "Avoid redundant writes"));
Check("unplugging headphones doesn't select an unrelated newly visible monitor", () =>
    Assert(HeadphoneRouting.Choose([speaker, headphone], [speaker, hdmi], true, null) is null, "Use Windows fallback"));

var directory = Path.Combine(Path.GetTempPath(), "betterquicksettings-check-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new LocalStore(directory);
    Check("first run has an empty notebook", () => Assert(store.LoadNote() == "", "Unexpected note"));
    var text = "Fikir: hızlı ayarlar\r\nTürkçe — 日本語 — 🌿\n" + new string('a', 10000);
    Check("notes round-trip Unicode and line endings across process-like reloads", () =>
    {
        store.SaveNote(text);
        Assert(new LocalStore(directory).LoadNote() == text, "Note changed on disk");
    });
    Check("a failed atomic replacement preserves the complete existing note", () =>
    {
        using var locked = new FileStream(store.NotePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failed = false;
        try { store.SaveNote("must not replace the locked note"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Assert(failed, "Expected a sharing violation");
        Assert(store.LoadNote() == text, "Previous content was lost");
        Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "Temporary write leaked");
    });
    Check("clearing a note persists deliberately", () =>
    {
        store.SaveNote("");
        Assert(new LocalStore(directory).LoadNote() == "", "Empty note didn't persist");
    });
    Check("preferences round-trip", () =>
    {
        var preferences = new Preferences { AutoHeadphones = false, ReplaceQuickSettings = false, PreferredHeadphonesId = "test-device", GlassAmount = 0, ToolsExpanded = true };
        store.SavePreferences(preferences);
        Assert(new LocalStore(directory).LoadPreferences() == preferences, "Preferences changed");
    });
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
async Task CheckAsync(string name, Func<Task> assertion)
{
    await assertion();
    Console.WriteLine("PASS " + name);
    passed++;
}

var shelfCheckDirectory = Path.Combine(Path.GetTempPath(), "betterquicksettings-shelf-check-" + Guid.NewGuid().ToString("N"));
try
{
    var originals = Path.Combine(shelfCheckDirectory, "originals");
    var localStore = new LocalStore(Path.Combine(shelfCheckDirectory, "app-data"));
    var shelf = new FileShelfStore(localStore);
    Directory.CreateDirectory(originals);
    await shelf.LoadAsync();

    await CheckAsync("staged files survive their original being moved", async () =>
    {
        var path = Path.Combine(originals, "brief.txt");
        await File.WriteAllTextAsync(path, "Original content");
        var entry = await shelf.AddPathAsync(path);
        File.Move(path, path + ".moved");
        Assert(await File.ReadAllTextAsync(shelf.GetPath(entry)) == "Original content", "The shelf stored a reference instead of a copy");
    });
    await CheckAsync("removing a shelf item preserves the source file", async () =>
    {
        var path = Path.Combine(originals, "keep-original.txt");
        await File.WriteAllTextAsync(path, "Do not delete");
        var entry = await shelf.AddPathAsync(path);
        await shelf.RemoveAsync(entry.Id);
        Assert(await File.ReadAllTextAsync(path) == "Do not delete", "Original file was modified");
        Assert(!File.Exists(shelf.GetPath(entry)), "Shelf copy was not removed");
    });
    await CheckAsync("same-named files coexist without overwriting", async () =>
    {
        var one = Path.Combine(originals, "one");
        var two = Path.Combine(originals, "two");
        Directory.CreateDirectory(one); Directory.CreateDirectory(two);
        await File.WriteAllTextAsync(Path.Combine(one, "notes.txt"), "First");
        await File.WriteAllTextAsync(Path.Combine(two, "notes.txt"), "Second");
        var a = await shelf.AddPathAsync(Path.Combine(one, "notes.txt"));
        var b = await shelf.AddPathAsync(Path.Combine(two, "notes.txt"));
        Assert(a.Id != b.Id && await File.ReadAllTextAsync(shelf.GetPath(a)) == "First" &&
            await File.ReadAllTextAsync(shelf.GetPath(b)) == "Second", "Same-named files collided");
    });
    await CheckAsync("folders are copied recursively and restored after restart", async () =>
    {
        var folder = Path.Combine(originals, "assets");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "nested", "data.txt"), "Folder content");
        var entry = await shelf.AddPathAsync(folder);
        Directory.Delete(folder, recursive: true);
        var restarted = new FileShelfStore(new LocalStore(localStore.DirectoryPath));
        var records = await restarted.LoadAsync();
        Assert(records.Any(item => item.Id == entry.Id), "Folder was lost from the persisted index");
        Assert(await File.ReadAllTextAsync(Path.Combine(restarted.GetPath(entry), "nested", "data.txt")) == "Folder content", "Folder copy is incomplete");
    });
    await CheckAsync("a failed copy never becomes a completed shelf item", async () =>
    {
        var path = Path.Combine(originals, "locked.txt");
        await File.WriteAllTextAsync(path, "Locked source");
        var count = (await shelf.LoadAsync()).Count;
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var failed = false;
        try { await shelf.AddPathAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Assert(failed && (await shelf.LoadAsync()).Count == count, "Failed copy polluted the index");
    });
    await CheckAsync("the shelf cannot recursively import itself", async () =>
    {
        var failed = false;
        try { await shelf.AddPathAsync(shelfCheckDirectory); }
        catch (IOException) { failed = true; }
        Assert(failed, "Recursive import was not rejected");
    });
    await CheckAsync("clipboard and saved-file copies deduplicate; kept captures survive retention", async () =>
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        using var first = new MemoryStream(png);
        var screenshot = await shelf.AddScreenshotAsync(first, "same-pixels");
        using var duplicate = new MemoryStream(png);
        Assert((await shelf.AddScreenshotAsync(duplicate, "same-pixels")).Id == screenshot.Id, "Duplicate screenshot was added");
        await shelf.KeepAsync(screenshot.Id);
        for (var i = 0; i <= FileShelfStore.RecentScreenshotLimit; i++)
        {
            using var image = new MemoryStream(png);
            await shelf.AddScreenshotAsync(image, "pixel-fixture-" + i);
        }
        var records = await shelf.LoadAsync();
        Assert(records.Count(item => item.Kind == ShelfItemKind.Screenshot) == FileShelfStore.RecentScreenshotLimit, "Recent capture retention failed");
        Assert(records.Any(item => item.Id == screenshot.Id && item.Kind == ShelfItemKind.File) &&
            File.Exists(shelf.GetPath(screenshot)), "A kept screenshot was evicted");
    });
    await CheckAsync("invalid index paths cannot escape the owned shelf", async () =>
    {
        var invalidStore = new LocalStore(Path.Combine(shelfCheckDirectory, "invalid-index"));
        invalidStore.SaveShelf([new ShelfEntry(Guid.NewGuid(), "..\\outside.txt", ShelfItemKind.File, DateTimeOffset.UtcNow, 0)]);
        var rejected = false;
        try { await new FileShelfStore(invalidStore).LoadAsync(); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Path traversal in an index was accepted");
    });
}
finally { if (Directory.Exists(shelfCheckDirectory)) Directory.Delete(shelfCheckDirectory, recursive: true); }

var notesDirectory = Path.Combine(Path.GetTempPath(), "betterquicksettings-notes-check-" + Guid.NewGuid().ToString("N"));
try
{
    Check("the existing scratchpad is preserved as one note, exactly once", () =>
    {
        var store = new LocalStore(Path.Combine(notesDirectory, "import"));
        store.SaveNote("Existing note\nKeep this content 🌿");
        var first = store.LoadNotes();
        var next = new LocalStore(store.DirectoryPath).LoadNotes();
        Assert(first.Entries.Count == 1 && next.Entries.Count == 1 && first.Entries[0].Id == next.Entries[0].Id,
            "Scratchpad import repeated or lost its identity");
        Assert(first.Entries[0].Content == store.LoadNote(), "The existing text was changed");
    });
    Check("notes support add, edit, list, draft recovery, and delete", () =>
    {
        var store = new LocalStore(Path.Combine(notesDirectory, "collection"));
        using (var model = new PanelViewModel(store, Dispatcher.CurrentDispatcher))
        {
            model.Note = "First idea\nDetails";
            Assert(model.AddNote(), "First note was not added");
            model.Note = "Second idea";
            Assert(model.AddNote() && model.Notes.Count == 2 && model.Note == "", "Composer did not reset after adding");
            var first = model.Notes.Single(note => note.Title == "First idea");
            model.Note = "Unfinished draft";
            Assert(model.EditNote(first), "Could not enter the editor");
            model.Note = "Edited idea\nNew details";
            Assert(model.SaveNote() && model.ShowNotesList() && model.Note == "Unfinished draft", "Draft was lost during editing");
            Assert(model.Notes.Any(note => note.Title == "Edited idea"), "Updated note did not appear in the list");
            Assert(model.DeleteNote(first.Id) && model.Notes.Count == 1, "Delete affected the wrong notes");
        }
        using var reopened = new PanelViewModel(new LocalStore(store.DirectoryPath), Dispatcher.CurrentDispatcher);
        Assert(reopened.Note == "Unfinished draft" && reopened.Notes.Single().Title == "Second idea", "Notes did not survive restart");
    });
    Check("a failed note edit keeps the saved collection and editable text", () =>
    {
        var store = new LocalStore(Path.Combine(notesDirectory, "locked"));
        using var model = new PanelViewModel(store, Dispatcher.CurrentDispatcher);
        model.Note = "Saved note"; model.AddNote(); model.EditNote(model.Notes[0]);
        var path = Path.Combine(store.DirectoryPath, "notes.json");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            model.Note = "Unsaved changes";
            Assert(!model.SaveNote() && model.Note == "Unsaved changes", "Failed save discarded the editor");
            Assert(new LocalStore(store.DirectoryPath).LoadNotes().Entries[0].Content == "Saved note", "Failed save corrupted the saved note");
        }
        Assert(model.SaveNote(), "The note could not be saved after the lock was released");
    });
}
finally { if (Directory.Exists(notesDirectory)) Directory.Delete(notesDirectory, recursive: true); }

var clipboardDirectory = Path.Combine(Path.GetTempPath(), "betterquicksettings-clipboard-check-" + Guid.NewGuid().ToString("N"));
try
{
    await CheckAsync("clipboard text preserves Unicode and line breaks, deduplicates, and survives restart", async () =>
    {
        var local = new LocalStore(Path.Combine(clipboardDirectory, "text"));
        var history = new ClipboardHistoryStore(local);
        var content = "First line\r\nTürkçe — 日本語 — 🌿\n\tLast line  ";
        var first = (await history.AddTextAsync(content))!;
        await history.AddTextAsync("A different copy");
        var duplicate = (await history.AddTextAsync(content))!;
        var restored = await new ClipboardHistoryStore(new LocalStore(local.DirectoryPath)).LoadAsync();
        Assert(duplicate.Id == first.Id && restored.Count == 2 && restored[0].Text == content && restored[0].Id == first.Id,
            "Clipboard round-trip or duplicate promotion changed the data");
    });
    await CheckAsync("clipboard count retention keeps the most recent fifty unique items", async () =>
    {
        var history = new ClipboardHistoryStore(new LocalStore(Path.Combine(clipboardDirectory, "count")));
        for (var i = 0; i < 57; i++) await history.AddTextAsync("Copied item " + i);
        var saved = await history.LoadAsync();
        Assert(saved.Count == 50 && saved[0].Text == "Copied item 56" && saved[^1].Text == "Copied item 7",
            "Old copies did not expire in chronological order");
        Assert(await history.AddTextAsync(new string('x', ClipboardHistoryStore.TextCharacterLimit + 1)) is null &&
            (await history.LoadAsync()).Count == 50, "An oversized text was retained or displaced a valid item");
    });
    await CheckAsync("clipboard image byte retention removes expired owned images without touching the source", async () =>
    {
        var local = new LocalStore(Path.Combine(clipboardDirectory, "bytes"));
        var history = new ClipboardHistoryStore(local);
        var payload = new byte[ClipboardHistoryStore.ImageByteLimit];
        payload[0] = 137; payload[1] = 80;
        var original = Path.Combine(clipboardDirectory, "original-image.bin");
        await File.WriteAllBytesAsync(original, payload);
        ClipboardEntry? first = null;
        for (var i = 0; i < 5; i++)
        {
            using var input = File.OpenRead(original);
            var entry = await history.AddImageAsync(input, "image-" + i, 2048, 1024);
            first ??= entry;
        }
        var saved = await history.LoadAsync();
        Assert(saved.Count == 4 && saved.Sum(item => item.Bytes) <= ClipboardHistoryStore.ByteLimit &&
            !File.Exists(history.ImagePath(first!.Id)), "Expired images remained indexed or on disk");
        Assert(File.Exists(original) && new FileInfo(original).Length == payload.Length, "An original image was changed");
    });
    await CheckAsync("a failed clipboard commit preserves the previous index and image copy", async () =>
    {
        var local = new LocalStore(Path.Combine(clipboardDirectory, "atomic"));
        var history = new ClipboardHistoryStore(local);
        using var image = new MemoryStream(new byte[] { 137, 80, 78, 71, 13, 10 });
        var first = (await history.AddImageAsync(image, "original-image", 1, 1))!;
        var failed = false;
        using (var locked = new FileStream(Path.Combine(local.DirectoryPath, "clipboard.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await history.ClearAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        }
        var restored = await new ClipboardHistoryStore(new LocalStore(local.DirectoryPath)).LoadAsync();
        Assert(failed && restored.Single().Id == first.Id && File.Exists(history.ImagePath(first.Id)),
            "A failed clear discarded durable history");
    });
    await CheckAsync("clipboard clear removes history while preserving notes, shelf files, and the original", async () =>
    {
        var local = new LocalStore(Path.Combine(clipboardDirectory, "clear"));
        local.SaveNote("Keep my note");
        var shelf = new FileShelfStore(local);
        await shelf.LoadAsync();
        var original = Path.Combine(clipboardDirectory, "original.txt");
        await File.WriteAllTextAsync(original, "Keep my file");
        var shelfEntry = await shelf.AddPathAsync(original);
        var history = new ClipboardHistoryStore(local);
        await history.AddTextAsync("A clipboard copy");
        using var image = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var imageEntry = (await history.AddImageAsync(image, "clear-image", 1, 1))!;
        await history.ClearAsync();
        Assert((await new ClipboardHistoryStore(local).LoadAsync()).Count == 0 && !File.Exists(history.ImagePath(imageEntry.Id)),
            "Cleared clipboard history reappeared after restart");
        Assert(local.LoadNote() == "Keep my note" && File.Exists(original) && File.Exists(shelf.GetPath(shelfEntry)),
            "Clearing history changed unrelated content");
    });
}
finally { if (Directory.Exists(clipboardDirectory)) Directory.Delete(clipboardDirectory, recursive: true); }
Console.WriteLine($"{passed} checks passed. No hardware settings were changed.");
