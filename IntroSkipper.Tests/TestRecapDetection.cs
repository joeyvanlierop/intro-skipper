// SPDX-FileCopyrightText: 2024-2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.ScheduledTasks;
using Xunit;

namespace IntroSkipper.Tests;

public class TestRecapDetection
{
    private static readonly Guid EpisodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const int MaxRecapDuration = 120;

    // Two shared regions exist near the episode start: a short early one (the
    // "Previously on" card, ~6s) and a longer later one (the intro, ~25s).
    private static (List<TimeRange> Lhs, List<TimeRange> Rhs) TwoSharedRegions()
    {
        var lhs = new List<TimeRange> { new(10, 16), new(40, 65) };
        var rhs = new List<TimeRange> { new(11, 17), new(41, 66) };
        return (lhs, rhs);
    }

    [Fact]
    public void SelectSharedRegion_Introduction_PicksLongestRegion()
    {
        var (lhs, rhs) = TwoSharedRegions();

        var (lhsSeg, _) = ChromaprintAnalyzer.SelectSharedRegion(
            Guid.NewGuid(), lhs, Guid.NewGuid(), rhs, AnalysisMode.Introduction);

        Assert.Equal(40, lhsSeg.Start);
        Assert.Equal(65, lhsSeg.End);
    }

    [Fact]
    public void SelectSharedRegion_Recap_PicksEarliestRegion()
    {
        var (lhs, rhs) = TwoSharedRegions();

        var (lhsSeg, rhsSeg) = ChromaprintAnalyzer.SelectSharedRegion(
            Guid.NewGuid(), lhs, Guid.NewGuid(), rhs, AnalysisMode.Recap);

        // The earliest region (the card) is chosen, not the longer intro,
        // and its paired rhs region is returned at the same index.
        Assert.Equal(10, lhsSeg.Start);
        Assert.Equal(16, lhsSeg.End);
        Assert.Equal(11, rhsSeg.Start);
        Assert.Equal(17, rhsSeg.End);
    }

    [Fact]
    public void GetMinimumRegionDuration_Recap_AllowsShortCard()
    {
        // The "Previously on" card is only a few seconds, so Recap must use a much smaller
        // floor than MinimumIntroDuration, which would otherwise discard it.
        var recapFloor = ChromaprintAnalyzer.GetMinimumRegionDuration(AnalysisMode.Recap, 15);

        Assert.True(recapFloor < 15, "Recap floor must be smaller than MinimumIntroDuration");
        Assert.True(recapFloor <= 5, "Recap floor must admit a few-second card");
    }

    [Fact]
    public void GetMinimumRegionDuration_Introduction_UsesConfiguredMinimum()
    {
        var introFloor = ChromaprintAnalyzer.GetMinimumRegionDuration(AnalysisMode.Introduction, 15);

        Assert.Equal(15, introFloor);
    }

    [Fact]
    public void ComputeRecapFromCard_ExtendsCardEndToIntroStart()
    {
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Recap] = new Segment(EpisodeId, new TimeRange(0, 6)),
            [AnalysisMode.Introduction] = new Segment(EpisodeId, new TimeRange(70, 95)),
        };

        var result = BaseItemAnalyzerTask.ComputeRecapFromCard(EpisodeId, timestamps, MaxRecapDuration);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Start);
        Assert.Equal(70, result.End);
    }

    [Fact]
    public void ComputeRecapFromCard_ClampsToMaximumRecapDuration()
    {
        // Intro is far away (e.g. a long cold open sits between recap and intro); clamp to the max.
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Recap] = new Segment(EpisodeId, new TimeRange(0, 6)),
            [AnalysisMode.Introduction] = new Segment(EpisodeId, new TimeRange(200, 230)),
        };

        var result = BaseItemAnalyzerTask.ComputeRecapFromCard(EpisodeId, timestamps, MaxRecapDuration);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Start);
        Assert.Equal(120, result.End);
    }

    [Fact]
    public void ComputeRecapFromCard_NoIntro_ReturnsNull()
    {
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Recap] = new Segment(EpisodeId, new TimeRange(0, 6)),
        };

        Assert.Null(BaseItemAnalyzerTask.ComputeRecapFromCard(EpisodeId, timestamps, MaxRecapDuration));
    }

    [Fact]
    public void ComputeRecapFromCard_NoCard_ReturnsNull()
    {
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Introduction] = new Segment(EpisodeId, new TimeRange(70, 95)),
        };

        Assert.Null(BaseItemAnalyzerTask.ComputeRecapFromCard(EpisodeId, timestamps, MaxRecapDuration));
    }

    [Fact]
    public void ComputeRecapFromCard_IntroBeforeCard_ReturnsNull()
    {
        // Defensive: an intro that starts before the card means the anchor is meaningless.
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Recap] = new Segment(EpisodeId, new TimeRange(50, 56)),
            [AnalysisMode.Introduction] = new Segment(EpisodeId, new TimeRange(10, 35)),
        };

        Assert.Null(BaseItemAnalyzerTask.ComputeRecapFromCard(EpisodeId, timestamps, MaxRecapDuration));
    }

    [Fact]
    public void ComputeRecapFromCard_AlreadyExtended_ReturnsNull()
    {
        // Recap already spans card start to intro start; re-running must not rewrite it.
        var timestamps = new Dictionary<AnalysisMode, Segment>
        {
            [AnalysisMode.Recap] = new Segment(EpisodeId, new TimeRange(0, 70)),
            [AnalysisMode.Introduction] = new Segment(EpisodeId, new TimeRange(70, 95)),
        };

        Assert.Null(BaseItemAnalyzerTask.ComputeRecapFromCard(EpisodeId, timestamps, MaxRecapDuration));
    }

    [Fact]
    public void RecapHash_ChangesWhenChromaprintTuningChanges()
    {
        // Recap now relies on the chromaprint matcher, so changing its tuning must invalidate the
        // stored analysis (force re-detection), the same as Introduction.
        var baseline = new PluginConfiguration();
        var tuned = new PluginConfiguration
        {
            MaximumFingerprintPointDifferences = baseline.MaximumFingerprintPointDifferences + 1,
        };

        var hashBaseline = ConfigHasher.Analysis(baseline, AnalysisMode.Recap, AnalyzerAction.Default);
        var hashTuned = ConfigHasher.Analysis(tuned, AnalysisMode.Recap, AnalyzerAction.Default);

        Assert.NotEqual(hashBaseline, hashTuned);
    }
}
