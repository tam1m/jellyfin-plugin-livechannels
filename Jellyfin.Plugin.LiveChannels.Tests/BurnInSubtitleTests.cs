using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveChannels.Models;
using Jellyfin.Plugin.LiveChannels.Utilities;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.LiveChannels.Tests;

/// <summary>
/// Tests for <see cref="SubtitleSelector.FindBurnInSubtitle"/>: the comparer-chain pipeline selects the correct
/// subtitle track for every mode, language preference, SDH setting, and source preference combination.
/// </summary>
public class BurnInSubtitleTests
{
    private static ProgramEntry Program(params SubtitleStreamInfo[] subtitles)
        => new(Guid.NewGuid(), "Item", null, 10_000_000L, "/media/item.mkv")
        {
            Subtitles = subtitles,
            DefaultAudioLanguage = "eng"
        };

    private static SubtitleStreamInfo Sub(int index, string lang = "", bool text = true, bool forced = false,
        bool isDefault = false, bool external = false, bool sdh = false)
        => new()
        {
            RelativeIndex = index,
            AbsoluteIndex = index + 2,
            IsText = text,
            IsForced = forced,
            IsDefault = isDefault,
            IsExternal = external,
            Language = lang,
            IsHearingImpaired = sdh
        };

    private static (int, bool)? Call(ProgramEntry p, SubtitleBurnInMode mode, string prefs = "",
        bool preferNonSdh = false, SubtitleSourcePreference src = SubtitleSourcePreference.Auto)
        => SubtitleSelector.FindBurnInSubtitle(p, mode, prefs, preferNonSdh, src);

    // ── Always mode ──────────────────────────────────────────────

    [Fact]
    public void Always_NonForcedBeatsForced()
    {
        var r = Call(Program(Sub(0, "jpn", isDefault: true), Sub(1, "eng", forced: true)),
            SubtitleBurnInMode.Always, "jpn,eng");
        Assert.Equal(0, r!.Value.Item1);
    }

    [Fact]
    public void Always_ForcedFallback_WhenNoNonForced()
    {
        var r = Call(Program(Sub(0, "eng", forced: true), Sub(1, "fre")),
            SubtitleBurnInMode.Always, "eng");
        Assert.Equal(0, r!.Value.Item1);
    }

    [Fact]
    public void Always_FiltersNonMatching_ReturnsNull()
    {
        var r = Call(Program(Sub(0, "jpn", isDefault: true), Sub(1, "fre")),
            SubtitleBurnInMode.Always, "eng");
        Assert.Null(r);
    }

    [Fact]
    public void Always_UntaggedWildcard_PassesFilter_SortsLast()
    {
        var r = Call(Program(Sub(0, "", isDefault: true), Sub(1, "eng")),
            SubtitleBurnInMode.Always, "eng");
        Assert.Equal(1, r!.Value.Item1);
    }

    [Fact]
    public void Always_UntaggedOnlyCandidate_Selected()
    {
        var r = Call(Program(Sub(0, ""), Sub(1, "fre")),
            SubtitleBurnInMode.Always, "eng");
        Assert.Equal(0, r!.Value.Item1);
    }

    [Fact]
    public void Always_EmptyPrefs_PreservesOldBehavior()
    {
        var r = Call(Program(Sub(0, "jpn", isDefault: true), Sub(1, "eng", forced: true)),
            SubtitleBurnInMode.Always);
        Assert.Equal(1, r!.Value.Item1);
    }

    // ── Forced mode ──────────────────────────────────────────────

    [Fact]
    public void Forced_FiltersToForcedOnly()
    {
        var r = Call(Program(Sub(0, "jpn", isDefault: true), Sub(1, "eng", forced: true)),
            SubtitleBurnInMode.Forced, "eng,jpn");
        Assert.Equal(1, r!.Value.Item1);
    }

    [Fact]
    public void Forced_NoMatch_ReturnsNull()
    {
        var r = Call(Program(Sub(0, "eng", forced: true)),
            SubtitleBurnInMode.Forced, "jpn");
        Assert.Null(r);
    }

    [Fact]
    public void Forced_UntaggedForced_PassesAsWildcard()
    {
        var r = Call(Program(Sub(0, "", forced: true), Sub(1, "fre", forced: true)),
            SubtitleBurnInMode.Forced, "eng");
        Assert.Equal(0, r!.Value.Item1);
    }

    [Fact]
    public void Forced_EmptyPrefs_PreservesOldBehavior()
    {
        var r = Call(Program(Sub(0, "jpn", isDefault: true), Sub(1, "eng", forced: true),
            Sub(2, "fre", forced: true)), SubtitleBurnInMode.Forced);
        Assert.Equal(1, r!.Value.Item1);
    }

    // ── Default mode ─────────────────────────────────────────────

    [Fact]
    public void Default_ForcedBeatsDefault_RegardlessOfLanguage()
    {
        var r = Call(Program(Sub(0, "jpn", isDefault: true), Sub(1, "eng", forced: true)),
            SubtitleBurnInMode.Default, "jpn,eng");
        Assert.Equal(1, r!.Value.Item1);
    }

    [Fact]
    public void Default_LanguageBreaksFlagTie()
    {
        var r = Call(Program(Sub(0, "jpn", isDefault: true), Sub(1, "eng", isDefault: true)),
            SubtitleBurnInMode.Default, "jpn,eng");
        Assert.Equal(0, r!.Value.Item1);
    }

    [Fact]
    public void Default_ForcedOverridesLanguage()
    {
        var r = Call(Program(Sub(0, "jpn", forced: true), Sub(1, "eng", isDefault: true)),
            SubtitleBurnInMode.Default, "eng");
        Assert.Equal(0, r!.Value.Item1);
    }

    [Fact]
    public void Default_NoFlags_LanguageDecides()
    {
        var r = Call(Program(Sub(0, "jpn"), Sub(1, "eng")),
            SubtitleBurnInMode.Default, "eng");
        Assert.Equal(1, r!.Value.Item1);
    }

    [Fact]
    public void Default_EmptyPrefs_PreservesOldBehavior()
    {
        var r = Call(Program(Sub(0, "jpn", isDefault: true), Sub(1, "eng", forced: true)),
            SubtitleBurnInMode.Default);
        Assert.Equal(1, r!.Value.Item1);
    }

    // ── Never mode ───────────────────────────────────────────────

    [Fact]
    public void Never_ReturnsNull()
    {
        var r = Call(Program(Sub(0, "eng", forced: true)),
            SubtitleBurnInMode.Never, "eng");
        Assert.Null(r);
    }

    // ── Normalization ────────────────────────────────────────────

    [Fact]
    public void Normalize_TwoLetter_MatchesThreeLetter()
    {
        var r = Call(Program(Sub(0, "en")),
            SubtitleBurnInMode.Always, "eng");
        Assert.NotNull(r);
    }

    [Fact]
    public void Normalize_UnknownCode_PassesThrough()
    {
        var r = Call(Program(Sub(0, "xyz")),
            SubtitleBurnInMode.Always, "xyz");
        Assert.NotNull(r);
    }

    [Fact]
    public void CaseInsensitive()
    {
        var r = Call(Program(Sub(0, "ENG")),
            SubtitleBurnInMode.Always, "eng");
        Assert.NotNull(r);
    }

    // ── SDH preference ───────────────────────────────────────────

    [Fact]
    public void Sdh_PreferNonSdh_NonSdhBeatsSdh_InSameLanguage()
    {
        var r = Call(Program(Sub(0, "eng", sdh: true), Sub(1, "eng")),
            SubtitleBurnInMode.Always, "eng", preferNonSdh: true);
        Assert.Equal(1, r!.Value.Item1);
    }

    [Fact]
    public void Sdh_PreferNonSdh_Off_IndexDecides()
    {
        var r = Call(Program(Sub(0, "eng", sdh: true), Sub(1, "eng")),
            SubtitleBurnInMode.Always, "eng", preferNonSdh: false);
        Assert.Equal(0, r!.Value.Item1);
    }

    [Fact]
    public void Sdh_LanguageBeatsSdh()
    {
        var r = Call(Program(Sub(0, "fre"), Sub(1, "eng", sdh: true)),
            SubtitleBurnInMode.Always, "eng", preferNonSdh: true);
        Assert.Equal(1, r!.Value.Item1);
    }

    // ── Source preference ────────────────────────────────────────

    [Fact]
    public void Source_PreferExternal()
    {
        var r = Call(Program(Sub(0, "eng", external: true), Sub(1, "eng")),
            SubtitleBurnInMode.Always, "eng",
            src: SubtitleSourcePreference.PreferExternal);
        Assert.Equal(0, r!.Value.Item1);
    }

    [Fact]
    public void Source_PreferInternal()
    {
        var r = Call(Program(Sub(0, "eng", external: true), Sub(1, "eng")),
            SubtitleBurnInMode.Always, "eng",
            src: SubtitleSourcePreference.PreferInternal);
        Assert.Equal(1, r!.Value.Item1);
    }

    [Fact]
    public void Source_Auto_IndexDecides()
    {
        var r = Call(Program(Sub(0, "eng", external: true), Sub(1, "eng")),
            SubtitleBurnInMode.Always, "eng",
            src: SubtitleSourcePreference.Auto);
        Assert.Equal(0, r!.Value.Item1);
    }

    // ── Logging ─────────────────────────────────────────────────

    [Fact]
    public void Logger_LogsDecisionChain_OnSelection()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        SubtitleSelector.FindBurnInSubtitle(
            Program(Sub(0, "jpn", isDefault: true), Sub(1, "eng", forced: true)),
            SubtitleBurnInMode.Always, "jpn,eng", false, SubtitleSourcePreference.Auto, logger);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("[Subtitle] \"Item\" → Always(jpn,eng,anysdh,auto)")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void Logger_NoLog_WhenDebugDisabled()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(false);

        SubtitleSelector.FindBurnInSubtitle(
            Program(Sub(0, "eng")),
            SubtitleBurnInMode.Always, "eng", false, SubtitleSourcePreference.Auto, logger);

        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void Logger_LogsSkipped_WhenNoTracks()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        SubtitleSelector.FindBurnInSubtitle(
            Program(),
            SubtitleBurnInMode.Always, "", false, SubtitleSourcePreference.Auto, logger);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("skipped")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
