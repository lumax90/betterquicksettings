using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace BetterQuickSettings.Core;

public sealed record QuickNote(Guid Id, string Content, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    [JsonIgnore] public string Title => Lines.FirstOrDefault() is { } title
        ? (title.Length > 70 ? title[..70] + "…" : title) : "Untitled note";
    [JsonIgnore] public string Excerpt
    {
        get
        {
            var text = string.Join(" ", Lines.Skip(1));
            return text.Length > 130 ? text[..130] + "…" : text;
        }
    }
    [JsonIgnore] public string ModifiedLabel => UpdatedAt.LocalDateTime.Date == DateTime.Today
        ? UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
        : UpdatedAt.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    private IEnumerable<string> Lines => Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Where(line => line.Length > 0);
}

public sealed record NotesDocument
{
    public string Draft { get; init; } = "";
    public List<QuickNote> Entries { get; init; } = new();
}
