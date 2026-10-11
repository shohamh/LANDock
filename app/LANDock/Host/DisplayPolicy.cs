namespace LANDock.Host;

/// <summary>
/// What one virtual display may do for one desktop: whether the desktop gets it at all, and the largest size and frame rate it
/// may use. Chosen per paired desktop on the laptop, and remembered with the desktop. The defaults keep everything as it was:
/// on, up to 4K, 60 frames per second.
/// </summary>
public sealed record DisplayPolicy(string Name, bool Enabled = true, int MaxWidth = 3840, int MaxHeight = 2160, int MaxFps = 60);

public static class DisplayPolicies
{
    /// <summary>The largest sizes offered in the settings.</summary>
    public static readonly (string label, int width, int height)[] Sizes =
    {
        ("720p (1280x720)", 1280, 720),
        ("1080p (1920x1080)", 1920, 1080),
        ("1440p (2560x1440)", 2560, 1440),
        ("4K (3840x2160)", 3840, 2160),
    };

    public static readonly int[] FrameRates = { 30, 60 };

    /// <summary>One policy per display, all on at the defaults.</summary>
    public static List<DisplayPolicy> Defaults() =>
        HostPlanning.AllInstances.Select(i => new DisplayPolicy(i.Label)).ToList();

    /// <summary>The policy of one display in a desktop's list (the defaults when the list has none for it).</summary>
    public static DisplayPolicy For(IEnumerable<DisplayPolicy>? list, string name) =>
        list?.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? new DisplayPolicy(name);

    /// <summary>
    /// The choice for a desktop screen (plan-monitor-layout.md, step 4): the entry keyed by the screen's id. When there is none, the
    /// entry keyed by the display name (the choices of older versions); when there is none of those, the defaults for the screen.
    /// </summary>
    public static DisplayPolicy Choose(IEnumerable<DisplayPolicy>? list, string? screenId, string? label)
    {
        var l = list?.ToList();
        if (screenId != null && l?.FirstOrDefault(p => p.Name.Equals(screenId, StringComparison.OrdinalIgnoreCase)) is { } byScreen) return byScreen;
        if (label != null && l?.FirstOrDefault(p => p.Name.Equals(label, StringComparison.OrdinalIgnoreCase)) is { } byLabel) return byLabel;
        return new DisplayPolicy(screenId ?? label ?? "");
    }

    /// <summary>The label of the largest size (for a width and height), as shown in the settings.</summary>
    public static string SizeLabel(int width, int height) =>
        Sizes.FirstOrDefault(s => s.width == width && s.height == height).label ?? $"{width}x{height}";
}
