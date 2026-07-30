using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveChannels.Models;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveChannels.Services;

// ChannelService: extracting the burn-in subtitle for a program. The selection logic lives in
// Utilities/SubtitleSelector.cs.
public partial class ChannelService
{
    // Item/track keys whose subtitle extraction is currently running, so concurrent tune-ins don't pile a
    // second whole-file extraction onto the producer's critical path.
    private readonly ConcurrentDictionary<string, byte> _subtitleExtractions = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves the chosen embedded text subtitle to a burn-ready file the same way Jellyfin's own transcodes do:
    /// <see cref="ISubtitleEncoder.GetSubtitleFilePath"/> extracts the track once into Jellyfin's subtitle cache
    /// (shared with normal playback, so it is usually already warm) and returns that file. Burning the extracted
    /// file rather than the media file means libass reads a few kilobytes instead of scanning gigabytes to reach a
    /// deep tune-in point, and the file keeps the markup the track was authored with, so bold, italic, and colour
    /// tags survive into the picture.
    /// <para>
    /// Bounded by a short timeout, because this sits on the producer's critical path: a cold extraction keeps
    /// running in the background to warm the cache while the caller falls back for this one item.
    /// </para>
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="relativeIndex">The chosen subtitle's index among the item's subtitle streams.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The subtitle file and any attached-font directory, or <c>null</c> when it was not ready in time.</returns>
    public async Task<BurnInSubtitleFile?> TryResolveBurnInSubtitleAsync(Guid itemId, int relativeIndex, CancellationToken cancellationToken)
    {
        var key = itemId.ToString("N", CultureInfo.InvariantCulture) + "-" + relativeIndex.ToString(CultureInfo.InvariantCulture);

        // Only one extraction per item/track at a time. A concurrent tune-in skips rather than launching a
        // duplicate; it simply starts without a burned-in subtitle, as a cold tune-in already does.
        if (!_subtitleExtractions.TryAdd(key, 0))
        {
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

            // CancellationToken.None so a cold extraction still finishes caching even after we stop waiting for
            // it below; the next airing of this item then burns it immediately.
            var resolve = _subtitleEncoder.GetSubtitleFilePath(subtitles[relativeIndex], source, CancellationToken.None);
            var ready = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken)).ConfigureAwait(false);
            if (ready != resolve || resolve.Status != TaskStatus.RanToCompletion)
            {
                _logger.LogWarning("[Subtitle] \"{Title}\" extraction warming for #{Index} — burning from media file",
                    item.Name, relativeIndex);
                return null;
            }

            var path = await resolve.ConfigureAwait(false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            var subStream = subtitles[relativeIndex];
            _logger.LogWarning("[Subtitle] \"{Title}\" resolved #{Index} \"{Lang}\" ({Codec}{Flags}) → {Path}",
                item.Name, relativeIndex,
                subStream.Language ?? "?",
                subStream.Codec,
                (subStream.IsForced ? " forced" : "") + (subStream.IsDefault ? " default" : "") + (subStream.IsHearingImpaired ? " SDH" : ""),
                path);

            return new BurnInSubtitleFile(path, AttachmentFontsDirectory(source.Id));
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

    // The fonts Jellyfin extracted from the media's attachments, so an ASS subtitle authored against an embedded
    // font renders in it rather than a substitute. Null when the item carries none.
    private string? AttachmentFontsDirectory(string mediaSourceId)
    {
        try
        {
            var directory = _pathManager.GetAttachmentFolderPath(mediaSourceId);
            return !string.IsNullOrEmpty(directory) && Directory.Exists(directory) ? directory : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the attachment font directory for {MediaSourceId}", mediaSourceId);
            return null;
        }
    }
}

/// <summary>
/// A burn-ready subtitle: the extracted subtitle file plus, when the media carries attached fonts, the directory
/// libass should load them from.
/// </summary>
/// <param name="Path">The subtitle file to burn.</param>
/// <param name="FontsDirectory">The attached-font directory, or <c>null</c>.</param>
public sealed record BurnInSubtitleFile(string Path, string? FontsDirectory);
