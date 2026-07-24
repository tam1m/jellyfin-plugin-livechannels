using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Cleans extracted ASS subtitle content so it renders correctly when burned in by ffmpeg's
/// <c>subtitles</c> filter (libass). libass only understands ASS override blocks (<c>{\...}</c>);
/// HTML-style markup that some SRT/WEBVTT sources carry (e.g. <c>&lt;i&gt;</c>, <c>&lt;font&gt;</c>)
/// renders as literal text on screen. This utility strips those tags from the dialogue text while
/// leaving the ASS header, style definitions, and valid ASS overrides untouched.
/// </summary>
public static class SubtitleStyler
{
    /// <summary>
    /// Matches HTML-style tags that start with a letter (optionally preceded by <c>/</c>),
    /// so <c>&lt;i&gt;</c>, <c>&lt;/font&gt;</c>, <c>&lt;br&gt;</c>, etc. are stripped but bare
    /// <c>&lt;</c> or <c>&gt;</c> characters that are not tags are left alone.
    /// </summary>
    private static readonly Regex HtmlTag = new(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);

    /// <summary>
    /// The number of comma-separated fields before the Text field in an ASS Events line
    /// (Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect — then Text).
    /// </summary>
    private const int FieldsBeforeText = 9;

    /// <summary>
    /// Strips HTML markup from the Text field of ASS <c>Dialogue:</c> and <c>Comment:</c> lines.
    /// The ASS header (<c>[Script Info]</c>), style definitions (<c>[V4+ Styles]</c>), and valid
    /// ASS override blocks (<c>{\i1}</c>) are passed through unchanged. Only the last field (Text,
    /// everything after the ninth comma) of dialogue/comment lines is cleaned, so style names and
    /// effect fields are never touched.
    /// </summary>
    /// <param name="ass">The raw ASS subtitle content as produced by Jellyfin's subtitle encoder.</param>
    /// <returns>The cleaned ASS content with HTML tags removed from dialogue text.</returns>
    public static string CleanAss(string ass)
    {
        ArgumentNullException.ThrowIfNull(ass);

        var lines = ass.Split('\n');
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var span = line.AsSpan();

            // Trim a trailing \r (from \r\n line endings) only for the prefix check — the line
            // content is processed in full below so the original ending is preserved on rejoin.
            if (span.Length > 0 && span[^1] == '\r')
            {
                span = span[..^1];
            }

            if (!span.StartsWith("Dialogue:", StringComparison.Ordinal)
                && !span.StartsWith("Comment:", StringComparison.Ordinal))
            {
                continue;
            }

            // The Text field is everything after the 9th comma (ASS Events format has 10 fields,
            // Text is last and may itself contain commas). Find that boundary.
            var textStart = IndexOfNthComma(line, FieldsBeforeText);
            if (textStart < 0 || textStart >= line.Length)
            {
                continue;
            }

            var prefix = line.Substring(0, textStart);
            var text = line.Substring(textStart);

            var cleaned = HtmlTag.Replace(text, string.Empty);
            if (!ReferenceEquals(cleaned, text))
            {
                lines[i] = prefix + cleaned;
                changed = true;
            }
        }

        return changed ? string.Join('\n', lines) : ass;
    }

    /// <summary>
    /// Returns the index immediately after the Nth comma in the string, or -1 when there are
    /// fewer than N commas.
    /// </summary>
    private static int IndexOfNthComma(string s, int n)
    {
        var count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == ',')
            {
                count++;
                if (count == n)
                {
                    return i + 1;
                }
            }
        }

        return -1;
    }
}
