using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveChannels.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Pure static subtitle track selection. A comparer-chain pipeline ranks every eligible subtitle by
/// mode-specific criteria. Language preference filters and sorts; SDH and source preferences are soft
/// tiebreakers. No Jellyfin dependencies — operates on the probed <see cref="SubtitleStreamInfo"/> list.
/// </summary>
public static class SubtitleSelector
{
    // Maps two-letter ISO 639-1 codes to three-letter ISO 639-2/B codes for language comparison.
    // Values not in this table pass through unchanged.
    private static readonly Dictionary<string, string> Iso2To3 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng", ["ja"] = "jpn", ["de"] = "deu", ["fr"] = "fra",
        ["es"] = "spa", ["it"] = "ita", ["pt"] = "por", ["ru"] = "rus",
        ["ko"] = "kor", ["zh"] = "zho", ["ar"] = "ara", ["hi"] = "hin",
        ["nl"] = "nld", ["sv"] = "swe", ["pl"] = "pol", ["tr"] = "tur",
        ["fi"] = "fin", ["no"] = "nor", ["da"] = "dan", ["cs"] = "ces",
        ["hu"] = "hun", ["ro"] = "ron", ["th"] = "tha", ["vi"] = "vie",
        ["id"] = "ind", ["uk"] = "ukr", ["ca"] = "cat", ["he"] = "heb",
        ["el"] = "ell",
    };

    private enum SortBasis { LanguageFirst, ForcedFirst }

    /// <summary>Normalizes a language code to its three-letter form for comparison.</summary>
    internal static string NormalizeLanguage(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }

        return Iso2To3.TryGetValue(code, out var iso3) ? iso3 : code;
    }

    /// <summary>Parses a comma-separated language preference string into a normalized list.</summary>
    internal static List<string> ParseLanguageList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>(0);
        }

        var list = new List<string>();
        foreach (var part in value.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                list.Add(trimmed);
            }
        }

        return list;
    }

    /// <summary>
    /// Picks the subtitle track to burn into an item. A comparer-chain pipeline ranks every eligible subtitle by
    /// mode-specific criteria. Language preference filters and sorts; SDH and source preferences are soft tiebreakers.
    /// </summary>
    /// <param name="program">The program, carrying its subtitle streams probed at refresh.</param>
    /// <param name="mode">The channel's subtitle burn-in mode.</param>
    /// <param name="preferredLanguages">Comma-separated preferred language codes, or empty.</param>
    /// <param name="preferNonSdh">Whether to deprioritize SDH tracks.</param>
    /// <param name="sourcePreference">External/internal subtitle source preference.</param>
    /// <param name="logger">Optional logger for decision-chain diagnostics at Warning level.</param>
    /// <returns>The chosen subtitle's index and whether it is text-based, or <c>null</c>.</returns>
    public static (int RelativeIndex, bool IsText)? FindBurnInSubtitle(
        ProgramEntry program,
        SubtitleBurnInMode mode,
        string preferredLanguages,
        bool preferNonSdh,
        SubtitleSourcePreference sourcePreference,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        var log = logger != null && logger.IsEnabled(LogLevel.Warning) ? new List<string>() : null;

        var candidates = program.Subtitles.Where(s => s.IsText || !s.IsExternal).ToList();
        var prefs = ParseLanguageList(preferredLanguages);

        var opts = new List<string>();
        if (prefs.Count > 0) opts.Add(string.Join(",", prefs));
        opts.Add(preferNonSdh ? "nosdh" : "anysdh");
        var src = sourcePreference.ToString();
        if (src == "PreferExternal") opts.Add("external");
        else if (src == "PreferInternal") opts.Add("internal");
        else opts.Add("auto");

        log?.Add($"\"{program.Title}\"");
        log?.Add($"{mode}({string.Join(",", opts)})");
        log?.Add($"tracks: [{TrackList(candidates)}]");

        if (candidates.Count == 0)
        {
            if (log != null) logger!.LogWarning("[Subtitle] {Chain} → skipped (no burnable)", string.Join(" → ", log));
            return null;
        }

        if (mode == SubtitleBurnInMode.Never)
        {
            if (log != null) logger!.LogWarning("[Subtitle] {Chain} → skipped", string.Join(" → ", log));
            return null;
        }

        // Forced mode: keep only forced tracks.
        if (mode == SubtitleBurnInMode.Forced)
        {
            var before = candidates.Count;
            candidates = candidates.Where(s => s.IsForced).ToList();
            if (candidates.Count < before) log?.Add($"forced [{TrackList(candidates)}]");

            if (candidates.Count == 0)
            {
                if (log != null) logger!.LogWarning("[Subtitle] {Chain} → skipped (no forced)", string.Join(" → ", log));
                return null;
            }
        }

        // Language filter.
        if ((mode == SubtitleBurnInMode.Forced || mode == SubtitleBurnInMode.Always) && prefs.Count > 0)
        {
            var before = candidates.Count;
            candidates = FilterByLanguage(candidates, prefs);
            if (candidates.Count < before) log?.Add($"lang [{TrackList(candidates)}]");

            if (candidates.Count == 0)
            {
                if (log != null) logger!.LogWarning("[Subtitle] {Chain} → skipped (no lang match)", string.Join(" → ", log));
                return null;
            }
        }

        SubtitleStreamInfo best;

        if (mode == SubtitleBurnInMode.Always)
        {
            if (prefs.Count > 0)
            {
                var nonForced = candidates.Where(s => !s.IsForced).ToList();
                var forced = candidates.Where(s => s.IsForced).ToList();
                var pool = nonForced.Count > 0 ? nonForced : forced;
                var comparer = BuildComparer(prefs, preferNonSdh, sourcePreference, SortBasis.LanguageFirst);
                pool.Sort(comparer);
                best = pool[0];
            }
            else
            {
                candidates.Sort(BuildComparer(prefs, preferNonSdh, sourcePreference, SortBasis.ForcedFirst));
                best = candidates[0];
            }
        }
        else
        {
            var basis = mode == SubtitleBurnInMode.Forced ? SortBasis.LanguageFirst : SortBasis.ForcedFirst;
            candidates.Sort(BuildComparer(prefs, preferNonSdh, sourcePreference, basis));
            best = candidates[0];
        }

        log?.Add($"#{best.RelativeIndex} {Lang(best)}");
        if (log != null) logger!.LogWarning("[Subtitle] {Chain}", string.Join(" → ", log));
        return (best.RelativeIndex, best.IsText);
    }

    private static string Lang(SubtitleStreamInfo t)
    {
        var code = string.IsNullOrEmpty(t.Language) ? "?" : t.Language;
        var flags = "";
        if (t.IsDefault) flags += "d";
        if (t.IsForced) flags += "f";
        if (t.IsHearingImpaired) flags += "s";
        if (t.IsExternal) flags += "e";
        return flags.Length > 0 ? $"{code}({flags})" : code;
    }

    private static string TrackList(List<SubtitleStreamInfo> tracks)
        => string.Join(",", tracks.Select(Lang));

    /// <summary>Filters candidates to those whose language matches a preferred entry. Tracks with no language tag
    /// pass as wildcards.</summary>
    private static List<SubtitleStreamInfo> FilterByLanguage(
        List<SubtitleStreamInfo> candidates,
        List<string> prefs)
    {
        var filtered = new List<SubtitleStreamInfo>();
        foreach (var sub in candidates)
        {
            var lang = NormalizeLanguage(sub.Language);
            if (lang.Length == 0)
            {
                filtered.Add(sub);
                continue;
            }

            foreach (var p in prefs)
            {
                if (string.Equals(NormalizeLanguage(p), lang, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(sub);
                    break;
                }
            }
        }

        return filtered;
    }

    /// <summary>Builds a multi-key comparer. <paramref name="basis"/> selects whether forced flag or
    /// language rank is the primary sort key.</summary>
    private static Comparer<SubtitleStreamInfo> BuildComparer(
        List<string> prefs,
        bool preferNonSdh,
        SubtitleSourcePreference sourcePreference,
        SortBasis basis)
    {
        return Comparer<SubtitleStreamInfo>.Create((a, b) =>
        {
            int cmp;

            if (basis == SortBasis.ForcedFirst)
            {
                cmp = CompareByForced(a, b);
                if (cmp != 0) return cmp;
            }

            if (prefs.Count > 0)
            {
                cmp = CompareByLanguage(a, b, prefs);
                if (cmp != 0) return cmp;
            }

            cmp = CompareByDefault(a, b);
            if (cmp != 0) return cmp;

            cmp = CompareByNonSdh(a, b, preferNonSdh);
            if (cmp != 0) return cmp;

            cmp = CompareBySource(a, b, sourcePreference);
            if (cmp != 0) return cmp;

            return CompareByIndex(a, b);
        });
    }

    private static int CompareByForced(SubtitleStreamInfo a, SubtitleStreamInfo b)
        => b.IsForced.CompareTo(a.IsForced);

    private static int CompareByLanguage(SubtitleStreamInfo a, SubtitleStreamInfo b, List<string> prefs)
    {
        var rankA = LanguageRank(a.Language, prefs);
        var rankB = LanguageRank(b.Language, prefs);
        return rankA.CompareTo(rankB);
    }

    private static int CompareByDefault(SubtitleStreamInfo a, SubtitleStreamInfo b)
        => b.IsDefault.CompareTo(a.IsDefault);

    private static int CompareByNonSdh(SubtitleStreamInfo a, SubtitleStreamInfo b, bool preferNonSdh)
    {
        if (!preferNonSdh) return 0;
        return a.IsHearingImpaired.CompareTo(b.IsHearingImpaired);
    }

    private static int CompareBySource(SubtitleStreamInfo a, SubtitleStreamInfo b,
        SubtitleSourcePreference sourcePreference)
    {
        if (sourcePreference == SubtitleSourcePreference.Auto) return 0;
        var preferExternal = sourcePreference == SubtitleSourcePreference.PreferExternal;
        var aMatch = a.IsExternal == preferExternal;
        var bMatch = b.IsExternal == preferExternal;
        return bMatch.CompareTo(aMatch);
    }

    private static int CompareByIndex(SubtitleStreamInfo a, SubtitleStreamInfo b)
        => a.RelativeIndex.CompareTo(b.RelativeIndex);

    /// <summary>Returns the rank of a language in the preference list (0 = highest priority).
    /// Returns <c>int.MaxValue</c> for wildcards and non-matching languages.</summary>
    private static int LanguageRank(string language, List<string> prefs)
    {
        var lang = NormalizeLanguage(language);
        if (lang.Length == 0)
        {
            return int.MaxValue;
        }

        for (var i = 0; i < prefs.Count; i++)
        {
            if (string.Equals(NormalizeLanguage(prefs[i]), lang, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
