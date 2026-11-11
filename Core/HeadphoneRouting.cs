using System.Collections.Generic;
using System.Linq;

namespace BetterQuickSettings.Core;

/// <summary>Routes only newly connected headphones; startup and manual selections are left alone.</summary>
public static class HeadphoneRouting
{
    public static AudioOutput? Choose(
        IReadOnlyCollection<AudioOutput> before,
        IReadOnlyCollection<AudioOutput> after,
        bool enabled,
        string? preferredId)
    {
        if (!enabled) return null;
        var previous = before.Select(d => d.Id).ToHashSet();
        return after.FirstOrDefault(d => !d.IsDefault && !previous.Contains(d.Id) &&
            (preferredId is not null ? d.Id == preferredId : d.IsHeadphones));
    }
}
