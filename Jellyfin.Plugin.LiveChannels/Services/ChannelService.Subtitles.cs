using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: picking and extracting the burn-in subtitle for a program.
public partial class ChannelService
{
    // Item/track keys whose subtitle extraction is currently running, so concurrent tune-ins don't pile a
    // second whole-file extraction onto the producer's critical path.
    private readonly ConcurrentDictionary<string, byte> _subtitleExtractions = new(StringComparer.Ordinal);

    /// <summary>
    /// Picks the subtitle track to burn into an item. A scoring system ranks every eligible subtitle by
    /// forced flag, text type, language match, SDH status, source preference, default flag, and index —
    /// in that priority order. When a language preference is configured, subtitles in non-matching
    /// languages are filtered out entirely (hard filter, not just a tie-breaker).
    /// </summary>
    /// <param name="program">The program, carrying its subtitle streams and default-audio language probed at refresh.</param>
    /// <param name="mode">The channel's subtitle burn-in mode.</param>
    /// <returns>The chosen subtitle's index among the item's subtitle streams and whether it is text-based, or <c>null</c> when nothing should be burned in.</returns>
    public (int RelativeIndex, bool IsText)? FindBurnInSubtitle(ProgramEntry program, SubtitleBurnInMode mode)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (mode == SubtitleBurnInMode.Never)
        {
            return null;
        }

        var subtitles = program.Subtitles;
        if (subtitles.Count == 0)
        {
            return null;
        }

        var config = Plugin.Instance?.ReadConfiguration(c => c);
        var defaultLanguage = config?.DefaultSubtitleLanguage ?? string.Empty;
        var audioLanguage = program.DefaultAudioLanguage;

        // "Forced only" escalates to Always behaviour when the audio is foreign, so foreign content stays followable.
        var forcedOnlyEscalate = mode == SubtitleBurnInMode.Forced
            && !string.IsNullOrEmpty(audioLanguage)
            && !string.IsNullOrEmpty(defaultLanguage)
            && !string.Equals(audioLanguage, defaultLanguage, StringComparison.OrdinalIgnoreCase);

        if (forcedOnlyEscalate)
        {
            _logger.LogDebug("Subtitle: mode Forced but audio '{Audio}' differs from default '{Default}' — escalating to Always", audioLanguage, defaultLanguage);
        }

        // Build the candidate list. Strict Forced-only without escalation considers only forced tracks;
        // Always and escalated Forced consider every subtitle.
        var candidates = new List<int>();
        for (var i = 0; i < subtitles.Count; i++)
        {
            if (mode == SubtitleBurnInMode.Forced && !forcedOnlyEscalate && !subtitles[i].IsForced)
            {
                continue;
            }

            candidates.Add(i);
        }

        if (candidates.Count == 0)
        {
            _logger.LogDebug("Subtitle: mode {Mode} but no matching candidates from {Count} subtitle tracks for \"{Title}\"", mode, subtitles.Count, program.Title);
            return null;
        }

        var langPrefs = ParseLanguagePreferences(config?.SubtitleLanguagePreference);
        var sourcePref = config?.SubtitleSourcePreference ?? SubtitleSourcePreference.Auto;
        var preferNonSdh = config?.PreferNonSdhSubtitles ?? false;

        // Language preference acts as a strict filter: when set, only subtitles matching one of the listed
        // languages are eligible. If none match, no subtitle is burned at all (return null). This lets a user
        // say "eng,deu only" and never see subtitles in a language they can't read. Empty preference = no filter.
        // Subtitles with no language tag are assumed to be in the item's default audio language — a sidecar
        // named Movie.srt next to English audio is almost certainly English.
        var audioLang = program.DefaultAudioLanguage ?? string.Empty;
        if (langPrefs.Count > 0)
        {
            var filtered = new List<int>();
            foreach (var idx in candidates)
            {
                var lang = subtitles[idx].Language;
                if (string.IsNullOrEmpty(lang))
                {
                    lang = audioLang;
                }

                if (!string.IsNullOrEmpty(lang) && langPrefs.Any(l => string.Equals(l, lang, StringComparison.OrdinalIgnoreCase)))
                {
                    filtered.Add(idx);
                }
            }

            if (filtered.Count == 0)
            {
                _logger.LogInformation("Subtitle: {Count} track(s) exist but none match language filter [{Prefs}] for \"{Title}\" — no subtitle",
                    subtitles.Count, string.Join(", ", langPrefs), program.Title);
                return null;
            }

            candidates = filtered;
        }

        // Score each candidate. Higher score wins. The weights ensure the priority order:
        // forced > text > language match > SDH penalty > source preference > default flag > lower index.
        var best = candidates[0];
        var bestScore = int.MinValue;

        foreach (var idx in candidates)
        {
            var sub = subtitles[idx];
            var score = 0;

            if (sub.IsForced)
            {
                score += 100000;
            }

            // Text subs always rank above bitmap subs (PGS/VOBSUB), which are fragile for burn-in.
            if (sub.IsText)
            {
                score += 20000;
            }

            // Language preference: earlier in the list = higher priority. The (count - li) * 1000 spacing
            // ensures a 1000-point gap between languages that the index penalty can never overwhelm.
            // Untagged subs use the audio language.
            if (langPrefs.Count > 0)
            {
                var lang = string.IsNullOrEmpty(sub.Language) ? audioLang : sub.Language;
                if (!string.IsNullOrEmpty(lang))
                {
                    for (var li = 0; li < langPrefs.Count; li++)
                    {
                        if (string.Equals(langPrefs[li], lang, StringComparison.OrdinalIgnoreCase))
                        {
                            score += (langPrefs.Count - li) * 1000;
                            break;
                        }
                    }
                }
            }

            // SDH deprioritization (opt-in): ranks regular subs above hearing-impaired in the same language.
            if (preferNonSdh && sub.IsHearingImpaired)
            {
                score -= 500;
            }

            if (sourcePref == SubtitleSourcePreference.PreferExternal && sub.IsExternal)
            {
                score += 100;
            }
            else if (sourcePref == SubtitleSourcePreference.PreferInternal && !sub.IsExternal)
            {
                score += 100;
            }

            if (sub.IsDefault)
            {
                score += 10;
            }

            score -= idx; // lower index wins exact ties

            if (score > bestScore)
            {
                bestScore = score;
                best = idx;
            }
        }

        var chosen = subtitles[best];
        _logger.LogInformation("Subtitle: chose track {RelIdx}/{Total} (lang={Lang}, forced={Forced}, default={Default}, external={Ext}, text={Text}, sdh={Sdh}, score={Score}) from {Candidates} candidate(s) for \"{Title}\"",
            chosen.RelativeIndex, subtitles.Count,
            string.IsNullOrEmpty(chosen.Language) ? "—" : chosen.Language,
            chosen.IsForced, chosen.IsDefault, chosen.IsExternal, chosen.IsText, chosen.IsHearingImpaired,
            bestScore,
            candidates.Count,
            program.Title);

        return (chosen.RelativeIndex, chosen.IsText);
    }

    /// <summary>
    /// Finds an external text subtitle among the program's subtitle streams that could serve as a fallback
    /// when the primary (embedded) subtitle's burn-in fails. Applies the same language filter as
    /// <see cref="FindBurnInSubtitle"/>. Excludes the already-tried track. Returns null when no suitable
    /// external text alternative exists.
    /// </summary>
    /// <param name="program">The program carrying subtitle metadata.</param>
    /// <param name="excludeRelativeIndex">The relative index of the subtitle already tried (to skip it).</param>
    /// <returns>A matching external text subtitle, or <c>null</c>.</returns>
    public (int RelativeIndex, bool IsText)? FindExternalTextAlternative(ProgramEntry program, int excludeRelativeIndex)
    {
        var langPrefs = ParseLanguagePreferences(Plugin.Instance?.ReadConfiguration(c => c.SubtitleLanguagePreference));
        var audioLang = program.DefaultAudioLanguage ?? string.Empty;

        foreach (var sub in program.Subtitles)
        {
            if (!sub.IsExternal || !sub.IsText)
            {
                continue;
            }

            if (sub.RelativeIndex == excludeRelativeIndex)
            {
                continue;
            }

            if (langPrefs.Count > 0)
            {
                var lang = string.IsNullOrEmpty(sub.Language) ? audioLang : sub.Language;
                if (string.IsNullOrEmpty(lang) || !langPrefs.Any(l => string.Equals(l, lang, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            return (sub.RelativeIndex, sub.IsText);
        }

        return null;
    }

    private static List<string> ParseLanguagePreferences(string? pref)
    {
        if (string.IsNullOrWhiteSpace(pref))
        {
            return new List<string>();
        }

        return pref.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToList();
    }

    /// <summary>
    /// Extracts the chosen text subtitle to a cleaned (and optionally styled) ASS file and returns its path.
    /// Two extraction paths: sidecar subtitles are read directly or converted via our own ffmpeg (fast and
    /// reliable); embedded subtitles use Jellyfin's <c>GetSubtitles</c> API (cached, warms in the background
    /// via <c>CancellationToken.None</c> when the 1.5s tune-in budget runs out). If an embedded extraction
    /// isn't ready, falls back to an external sidecar alternative in the same language.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="relativeIndex">The chosen subtitle's index among the item's subtitle streams.</param>
    /// <param name="offset">How far into the item the tune-in is; passed to <c>GetSubtitles</c> so only events from the seek point on are extracted.</param>
    /// <param name="outputDirectory">Where to write the burn-ready ASS file.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The ASS file path, or <c>null</c> when it could not be produced.</returns>
    public async Task<string?> TryExtractTuneInSubtitleAsync(Guid itemId, int relativeIndex, TimeSpan offset, string outputDirectory, CancellationToken cancellationToken)
    {
        var key = itemId.ToString("N", CultureInfo.InvariantCulture) + "-" + relativeIndex.ToString(CultureInfo.InvariantCulture);

        // Only one extraction per item/track at a time. A concurrent tune-in skips rather than launching a
        // duplicate; it simply starts without a burned-in subtitle, as a cold tune-in already does.
        if (!_subtitleExtractions.TryAdd(key, 0))
        {
            _logger.LogDebug("Subtitle: extraction already in progress for item {ItemId} track {RelIdx} — skipping", itemId, relativeIndex);
            return null;
        }

        try
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                return null;
            }

            var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
            var source = sources.Count > 0 ? sources[0] : null;
            if (source?.MediaStreams is null)
            {
                return null;
            }

            var subtitles = source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle).OrderBy(s => s.Index).ToList();
            if (relativeIndex < 0 || relativeIndex >= subtitles.Count)
            {
                return null;
            }

            var stream = subtitles[relativeIndex];

            // Two extraction paths:
            // - Sidecar (external) subtitles: read the file directly or convert via our own ffmpeg.
            //   Fast (the file is tiny), reliable, and doesn't touch Jellyfin's subtitle cache.
            // - Embedded subtitles: use Jellyfin's GetSubtitles (the upstream path). It extracts via
            //   Jellyfin's own batch pipeline, caches the result, and — crucially — keeps extracting
            //   in the background (CancellationToken.None) when the 1.5s tune-in budget runs out, so
            //   the cache is warm for the next tune-in. Our own ffmpeg extraction couldn't match this
            //   because it had no persistence: a timeout killed the process and lost the work.
            string? ass;
            if (stream.IsExternal && !string.IsNullOrEmpty(stream.Path))
            {
                ass = await ExtractSidecarToAssAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ass = await ExtractEmbeddedToAssAsync(item, source, stream, offset, cancellationToken).ConfigureAwait(false);

                // If the embedded extraction wasn't ready (timed out or failed), try to fall back to an
                // external sidecar subtitle in the same language. Sidecar extraction is fast and reliable
                // (tiny file), so it usually succeeds where the embedded path couldn't.
                if (string.IsNullOrEmpty(ass))
                {
                    var alt = FindExternalSidecarByLanguage(subtitles, stream.Language);
                    if (alt is not null)
                    {
                        _logger.LogInformation("Subtitle: embedded extraction not ready — falling back to external sidecar ({Lang}) {Path}", alt.Language, alt.Path);
                        ass = await ExtractSidecarToAssAsync(alt, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (string.IsNullOrEmpty(ass))
            {
                _logger.LogWarning("Subtitle: extraction produced no output for \"{Title}\"", item.Name);
                return null;
            }

            // Strip HTML markup that libass would render as literal text.
            ass = SubtitleStyler.CleanAss(ass);

            // When subtitle styling is enabled, force a uniform appearance: strip ASS override blocks,
            // rewrite PlayResX/Y, and replace every Style line so all sources share one look.
            var ssWidth = Plugin.Instance?.ReadConfiguration(c => c.TranscodeWidth) ?? 1280;
            var ssStyle = Plugin.Instance?.ReadConfiguration(c => c.SubtitleStyle);
            if (ssStyle is { Enabled: true })
            {
                var ssHeight = (int)Math.Round(ssWidth * 9.0 / 16.0);
                if (ssHeight % 2 != 0)
                {
                    ssHeight++;
                }

                ass = SubtitleStyler.StyleAss(ass, ssStyle, ssWidth, ssHeight);
            }

            // Write to a temp file then move atomically so a reader never sees a half-written file.
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, "lc-sub-" + key + ".ass");
            var temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";

            try
            {
                var file = new FileStream(temp, FileMode.Create, FileAccess.Write);
                await using (file.ConfigureAwait(false))
                {
                    using var writer = new StreamWriter(file, Encoding.UTF8);
                    await writer.WriteAsync(ass.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDeleteSubtitle(temp);
                throw;
            }

            return path;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not prepare a burn-in subtitle for {ItemId}", itemId);
            return null;
        }
        finally
        {
            _subtitleExtractions.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Extracts an embedded subtitle to ASS via Jellyfin's <c>GetSubtitles</c> API — the same path upstream uses.
    /// Jellyfin caches the extraction and, because the task uses <c>CancellationToken.None</c>, keeps extracting
    /// in the background when the 1.5s tune-in budget runs out. The cache is then warm for the next tune-in of
    /// the same item, making subsequent plays instant.
    /// </summary>
    private async Task<string?> ExtractEmbeddedToAssAsync(BaseItem item, MediaSourceInfo source, MediaStream stream, TimeSpan offset, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Subtitle: extracting embedded stream {Index} ({Codec}) via Jellyfin GetSubtitles", stream.Index, stream.Codec ?? "?");

        var extract = _subtitleEncoder.GetSubtitles(item, source.Id, stream.Index, "ass", offset.Ticks, 0, true, CancellationToken.None);
        var ready = await Task.WhenAny(extract, Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken)).ConfigureAwait(false);
        if (ready != extract || extract.Status != TaskStatus.RanToCompletion)
        {
            _logger.LogInformation("Subtitle: embedded extraction not ready within 1.5s — cache will warm for next tune-in");
            return null;
        }

        var subtitleStream = await extract.ConfigureAwait(false);
        await using (subtitleStream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(subtitleStream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads or converts a sidecar subtitle file to ASS. ASS/SSA files are read directly (no ffmpeg needed);
    /// other formats (SRT, VTT) are converted via our own ffmpeg with <c>-y</c> and a 1.5s timeout. Sidecar
    /// files are tiny, so this is fast and reliable.
    /// </summary>
    private async Task<string?> ExtractSidecarToAssAsync(MediaStream stream, CancellationToken cancellationToken)
    {
        var codec = stream.Codec ?? string.Empty;
        if (IsAssCodec(codec) && File.Exists(stream.Path))
        {
            _logger.LogDebug("Subtitle: reading external ASS sidecar {Path}", stream.Path);
            return await File.ReadAllTextAsync(stream.Path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Subtitle: converting external sidecar ({Codec}) to ASS via ffmpeg: {Path}", codec, stream.Path);
        return await ConvertSidecarViaFfmpegAsync(_mediaEncoder.EncoderPath, stream.Path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs ffmpeg to convert a sidecar subtitle file to ASS via stdout. Bounded by a 1.5s timeout — sidecar
    /// files are small, so this should never be tight.
    /// </summary>
    private async Task<string?> ConvertSidecarViaFfmpegAsync(string ffmpeg, string inputPath, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(1.5));
        var token = timeoutCts.Token;

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-c:s");
        startInfo.ArgumentList.Add("ass");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("ass");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            _logger.LogWarning("Subtitle: sidecar conversion timed out after 1.5s for {Path}", inputPath);
            return null;
        }

        _ = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        return string.IsNullOrEmpty(stdout) ? null : stdout;
    }

    /// <summary>
    /// Finds an external sidecar text subtitle matching the given language, for use as a fallback when
    /// embedded extraction times out. Falls back to any external text sub if no language match exists.
    /// Operates on raw <see cref="MediaStream"/> objects (unlike <see cref="FindExternalTextAlternative"/>
    /// which operates on <see cref="ProgramEntry"/> and applies the config-based language filter).
    /// </summary>
    private static MediaStream? FindExternalSidecarByLanguage(List<MediaStream> subtitles, string language)
    {
        MediaStream? langMatch = null;
        MediaStream? anyExternal = null;

        foreach (var s in subtitles)
        {
            if (!s.IsExternal || !s.IsTextSubtitleStream || string.IsNullOrEmpty(s.Path))
            {
                continue;
            }

            anyExternal ??= s;

            if (!string.IsNullOrEmpty(language) && string.Equals(s.Language, language, StringComparison.OrdinalIgnoreCase))
            {
                langMatch = s;
                break; // exact language match — best possible alternative
            }
        }

        return langMatch ?? anyExternal;
    }

    private static bool IsAssCodec(string codec)
        => codec.Equals("ass", StringComparison.OrdinalIgnoreCase)
           || codec.Equals("ssa", StringComparison.OrdinalIgnoreCase);

    private void TryDeleteSubtitle(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete temp subtitle {Path}", path);
        }
    }
}
