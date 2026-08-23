# Pending tests

These test files came across in the Stage 0 port and are excluded from the test project's compile,
**not deleted**. Every one of them fails to compile for the same immediate reason: they call
an external `GameIntegrationService`, a static service that Tript never ported. Three of them
also want `Tript.Core.AppState.Instance.Recording` and a mutable `Recording` with a `Bookmarks` list;
Tript's equivalent is `RecordingSessionRegistry` + the `IRecordingSession` interface, which hands out
no list to read back — a port supplies a fake session instead, as `DetectionHostTests` does.

What is behind that service differs per file, and so does how much of it Tript still owes. It is
**not** true that all of this is waiting on one Stage 2 subsystem: the detection host itself shipped,
as `Tript.Recorder.DetectionHost`, with per-run instances in place of the service's mutable statics.

| File | What it actually needs | Status |
| --- | --- | --- |
| `ModelIdResolutionTests.cs` | `GameIntegrationService.ResolveModelId(igdbId, displayName)` — an IGDB-id → model-id catalogue with a canonical spelling — and `IsMlDetectionEnabled(modelId, GameIntegrations)`. | Genuinely pending. Neither exists: `DetectionHost.Start` is handed a raw game id, and the settings model's per-game toggle (`GameSetting.Integrations.Enabled`) is written but read by nothing. Its one casing assertion is already covered by the active `../ModelPathCasingTests.cs`. |
| `ModelPathCasingTests.cs` (class `ModelPathCasingIntegrationTests`) | `GameIntegrationService.ResolveModelId` only — the name-only resolution path, end to end onto a shipped model. | Genuinely pending, on exactly the same piece as the file above. The rest of this file's tests run in `../ModelPathCasingTests.cs`. |
| `ExclusionSuppressionTests.cs` | A batch-level exclusion pass in `HandleDetections`: one `EventType.Exclusion` detection vetoes every trigger in the same cycle, before any of them reaches the cooldown tracker. | Genuinely pending, and pending on **behaviour**, not just on a host to put it in. `DetectionHost.DetectionRun.OnDetections` feeds every defined class straight to `CooldownTracker`; an exclusion definition is treated as a trigger and merely happens not to bookmark, because it carries no `BookmarkType`. `ExclusionDetection_NeverBookmarksItself` — an exclusion that *does* carry one — would fail against shipped code today. |
| `DetectionSessionTests.cs` | `GameIntegrationService.DetectionSession`, `HandleDetections` and `Shutdown` as statics, plus the same exclusion pass. | Partly superseded. The concurrency test pins a race between a detection cycle and a teardown that clears three separate statics; `DetectionHost` has no such statics — a run is immutable once created and an in-flight handler keeps reading the run it captured — so that shape cannot occur. What is left that Tript does not have is the exclusion pass, which is `ExclusionSuppressionTests`' subject. |
| `DetectorTeardownOnGameSwitchTests.cs` | `GameIntegrationService.Start`/`Shutdown`, plus reflection over its `_detectionSession` static and `VisualEventDetector._cts`. | Superseded. The guarantee it pins — a `Start` that declines still tears the previous game's detector down — ships in `DetectionHost.Start`, which stops the old run before it decides whether to begin a new one. It is now pinned by `DetectionHostTests`. Only the "no game name" entry path is left here, and it has no counterpart: `DetectionHost.Start` rejects an empty game id outright. |

Two tests were split out into the active suites, against the shipped API, because the guards they
covered ship today and had no other test:

| Was | Now |
| --- | --- |
| `DetectionSessionTests.HandleDetections_IgnoresClassIdsWithNoDefinition` | `Tript.Recorder.Tests/DetectionHostTests.Detections_WithNoDefinitionForTheirClass_AreDropped` |
| `DetectorTeardownOnGameSwitchTests.Start_ForAGameWithNoModelOnDisk_StopsThePreviousDetector` | `Tript.Recorder.Tests/DetectionHostTests.Start_ForAGameItRefuses_StillStopsThePreviousDetector` |

What is left here is still the record of what Tript owes the detector — the model-id catalogue, the
per-game ML toggle, and the exclusion pass. Re-enable them against whatever lands; do not rewrite
them from scratch.
