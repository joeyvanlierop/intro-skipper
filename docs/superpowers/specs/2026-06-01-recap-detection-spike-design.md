# Recap detection spike — design

**Date:** 2026-06-01
**Status:** Approved (spike / experiment)
**Branch:** `worktree-recap-detection-spike` (fork: `joeyvanlierop/intro-skipper`)

## Goal

Validate, with minimal code, that a TV recap ("Previously on …") can be detected by
combining the existing cross-episode chromaprint matcher with the already-detected intro
position. This is a throwaway-quality spike to prove the signal before investing in a
robust, general implementation.

## Background

`Recap` is already a first-class `AnalysisMode`, plumbed end to end (config toggle
`ScanRecap`, bounds `MinimumRecapDuration`/`MaximumRecapDuration`, storage, and mapping to
`MediaSegmentType.Recap` in `SegmentProvider`). The **only** missing piece is detection:
today the Recap pipeline runs *only* `ChapterAnalyzer`
(`BaseItemAnalyzerTask.cs` — `// Recap, Preview, Commercial: only ChapterAnalyzer`), so
recaps are found only when a chapter marker is literally named "Recap".

### Key insight

Intros are byte-identical across episodes, so the cross-episode chromaprint matcher finds
them. Recaps are **not** — the recap body is clips from previous episodes and differs every
episode. What *is* consistent across episodes is the **"Previously on" card** (confirmed:
the target show has a consistent musical sting under the card). So chromaprint anchors the
*card*, not the recap body. The black frames give structure; the already-detected intro
gives a convenient end anchor.

Target show structure: `black → "Previously on" card → black → recap body → black → intro`.
On some episodes a cold open may sit between recap and intro, or the recap may be absent.

## Approach

Reuse the existing matcher to find the short common region near the episode start (the
card) → emit `recap = [card start, intro start]`, clamped to `MaximumRecapDuration`.
Chromaprint presence **is** the gate: no card match → no recap (this is what keeps cold
opens from being mislabeled).

## Changes (4 touch-points)

1. **`FFmpegWrapper.GetFingerprintRange`** — add `AnalysisMode.Recap => (0,
   episode.IntroFingerprintEnd)` (fingerprint the start window, same as Introduction; the
   card lives in the first seconds). Currently throws for Recap.

2. **`ChromaprintAnalyzer` — Recap-mode selection.** Two current behaviors fight us:
   - `FindContiguous` discards matches shorter than `MinimumIntroDuration` (15s); the card
     is ~a few seconds. → For Recap, thread in a small floor (~3s) so the card survives.
   - `GetLongestTimeRange` returns the *longest* region (the intro). → For Recap, select
     the *earliest* qualifying region (smallest `Start`) = the card.

3. **Wire Recap into the pipeline** (`BaseItemAnalyzerTask`): add `ChromaprintAnalyzer` to
   the Recap branch (non-movie, ffmpeg valid), after `ChapterAnalyzer`.

4. **Crude end extension — post-step** (mirrors `CreateAnimePreviewFromCredits`): for each
   episode with a detected card + a detected intro, set
   `recap.End = min(intro.Start, recap.Start + MaximumRecapDuration)`. Chromaprint writes
   the card region; this step widens it. Make `modes` deterministically ordered so
   Introduction precedes Recap; skip the extension gracefully when no intro is present.

## Known limitations (accepted for the spike)

- Over-extends into a cold open when one sits between recap and intro. Refine later with a
  black-frame end boundary.
- If leading black/silence yields a spurious *earlier* common match than the card,
  earliest-selection could grab it. Fallback: skip the first ~1–2s or raise the floor. The
  experiment will reveal whether this happens.

## Validation

- Unit test the earliest-short-region selection in `ChromaprintAnalyzer` (in-memory,
  synthetic fingerprints — no ffmpeg dependency), mirroring existing `CompareEpisodes`
  tests.
- Empirical: run analysis against the target show, inspect detected Recap segments in the
  segment editor.

## Out of scope (future, if the spike pans out)

- Black-frame-derived recap end boundary (correct cold-open handling).
- Generalization / configurability for arbitrary shows.
- New config knobs beyond reusing existing Recap bounds.
