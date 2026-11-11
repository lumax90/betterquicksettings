namespace BetterQuickSettings.Core;

public sealed record Preferences
{
    public bool ReplaceQuickSettings { get; init; } = true;
    public bool AutoHeadphones { get; init; } = true;
    public string? PreferredHeadphonesId { get; init; }
    public bool ToolsExpanded { get; init; }
    public string? ScreenshotFolder { get; init; }
    public double GlassAmount { get; init; } = 55;
    public double PanelHeight { get; init; } = 360;
    public bool ClipboardHistoryEnabled { get; init; } = true;
    public bool VolumeFeedbackEnabled { get; init; } = true;
}

public sealed record AudioOutput(string Id, string Name, bool IsHeadphones, bool IsDefault);
