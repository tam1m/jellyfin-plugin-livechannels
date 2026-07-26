using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveChannels.Models;

namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// Cleans and restyles extracted ASS subtitle content for burn-in. Two operations:
/// <list type="bullet">
/// <item><see cref="CleanAss"/> strips HTML markup from dialogue text so libass doesn't render tags like <c>&lt;i&gt;</c> as literal text.</item>
/// <item><see cref="StyleAss"/> forces a uniform appearance: strips ASS override blocks (<c>{\...}</c>), rewrites <c>PlayResX/Y</c>, and replaces every <c>Style:</c> line so all subtitles share one look regardless of source.</item>
/// </list>
/// </summary>
public static class SubtitleStyler
{
    /// <summary>
    /// Matches HTML-style tags that start with a letter (optionally preceded by <c>/</c>),
    /// so <c>&lt;i&gt;</c>, <c>&lt;/font&gt;</c>, <c>&lt;br&gt;</c>, etc. are stripped but bare
    /// <c>&lt;</c> or <c>&gt;</c> characters that are not tags are left alone.
    /// </summary>
    private static readonly Regex HtmlTag = new(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);

    /// <summary>Matches ASS override blocks (<c>{\i1}</c>, <c>{\an8}</c>, <c>{\pos(...)}</c>, etc.).</summary>
    private static readonly Regex AssOverride = new(@"\{[^}]*\}", RegexOptions.Compiled);

    /// <summary>
    /// The number of comma-separated fields before the Text field in an ASS Events line
    /// (Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect — then Text).
    /// </summary>
    private const int FieldsBeforeText = 9;

    /// <summary>
    /// Strips HTML markup from the Text field of ASS <c>Dialogue:</c> and <c>Comment:</c> lines.
    /// The ASS header (<c>[Script Info]</c>), style definitions (<c>[V4+ Styles]</c>), and valid
    /// ASS override blocks (<c>{\i1}</c>) are passed through unchanged.
    /// </summary>
    /// <param name="ass">The raw ASS subtitle content as produced by Jellyfin's subtitle encoder.</param>
    /// <returns>The cleaned ASS content with HTML tags removed from dialogue text.</returns>
    public static string CleanAss(string ass)
    {
        ArgumentNullException.ThrowIfNull(ass);
        return StripFromDialogueText(ass, HtmlTag);
    }

    /// <summary>
    /// Forces a uniform subtitle appearance. Strips ASS override blocks from dialogue text (so inline
    /// colour, size, and position overrides disappear), rewrites <c>PlayResX</c>/<c>PlayResY</c> to match
    /// the video dimensions (so pixel-based font sizing is exact), and replaces every <c>Style:</c> line
    /// with identical values derived from <paramref name="style"/>. After this pass, every subtitle line
    /// renders the same regardless of the source's original styling.
    /// </summary>
    /// <param name="ass">The ASS content (already cleaned by <see cref="CleanAss"/>).</param>
    /// <param name="style">The desired appearance.</param>
    /// <param name="videoWidth">The output video width in pixels (sets <c>PlayResX</c>).</param>
    /// <param name="videoHeight">The output video height in pixels (sets <c>PlayResY</c> and drives font/margin sizing).</param>
    /// <returns>The restyled ASS content.</returns>
    public static string StyleAss(string ass, SubtitleStyle style, int videoWidth, int videoHeight)
    {
        ArgumentNullException.ThrowIfNull(ass);
        ArgumentNullException.ThrowIfNull(style);

        // 1. Strip ASS override blocks ({\...}) from dialogue text.
        ass = StripFromDialogueText(ass, AssOverride);

        // 2. Rewrite PlayResX/Y and all Style lines in a single pass.
        var fontsize = (int)Math.Round(videoHeight * (style.FontSizePercent / 100.0));
        var marginV = (int)Math.Round(videoHeight * (style.MarginVerticalPercent / 100.0));
        var marginH = (int)Math.Round(videoWidth * 0.03);
        var primary = HtmlToAssColour(style.PrimaryColour);
        var outline = HtmlToAssColour(style.OutlineColour);
        var bold = style.Bold ? -1 : 0;
        var italic = style.Italic ? -1 : 0;

        var lines = new List<string>(ass.Split('\n'));
        var changed = false;
        string? section = null;
        var playResXSeen = false;
        var playResYSeen = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var span = line.AsSpan();
            var hasCr = span.Length > 0 && span[^1] == '\r';
            if (hasCr)
            {
                span = span[..^1];
            }

            // Section header — check whether we're leaving [Script Info] without PlayResX/Y.
            if (span.StartsWith("[", StringComparison.Ordinal))
            {
                if (section == "[Script Info]" && (!playResXSeen || !playResYSeen))
                {
                    var cr = hasCr ? "\r" : "";
                    if (!playResXSeen)
                    {
                        lines.Insert(i, "PlayResX: " + videoWidth.ToString(CultureInfo.InvariantCulture) + cr);
                        i++;
                    }

                    if (!playResYSeen)
                    {
                        lines.Insert(i, "PlayResY: " + videoHeight.ToString(CultureInfo.InvariantCulture) + cr);
                        i++;
                    }

                    changed = true;
                }

                section = span.ToString();
                continue;
            }

            if (section == "[Script Info]")
            {
                if (span.StartsWith("PlayResX:", StringComparison.Ordinal))
                {
                    lines[i] = "PlayResX: " + videoWidth.ToString(CultureInfo.InvariantCulture) + (hasCr ? "\r" : "");
                    playResXSeen = true;
                    changed = true;
                    continue;
                }

                if (span.StartsWith("PlayResY:", StringComparison.Ordinal))
                {
                    lines[i] = "PlayResY: " + videoHeight.ToString(CultureInfo.InvariantCulture) + (hasCr ? "\r" : "");
                    playResYSeen = true;
                    changed = true;
                    continue;
                }
            }
            else if (section is not null && section.StartsWith("[V4", StringComparison.Ordinal))
            {
                if (span.StartsWith("Style:", StringComparison.Ordinal))
                {
                    var name = ExtractStyleName(span);
                    lines[i] = BuildStyleLine(name, style.FontFamily, fontsize, primary, outline, bold, italic, style.Alignment, marginH, marginV) + (hasCr ? "\r" : "");
                    changed = true;
                    continue;
                }
            }
        }

        // Edge case: [Script Info] was the last section (no section break triggered insertion above).
        if (section == "[Script Info]" && (!playResXSeen || !playResYSeen))
        {
            if (!playResXSeen)
            {
                lines.Add("PlayResX: " + videoWidth.ToString(CultureInfo.InvariantCulture));
            }

            if (!playResYSeen)
            {
                lines.Add("PlayResY: " + videoHeight.ToString(CultureInfo.InvariantCulture));
            }

            changed = true;
        }

        return changed ? string.Join('\n', lines) : ass;
    }

    /// <summary>
    /// Walks <c>Dialogue:</c> and <c>Comment:</c> lines, applying <paramref name="pattern"/> to the Text
    /// field (everything after the ninth comma). The ASS header, style definitions, and non-dialogue
    /// lines are passed through untouched. Returns the original reference when no substitutions were made.
    /// </summary>
    private static string StripFromDialogueText(string ass, Regex pattern)
    {
        var lines = ass.Split('\n');
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var span = line.AsSpan();

            if (span.Length > 0 && span[^1] == '\r')
            {
                span = span[..^1];
            }

            if (!span.StartsWith("Dialogue:", StringComparison.Ordinal)
                && !span.StartsWith("Comment:", StringComparison.Ordinal))
            {
                continue;
            }

            var textStart = IndexOfNthComma(line, FieldsBeforeText);
            if (textStart < 0 || textStart >= line.Length)
            {
                continue;
            }

            var prefix = line.Substring(0, textStart);
            var text = line.Substring(textStart);

            var cleaned = pattern.Replace(text, string.Empty);
            if (!ReferenceEquals(cleaned, text))
            {
                lines[i] = prefix + cleaned;
                changed = true;
            }
        }

        return changed ? string.Join('\n', lines) : ass;
    }

    /// <summary>
    /// Converts an HTML hex colour (<c>#RRGGBB</c>) to the ASS BGR format (<c>&amp;H00BBGGRR&amp;</c>).
    /// Returns white on malformed input as a defensive fallback.
    /// </summary>
    private static string HtmlToAssColour(string html)
    {
        if (html.Length != 7 || html[0] != '#')
        {
            return "&H00FFFFFF&";
        }

        var r = html.Substring(1, 2);
        var g = html.Substring(3, 2);
        var b = html.Substring(5, 2);
        return "&H00" + b + g + r + "&";
    }

    /// <summary>Extracts the style name from a <c>Style:</c> line (the first field after the colon).</summary>
    private static string ExtractStyleName(ReadOnlySpan<char> styleLine)
    {
        var colon = styleLine.IndexOf(':');
        if (colon < 0)
        {
            return "Default";
        }

        var rest = styleLine[(colon + 1)..].TrimStart(' ');
        var comma = rest.IndexOf(',');
        return comma >= 0 ? rest[..comma].ToString() : (rest.IsEmpty ? "Default" : rest.ToString());
    }

    /// <summary>
    /// Builds a complete ASS V4+ <c>Style:</c> line with 23 fields. Non-exposed fields (outline width,
    /// shadow, secondary colour, horizontal margins) use sensible defaults.
    /// </summary>
    private static string BuildStyleLine(
        string name, string fontname, int fontsize,
        string primaryAss, string outlineAss,
        int bold, int italic, int alignment, int marginH, int marginV)
        => string.Format(
            CultureInfo.InvariantCulture,
            "Style: {0},{1},{2},{3},{3},{4},&H00000000&,{5},{6},0,0,100,100,0,0,1,2,1,{7},{8},{8},{9},1",
            name,
            fontname,
            fontsize,
            primaryAss,
            outlineAss,
            bold,
            italic,
            alignment,
            marginH,
            marginV);

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
