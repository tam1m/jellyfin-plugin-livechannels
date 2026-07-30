namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// Whether to prefer external (sidecar) or internal (embedded) subtitle streams when
/// both are available. A soft preference — breaks ties, never filters content out.
/// </summary>
public enum SubtitleSourcePreference
{
    /// <summary>No source preference.</summary>
    Auto = 0,

    /// <summary>Prefer external sidecar subtitle files (e.g. <c>movie.en.srt</c>).</summary>
    PreferExternal = 1,

    /// <summary>Prefer subtitle streams embedded in the media container.</summary>
    PreferInternal = 2
}
