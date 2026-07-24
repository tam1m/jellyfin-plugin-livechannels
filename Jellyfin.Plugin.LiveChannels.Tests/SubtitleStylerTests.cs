using System;
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
}
