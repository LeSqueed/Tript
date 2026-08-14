# Pending tests

These test files came across in the Stage 0 port but cannot compile yet: they exercise the detector
through the **game-detection and integration subsystem**, which does not exist in Tript until Stage 2.

They are excluded from the test project's compile, **not deleted**. Each is a behaviour we have
already specified and must not lose:

| File | Waiting on |
| --- | --- |
| `DetectionSessionTests.cs` | Game session lifecycle |
| `DetectorTeardownOnGameSwitchTests.cs` | Teardown when the active game changes |
| `ModelIdResolutionTests.cs` | Resolving a model from the detected game |
| `ExclusionSuppressionTests.cs` | Per-game exclusion rules |
| `ModelPathCasingTests.cs` | Case handling in model path resolution |

When the integration subsystem lands, re-enable these against it — do not rewrite them from scratch.
Their assertions are the record of what that subsystem owes the detector.
