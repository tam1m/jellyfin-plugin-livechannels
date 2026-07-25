namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// Whether to prefer external (sidecar) or internal (embedded) subtitle streams when both are available.
/// This is a soft preference — it breaks ties after forced/language/default, never filters content out.
/// </summary>
public enum SubtitleSourcePreference
{
    /// <summary>No source preference; use forced/default/index ordering only.</summary>
    Auto,

    /// <summary>Prefer external sidecar subtitle files (e.g. movie.en.srt).</summary>
    PreferExternal,

    /// <summary>Prefer subtitle streams embedded in the media container.</summary>
    PreferInternal
}
