namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// The global appearance applied to every burned-in text subtitle when <see cref="Enabled"/> is on.
/// When enabled, the original subtitle's own fonts, colours, positions, and inline styling are fully
/// overridden so every channel shows one uniform look. When disabled, each subtitle keeps its
/// original appearance (after HTML tag cleanup).
/// </summary>
public sealed class SubtitleStyle
{
    /// <summary>Gets or sets a value indicating whether subtitle styling is overridden. Off by default so existing configs are unaffected.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the font family name (e.g. <c>DejaVu Sans</c>). Empty uses libass's default font.</summary>
    public string FontFamily { get; set; } = string.Empty;

    /// <summary>Gets or sets the font size as a percentage of the video frame height (2–10). Default 4.2 matches Netflix's medium subtitle size.</summary>
    public int FontSizePercent { get; set; } = 4;

    /// <summary>Gets or sets the primary (fill) text colour as <c>#RRGGBB</c>. Default is white, matching Netflix.</summary>
    public string PrimaryColour { get; set; } = "#FFFFFF";

    /// <summary>Gets or sets the outline (border) colour as <c>#RRGGBB</c>. Default is a near-black that Netflix uses for its drop shadow look.</summary>
    public string OutlineColour { get; set; } = "#000000";

    /// <summary>Gets or sets a value indicating whether the text is bold. Netflix uses a medium-weight (semi-bold) font rather than true bold, so this defaults off.</summary>
    public bool Bold { get; set; }

    /// <summary>Gets or sets a value indicating whether the text is italic.</summary>
    public bool Italic { get; set; }

    /// <summary>Gets or sets the screen position as an ASS alignment numpad value (1 = bottom-left through 9 = top-right). Default is 2 (bottom-center), matching Netflix.</summary>
    public int Alignment { get; set; } = 2;

    /// <summary>Gets or sets the vertical margin from the nearest top or bottom edge as a percentage of frame height (0–20). Default 5.5 matches Netflix's bottom placement.</summary>
    public int MarginVerticalPercent { get; set; } = 6;
}
