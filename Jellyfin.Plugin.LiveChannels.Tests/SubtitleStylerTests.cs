using System;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="SubtitleStyler"/> — the HTML-tag stripping that prevents burned-in
/// subtitles from showing markup like <c>&lt;i&gt;</c> or <c>&lt;font&gt;</c> as literal text.
/// </summary>
public class SubtitleStylerTests
{
    /// <summary>A minimal valid ASS header used by every test so the Dialogue lines are realistic.</summary>
    private const string Header = """
        [Script Info]
        Title: Test
        ScriptType: v4.00+
        PlayResX: 1920
        PlayResY: 1080

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,1,2,40,40,60,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text

        """;

    /// <summary>Builds a full ASS document from the given Dialogue lines.</summary>
    private static string Ass(params string[] dialogues)
        => Header + string.Join('\n', dialogues) + '\n';

    /// <summary>Extracts the Text field (after the 9th comma) from the first Dialogue line.</summary>
    private static string TextOf(string ass)
    {
        var lines = ass.Split('\n');
        foreach (var line in lines)
        {
            if (!line.StartsWith("Dialogue:", StringComparison.Ordinal))
            {
                continue;
            }

            var commas = 0;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == ',' && ++commas == 9)
                {
                    return line[(i + 1)..];
                }
            }
        }

        return string.Empty;
    }

    [Fact]
    public void StripsItalics()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<i>Hello</i> world"));
        Assert.Equal("Hello world", TextOf(cleaned));
    }

    [Fact]
    public void StripsBold()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<b>Bold</b> text"));
        Assert.Equal("Bold text", TextOf(cleaned));
    }

    [Fact]
    public void StripsUnderline()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<u>underlined</u>"));
        Assert.Equal("underlined", TextOf(cleaned));
    }

    [Fact]
    public void StripsFontTagWithAttributes()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<font color=\"#FF0000\">Red</font>"));
        Assert.Equal("Red", TextOf(cleaned));
    }

    [Fact]
    public void StripsNestedTags()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<b><i>both</i></b>"));
        Assert.Equal("both", TextOf(cleaned));
    }

    [Fact]
    public void StripsUnknownTag()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<custom>text</custom>"));
        Assert.Equal("text", TextOf(cleaned));
    }

    [Fact]
    public void StripsMultipleTagsInOneLine()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<i>a</i> and <b>b</b>"));
        Assert.Equal("a and b", TextOf(cleaned));
    }

    [Fact]
    public void StripsAllTagsAcrossMultipleLines()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,<i>first</i>",
            "Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,<b>second</b>"));
        var lines = cleaned.Split('\n');
        var dialogues = Array.FindAll(lines, l => l.StartsWith("Dialogue:", StringComparison.Ordinal));
        Assert.Equal(2, dialogues.Length);
        Assert.EndsWith(",first", dialogues[0]);
        Assert.EndsWith(",second", dialogues[1]);
    }

    [Fact]
    public void PreservesAssOverrideBlocks()
    {
        // ASS overrides ({\i1}...{\i0}) are valid ASS and must survive — only HTML tags are stripped.
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\i1}Hello{\\i0} world"));
        Assert.Equal("{\\i1}Hello{\\i0} world", TextOf(cleaned));
    }

    [Fact]
    public void PreservesAssPositionOverride()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\an8}Top text"));
        Assert.Equal("{\\an8}Top text", TextOf(cleaned));
    }

    [Fact]
    public void PreservesPlainText()
    {
        var input = Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,Just plain text");
        var cleaned = SubtitleStyler.CleanAss(input);
        Assert.Equal("Just plain text", TextOf(cleaned));
    }

    [Fact]
    public void ReturnsInputUnchanged_WhenNoTagsFound()
    {
        var input = Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,No tags here");
        var cleaned = SubtitleStyler.CleanAss(input);
        Assert.Equal(input, cleaned);
    }

    [Fact]
    public void PreservesHeader()
    {
        var input = Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<i>x</i>");
        var cleaned = SubtitleStyler.CleanAss(input);
        Assert.Contains("[Script Info]", cleaned);
        Assert.Contains("[V4+ Styles]", cleaned);
        Assert.Contains("Style: Default,Arial,48", cleaned);
        Assert.Contains("Format: Layer, Start, End, Style, Name", cleaned);
    }

    [Fact]
    public void DoesNotTouchStyleField()
    {
        // A tag-looking fragment in the Style field (before the 9th comma) must be preserved.
        // The Style name here is "<b>" — unusual, but it proves the cleaner only touches Text.
        var input = Ass("Dialogue: 0,0:00:01.00,0:00:05.00,<b>,,0,0,0,,real text");
        var cleaned = SubtitleStyler.CleanAss(input);
        // The <b> in the Style field (before the Text boundary) must survive.
        Assert.Contains("0:00:05.00,<b>,", cleaned);
        // The Text field is "real text" and should be unmodified.
        Assert.Equal("real text", TextOf(cleaned));
    }

    [Fact]
    public void HandlesCommentLines()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Comment: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<i>note</i>"));
        // Comment lines are also cleaned (they can appear in burned output via some tools).
        var lines = cleaned.Split('\n');
        var comment = Array.Find(lines, l => l.StartsWith("Comment:", StringComparison.Ordinal));
        Assert.NotNull(comment);
        Assert.EndsWith(",note", comment);
    }

    [Fact]
    public void HandlesMixedHtmlAndAssOverrides()
    {
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\i1}<b>bold italic</b>{\\i0}"));
        Assert.Equal("{\\i1}bold italic{\\i0}", TextOf(cleaned));
    }

    [Fact]
    public void HandlesCarriageReturnLineEndings()
    {
        var input = Header.Replace("\n", "\r\n")
            + "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,<i>Hello</i>\r\n";
        var cleaned = SubtitleStyler.CleanAss(input);
        Assert.Contains("Hello", cleaned);
        Assert.DoesNotContain("<i>", cleaned);
    }

    [Fact]
    public void DoesNotStripBareAngleBrackets()
    {
        // "3 < 5" should survive — the < is not followed by a letter.
        var cleaned = SubtitleStyler.CleanAss(Ass(
            "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,3 < 5 is true"));
        Assert.Equal("3 < 5 is true", TextOf(cleaned));
    }

    [Fact]
    public void SkipsMalformedDialogueWithTooFewFields()
    {
        // A Dialogue line missing commas (malformed) should pass through untouched, not crash.
        var input = Ass("Dialogue: broken line with no commas");
        var cleaned = SubtitleStyler.CleanAss(input);
        Assert.Contains("Dialogue: broken line with no commas", cleaned);
    }

    [Fact]
    public void NullInput_Throws()
        => Assert.Throws<ArgumentNullException>(() => SubtitleStyler.CleanAss(null!));

    [Fact]
    public void EmptyInput_ReturnsEmpty()
        => Assert.Equal(string.Empty, SubtitleStyler.CleanAss(string.Empty));

    // --- StyleAss tests ---

    /// <summary>A default enabled style used by most StyleAss tests.</summary>
    private static SubtitleStyle Style() => new()
    {
        Enabled = true,
        FontFamily = "DejaVu Sans",
        FontSizePercent = 4,
        PrimaryColour = "#FFFFFF",
        OutlineColour = "#000000",
        Alignment = 2,
        MarginVerticalPercent = 6
    };

    /// <summary>Splits the first matching Style line into its comma-separated fields (Name is [0]).</summary>
    private static string[] StyleFields(string ass, string name = "Default")
    {
        foreach (var line in ass.Split('\n'))
        {
            var t = line.Trim('\r');
            if (t.StartsWith("Style:", StringComparison.Ordinal) && t.Contains(name + ",", StringComparison.Ordinal))
            {
                var c = t.IndexOf(':');
                return t[(c + 1)..].Trim().Split(',');
            }
        }

        return Array.Empty<string>();
    }

    private static string PlayRes(string ass, string key)
    {
        foreach (var line in ass.Split('\n'))
        {
            var t = line.Trim('\r');
            if (t.StartsWith(key + ":", StringComparison.Ordinal))
            {
                return t[(t.IndexOf(':') + 1)..].Trim();
            }
        }

        return null!;
    }

    [Fact]
    public void StyleAss_RewritesPlayResY()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), Style(), 1920, 1080);
        Assert.Equal("1080", PlayRes(styled, "PlayResY"));
    }

    [Fact]
    public void StyleAss_RewritesPlayResX()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), Style(), 1920, 1080);
        Assert.Equal("1920", PlayRes(styled, "PlayResX"));
    }

    [Fact]
    public void StyleAss_InsertsPlayRes_WhenMissing()
    {
        // An ASS with no PlayResX/Y in [Script Info].
        var noRes = "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname\nStyle: Default,Arial\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text\n";
        var styled = SubtitleStyler.StyleAss(noRes, Style(), 1920, 1080);
        Assert.Equal("1920", PlayRes(styled, "PlayResX"));
        Assert.Equal("1080", PlayRes(styled, "PlayResY"));
    }

    [Fact]
    public void StyleAss_FontSizeIsPercentOfHeight()
    {
        // 4% of 1080 = 43 (rounded)
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), Style(), 1920, 1080);
        Assert.Equal("43", StyleFields(styled)[2]);
    }

    [Fact]
    public void StyleAss_FontNameAppearsInStyleLine()
    {
        var style = Style();
        style.FontFamily = "Liberation Sans";
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), style, 1920, 1080);
        Assert.Equal("Liberation Sans", StyleFields(styled)[1]);
    }

    [Fact]
    public void StyleAss_EmptyFontNamePassesThrough()
    {
        var style = Style();
        style.FontFamily = "";
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), style, 1920, 1080);
        Assert.Equal("", StyleFields(styled)[1]);
    }

    [Fact]
    public void StyleAss_PrimaryColour_White()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), Style(), 1920, 1080);
        Assert.Equal("&H00FFFFFF&", StyleFields(styled)[3]);
    }

    [Fact]
    public void StyleAss_PrimaryColour_Red_BgrSwap()
    {
        var style = Style();
        style.PrimaryColour = "#FF0000"; // red
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), style, 1920, 1080);
        Assert.Equal("&H000000FF&", StyleFields(styled)[3]);
    }

    [Fact]
    public void StyleAss_OutlineColour_Black()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), Style(), 1920, 1080);
        Assert.Equal("&H00000000&", StyleFields(styled)[5]);
    }

    [Fact]
    public void StyleAss_BoldTrue_SetsMinus1()
    {
        var style = Style();
        style.Bold = true;
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), style, 1920, 1080);
        Assert.Equal("-1", StyleFields(styled)[7]);
    }

    [Fact]
    public void StyleAss_BoldFalse_SetsZero()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), Style(), 1920, 1080);
        Assert.Equal("0", StyleFields(styled)[7]);
    }

    [Fact]
    public void StyleAss_ItalicTrue_SetsMinus1()
    {
        var style = Style();
        style.Italic = true;
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:01.00,0:00:05.00,Default,,0,0,0,,text"), style, 1920, 1080);
        Assert.Equal("-1", StyleFields(styled)[8]);
    }

    [Fact]
    public void StyleAss_Alignment_SetsAssAlignment()
    {
        var style = Style();
        style.Alignment = 8; // top-center
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), style, 1920, 1080);
        Assert.Equal("8", StyleFields(styled)[18]);
    }

    [Fact]
    public void StyleAss_MarginV_PercentToPixels()
    {
        // 6% of 1080 = 65 (rounded)
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), Style(), 1920, 1080);
        Assert.Equal("65", StyleFields(styled)[21]);
    }

    [Fact]
    public void StyleAss_RewritesAllStyles_ToIdenticalValues()
    {
        // Two styles with different fonts in [V4+ Styles] — both should end up with the same values.
        var input = "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,1,2,40,40,60,1\nStyle: Signs,Comic Sans,99,&H0000FFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,1,7,10,10,20,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text\n";
        var styled = SubtitleStyler.StyleAss(input, Style(), 1920, 1080);
        var def = StyleFields(styled, "Default");
        var signs = StyleFields(styled, "Signs");
        Assert.NotEmpty(def);
        Assert.NotEmpty(signs);
        // Same font size, colour, alignment — everything except the name.
        Assert.Equal(def[2], signs[2]); // fontsize
        Assert.Equal(def[3], signs[3]); // primary
        Assert.Equal(def[18], signs[18]); // alignment
    }

    [Fact]
    public void StyleAss_PreservesStyleNames()
    {
        var input = "[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,1,2,40,40,60,1\nStyle: Signs,Comic Sans,99,&H0000FFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,1,7,10,10,20,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text\n";
        var styled = SubtitleStyler.StyleAss(input, Style(), 1920, 1080);
        Assert.Equal("Default", StyleFields(styled, "Default")[0]);
        Assert.Equal("Signs", StyleFields(styled, "Signs")[0]);
    }

    [Fact]
    public void StyleAss_StripsAssOverrides()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\i1}Hello{\\i0} world"), Style(), 1920, 1080);
        Assert.Equal("Hello world", TextOf(styled));
    }

    [Fact]
    public void StyleAss_StripsPositionOverride()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\pos(100,200)}text"), Style(), 1920, 1080);
        Assert.Equal("text", TextOf(styled));
    }

    [Fact]
    public void StyleAss_StripsColourOverride()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\c&H0000FF&}text"), Style(), 1920, 1080);
        Assert.Equal("text", TextOf(styled));
    }

    [Fact]
    public void StyleAss_PreservesLineBreaks()
    {
        // \N is a line break escape, not inside {} — must survive.
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,Line1\\NLine2"), Style(), 1920, 1080);
        Assert.Equal("Line1\\NLine2", TextOf(styled));
    }

    [Fact]
    public void StyleAss_PreservesFormatLine()
    {
        var styled = SubtitleStyler.StyleAss(Ass("Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,text"), Style(), 1920, 1080);
        Assert.Contains("Format: Layer, Start, End, Style, Name", styled);
    }

    [Fact]
    public void StyleAss_HandlesNoStylesSection()
    {
        // An ASS with no [V4+ Styles] section — should not crash, just strip overrides and set PlayRes.
        var noStyles = "[Script Info]\nScriptType: v4.00+\nPlayResY: 288\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,{\\i1}text{\\i0}\n";
        var styled = SubtitleStyler.StyleAss(noStyles, Style(), 1920, 1080);
        Assert.Equal("1080", PlayRes(styled, "PlayResY"));
        Assert.DoesNotContain("{\\i1}", styled);
    }

    [Fact]
    public void StyleAss_NullStyle_Throws()
        => Assert.Throws<ArgumentNullException>(() => SubtitleStyler.StyleAss("x", null!, 1920, 1080));

    [Fact]
    public void StyleAss_NullInput_Throws()
        => Assert.Throws<ArgumentNullException>(() => SubtitleStyler.StyleAss(null!, Style(), 1920, 1080));
}
