using Jellyfin.Plugin.LiveChannels.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for language normalization and preference parsing in <see cref="SubtitleSelector"/>.
/// </summary>
public class SubtitleNormalizationTests
{
    [Theory]
    [InlineData("en", "eng")]
    [InlineData("EN", "eng")]
    [InlineData("En", "eng")]
    [InlineData("ja", "jpn")]
    [InlineData("de", "deu")]
    [InlineData("fr", "fra")]
    [InlineData("es", "spa")]
    [InlineData("zh", "zho")]
    public void NormalizeLanguage_ConvertsTwoLetterToThreeLetter(string input, string expected)
        => Assert.Equal(expected, SubtitleSelector.NormalizeLanguage(input));

    [Theory]
    [InlineData("eng")]
    [InlineData("jpn")]
    [InlineData("deu")]
    public void NormalizeLanguage_PassesThreeLetterThrough(string code)
        => Assert.Equal(code, SubtitleSelector.NormalizeLanguage(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeLanguage_EmptyOrNull_ReturnsEmpty(string? input)
        => Assert.Equal(string.Empty, SubtitleSelector.NormalizeLanguage(input));

    [Fact]
    public void NormalizeLanguage_UnknownCode_PassesThrough()
        => Assert.Equal("xyz", SubtitleSelector.NormalizeLanguage("xyz"));

    [Theory]
    [InlineData("eng,deu,jap", new[] { "eng", "deu", "jap" })]
    [InlineData("eng", new[] { "eng" })]
    [InlineData(" eng , deu ", new[] { "eng", "deu" })]
    [InlineData("ENG,Deu", new[] { "ENG", "Deu" })]
    public void ParseLanguageList_ParsesCommaSeparated(string input, string[] expected)
        => Assert.Equal(expected, SubtitleSelector.ParseLanguageList(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseLanguageList_EmptyOrWhitespace_ReturnsEmpty(string? input)
        => Assert.Empty(SubtitleSelector.ParseLanguageList(input));

    [Fact]
    public void ParseLanguageList_SkipsEmptyEntries()
    {
        var result = SubtitleSelector.ParseLanguageList("eng,,deu");
        Assert.Equal(new[] { "eng", "deu" }, result);
    }
}
