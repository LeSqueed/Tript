// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Buffers;
using System.Threading.Channels;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tript.Obs;
using Serilog;

namespace Tript.Detection;

public class VisualEventDetector : IDisposable
{
    private const int ModelInputSize = 640;
    private const int FpsDivisor = 30;
    private const int TargetCaptureFps = 3;
    private const int ObsSubscribeWidth = 1920;
    private const int ObsSubscribeHeight = 1080;

    // OCR of small in-game text (kill feed) needs more than 1080p — recall roughly doubles at the
    // native canvas size. When a game has OCR events the frame is requested at the OBS output
    // resolution instead, capped here to bound the per-frame copy and crop cost.
    private const int OcrSubscribeMaxWidth = 2560;
    private const int OcrSubscribeMaxHeight = 1440;
    // Past this, the loop is assumed to still be inside session.Run.
    private const int StopJoinTimeoutSeconds = 3;

    // Stop waits this long for the OCR worker to exit, then leaves it to tear itself down.
    private const int OcrStopJoinSeconds = 3;

    // Past this a completed OCR pass is dropped rather than merged into further object cycles.
    private const int OcrSnapshotMaxAgeMs = 2500;

    private const int OcrPollIntervalMs = 100;

    // Past this, a frame callback is assumed never to finish, and teardown stops waiting for it.
    private const int FrameCallbackQuiesceTimeoutMs = 1000;

    private readonly int _detectionIntervalMs;
    private readonly object _lifecycleGate = new();
    private IFrameSubscription? _subscription;
    private CancellationTokenSource? _cts;
    private Thread? _detectionThread;
    private readonly Channel<FrameData> _frameQueue = Channel.CreateBounded<FrameData>(
        new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        },
        static dropped => dropped.ReturnBuffer());

    // OCR runs on its own thread so a multi-second pass never sets the object cadence or blocks
    // Stop. It pulls a frame only when idle, so OnFrame copies for it at the OCR rate, not capture.
    private readonly Channel<FrameData> _ocrFrameQueue = Channel.CreateBounded<FrameData>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        },
        static dropped => dropped.ReturnBuffer());
    private Thread? _ocrThread;
    private volatile bool _ocrActive;
    private volatile bool _ocrHungry;
    private volatile OcrSnapshot? _ocrSnapshot;

    private InferenceSession? _session;
    private object? _runIdentity;
    private float[]? _inputBuffer;
    private DenseTensor<float>? _inputTensor;
    private List<NamedOnnxValue>? _inputContainer;
    private IReadOnlyList<string>? _outputNames;
    private RunOptions? _runOptions;
    private string? _gameId;
    private int _isProcessing;
    private int _diagnosticFrameCount;
    private DateTime _lastEmptyInferenceLog;
    private List<RegionGroup> _regionGroups = new();
    private List<EventDefinition> _objectDefinitions = [];
    private GrayscaleStrategy _grayscaleStrategy = GrayscaleStrategy.PerGroupCrop;
    private int _numClasses;
    private bool _disposed;
    private bool _quarantined;

    public event Action<DetectionBatch>? DetectionsAvailable;

    public VisualEventDetector(int detectionIntervalMs = 1000)
    {
        _detectionIntervalMs = detectionIntervalMs;
    }

    private sealed class FrameData
    {
        private byte[] _buffer = Array.Empty<byte>();

        public byte[] Buffer
        {
            get => _buffer;
            set => _buffer = value;
        }

        public int Width { get; set; }
        public int Height { get; set; }
        public DateTime Timestamp { get; set; }

        // Idempotent: returning the same array to the pool twice lets the pool hand it
        // to two callers at once.
        public void ReturnBuffer()
        {
            var buffer = Interlocked.Exchange(ref _buffer, Array.Empty<byte>());
            if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record OcrSnapshot(List<OcrMatch> Matches, DateTime CompletedAtUtc);

    public void Start(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_detectionThread is not null || _ocrThread is not null || _quarantined)
                throw new InvalidOperationException("VisualEventDetector is already running or is still quiescing.");

            InferenceSession? session = null;
            IFrameSubscription? subscription = null;
            CancellationTokenSource? cts = null;
            RunOptions? runOptions = null;
            PaddleOcrRecognizer? ocrRecognizer = null;
            var ocrThreadStarted = false;
            var runIdentity = new object();
            var modelLoaded = false;

            try
            {
                List<EventDefinition> definitions;
                while (true)
                {
                    definitions = ModelService.LoadEventDefinitions(gameId);
                    var objectDefinitions = definitions
                        .Where(definition => definition.DetectionKind == DetectionKind.Object)
                        .ToList();
                    if (objectDefinitions.Count == 0)
                        break;

                    session = ModelService.LoadModel(gameId);
                    modelLoaded = true;
                    var metadata = OnnxModelInspector.Inspect(session, ModelService.GetModelPath(gameId));
                    var apiMismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
                    if (apiMismatch is null)
                        break;

                    ModelService.UnloadModel(gameId);
                    session = null;
                    modelLoaded = false;
                    if (!ModelService.RejectCurrentBundle(gameId, out var rejectedPath))
                    {
                        throw new InvalidDataException(
                            $"Model API v1 compatibility failed for {gameId}: {apiMismatch}");
                    }

                    Log.Warning(
                        "VisualEventDetector: skipping incompatible model bundle {ModelPath} for {GameId}: {Mismatch}",
                        rejectedPath, gameId, apiMismatch);
                }

                var ocrDefinitions = definitions
                    .Where(definition => definition.DetectionKind == DetectionKind.Ocr)
                    .ToList();
                if (session is null && ocrDefinitions.Count == 0)
                    throw new InvalidDataException($"No detection events are configured for {gameId}.");
                if (ocrDefinitions.Count > 0)
                {
                    ocrRecognizer = new PaddleOcrRecognizer(
                        ModelService.GetOcrModelPath(gameId), ModelService.GetOcrDictionaryPath(gameId),
                        ModelService.GetOcrDetectorPath(gameId));
                }

                float[]? inputBuffer = null;
                DenseTensor<float>? inputTensor = null;
                List<string>? outputNames = null;
                List<NamedOnnxValue>? inputContainer = null;
                var numClasses = 0;
                var regionGroups = new List<RegionGroup>();
                if (session is not null)
                {
                    inputBuffer = new float[ModelInputSize * ModelInputSize * 3];
                    inputTensor = new DenseTensor<float>(
                        inputBuffer.AsMemory(), new[] { 1, 3, ModelInputSize, ModelInputSize });
                    outputNames = session.OutputMetadata.Keys.ToList();
                    inputContainer =
                    [
                        NamedOnnxValue.CreateFromTensor(session.InputNames[0], inputTensor),
                    ];
                    numClasses = ResolveClassCount(session, outputNames[0], definitions, gameId);
                    regionGroups = BuildRuntimeRegionGroups(definitions, numClasses);
                }
                var ocrRegionPlans = OcrRegionPlanner.Build(definitions,
                    ModelService.LoadRegionGroups(gameId));
                var grayscaleStrategy = DetectionFramePreprocessor.SelectGrayscaleStrategy(regionGroups);
                var divisor = ComputeFrameRateDivisor(GetConfiguredOutputFps());

                var (subscribeWidth, subscribeHeight) = ResolveSubscribeSize(ocrDefinitions.Count > 0);

                cts = new CancellationTokenSource();
                subscription = FrameSourceRegistry.Current.Subscribe(
                    FramePixelFormat.Bgra,
                    width: subscribeWidth,
                    height: subscribeHeight,
                    callback: OnFrame,
                    frameRateDivisor: (uint)divisor);
                runOptions = new RunOptions();

                _gameId = gameId;
                _session = session;
                _runIdentity = runIdentity;
                _inputBuffer = inputBuffer;
                _inputTensor = inputTensor;
                _inputContainer = inputContainer;
                _outputNames = outputNames;
                _runOptions = runOptions;
                _regionGroups = regionGroups;
                _objectDefinitions = definitions
                    .Where(definition => definition.DetectionKind == DetectionKind.Object)
                    .ToList();
                _grayscaleStrategy = grayscaleStrategy;
                _numClasses = numClasses;
                _subscription = subscription;
                _cts = cts;

                var token = cts.Token;

                if (ocrRecognizer is not null)
                {
                    _ocrSnapshot = null;
                    _ocrHungry = true;
                    _ocrActive = true;
                    var capturedRecognizer = ocrRecognizer;
                    var ocrThread = new Thread(() => RunOcrThread(
                        token, capturedRecognizer, ocrRegionPlans))
                    {
                        IsBackground = true,
                        Name = "Tript.VisualEventDetector.Ocr",
                        Priority = ThreadPriority.Lowest,
                    };
                    _ocrThread = ocrThread;
                    ocrThreadStarted = true;
                    ocrThread.Start();
                }

                var thread = new Thread(() => RunDetectionThread(
                    token, runIdentity, gameId, runOptions, cts, modelLoaded))
                {
                    IsBackground = true,
                    Name = "Tript.VisualEventDetector",
                    Priority = ThreadPriority.BelowNormal,
                };
                _detectionThread = thread;
                thread.Start();

                Log.Information("VisualEventDetector: Started for game {GameId} with {RegionGroupCount} region groups",
                    gameId, _regionGroups.Count);
            }
            catch
            {
                _subscription = null;
                _cts = null;
                _runOptions = null;
                _detectionThread = null;
                _session = null;
                _runIdentity = null;
                _gameId = null;
                _objectDefinitions = [];
                _quarantined = false;

                subscription?.Dispose();
                DrainFrameQueue();
                runOptions?.Dispose();

                if (ocrThreadStarted)
                {
                    // The OCR thread self-tears-down on cancel; leave its cts alive to observe it.
                    cts?.Cancel();
                }
                else
                {
                    _ocrActive = false;
                    _ocrThread = null;
                    _ocrSnapshot = null;
                    ocrRecognizer?.Dispose();
                    cts?.Dispose();
                }

                if (modelLoaded)
                    ModelService.UnloadModel(gameId);
                throw;
            }
        }
    }

    public void Stop()
    {
        Thread? thread;
        Thread? ocrThread;
        IFrameSubscription? subscription;

        lock (_lifecycleGate)
        {
            _cts?.Cancel();
            _ocrActive = false;
            subscription = Interlocked.Exchange(ref _subscription, null);
            thread = _detectionThread;
            ocrThread = _ocrThread;
        }

        subscription?.Dispose();

        if (thread is not null && !thread.Join(TimeSpan.FromSeconds(StopJoinTimeoutSeconds)))
        {
            lock (_lifecycleGate)
                _quarantined = true;

            Log.Error("VisualEventDetector: detection loop for {GameId} did not exit within {TimeoutSeconds}s; keeping the run owned and refusing restart until it exits",
                _gameId, StopJoinTimeoutSeconds);
            return;
        }

        // Bounded, non-quarantining: a still-running OCR pass releases itself in the background.
        if (ocrThread is not null && !ocrThread.Join(TimeSpan.FromSeconds(OcrStopJoinSeconds)))
            Log.Warning("VisualEventDetector: OCR worker for {GameId} did not exit within {TimeoutSeconds}s; it will finish and release in the background",
                _gameId, OcrStopJoinSeconds);

        DrainFrameQueue();

        // A test or a failed setup can leave a cancellation source without a thread. Dispose that
        // state here; a started run releases all of its resources from RunDetectionThread.
        lock (_lifecycleGate)
        {
            if (thread is null && _detectionThread is null)
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        Log.Information("VisualEventDetector: Stopped");
    }

    private void RunDetectionThread(CancellationToken token, object runIdentity,
        string gameId, RunOptions runOptions, CancellationTokenSource cts, bool modelLoaded)
    {
        try
        {
            try
            {
                DetectionLoop(token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error(ex, "VisualEventDetector: detection loop terminated unexpectedly");
            }
        }
        finally
        {
            // Stop may have timed out while this thread was inside native inference. Once the loop
            // finally exits, no consumer can touch queued frames, so return every remaining buffer
            // before releasing the run's native resources.
            DrainFrameQueue();

            var ownsRun = false;
            IFrameSubscription? orphanedSubscription = null;
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_runIdentity, runIdentity))
                {
                    ownsRun = true;
                    orphanedSubscription = _subscription;
                    _detectionThread = null;
                    _subscription = null;
                    _cts = null;
                    _runOptions = null;
                    _inputContainer = null;
                    _inputTensor = null;
                    _inputBuffer = null;
                    _outputNames = null;
                    _session = null;
                    _runIdentity = null;
                    _objectDefinitions = [];
                    _gameId = null;
                }
            }

            if (ownsRun)
            {
                orphanedSubscription?.Dispose();
                DrainFrameQueue();
                if (modelLoaded) ModelService.UnloadModel(gameId);
                runOptions.Dispose();
                cts.Dispose();

                lock (_lifecycleGate)
                    _quarantined = false;
            }
        }
    }

    // Runs the OCR pass off the object cadence and publishes a snapshot; owns its recogniser and
    // clears _ocrThread (by thread identity) on exit.
    private void RunOcrThread(CancellationToken token,
        PaddleOcrRecognizer recognizer, IReadOnlyList<OcrRegionPlan> regionPlans)
    {
        try
        {
            _ocrHungry = true;
            while (!token.IsCancellationRequested)
            {
                if (token.WaitHandle.WaitOne(OcrPollIntervalMs)) break;
                if (!_ocrFrameQueue.Reader.TryRead(out var frame))
                {
                    _ocrHungry = true;
                    continue;
                }

                try
                {
                    var matches = RunOcrPass(recognizer, regionPlans, frame, token);
                    _ocrSnapshot = new OcrSnapshot(matches, DateTime.UtcNow);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "VisualEventDetector: OCR pass failed");
                }
                finally
                {
                    frame.ReturnBuffer();
                    _ocrHungry = true;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "VisualEventDetector: OCR worker terminated unexpectedly");
        }
        finally
        {
            while (_ocrFrameQueue.Reader.TryRead(out var stale))
                stale.ReturnBuffer();

            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_ocrThread, Thread.CurrentThread))
                {
                    _ocrThread = null;
                    _ocrActive = false;
                    _ocrSnapshot = null;
                }
            }

            recognizer.Dispose();
        }
    }

    private void DrainFrameQueue()
    {
        while (_frameQueue.Reader.TryRead(out var stale))
            stale.ReturnBuffer();
        while (_ocrFrameQueue.Reader.TryRead(out var stale))
            stale.ReturnBuffer();
    }

    // The YOLO parser strides the tensor by (4 + numClasses), so a count disagreeing with the
    // exported graph decodes every box to garbage, silently. The graph's shape is authoritative;
    // hand-edited events.json is checked against it rather than believed.
    private static int ResolveClassCount(InferenceSession session, string outputName,
        List<EventDefinition> definitions, string gameId)
    {
        var dimensions = session.OutputMetadata[outputName].Dimensions;
        var modelClassNames = ReadModelClassNames(session);

        if (!TryDeriveClassCount(dimensions, out var numClasses))
        {
            // A dynamic axis exports as -1/0; arithmetic on it yields a plausible-looking stride.
            numClasses = definitions.Count(definition => definition.DetectionKind == DetectionKind.Object);
            Log.Warning("VisualEventDetector: output {OutputName} of model {GameId} has no static class dimension ({Dimensions}), falling back to {NumClasses} classes from events.json",
                outputName, gameId, string.Join('x', dimensions), numClasses);
        }

        var mismatch = FindClassMapMismatch(definitions, numClasses, modelClassNames);
        if (mismatch != null)
        {
            Log.Error("VisualEventDetector: events.json does not match model.onnx for {GameId}: {Mismatch}",
                gameId, mismatch);
            throw new InvalidOperationException(
                $"Event definitions for {gameId} do not match model.onnx: {mismatch}");
        }

        Log.Information("VisualEventDetector: model {GameId} declares {NumClasses} classes for {EventCount} event definitions",
            gameId, numClasses, definitions.Count);
        return numClasses;
    }

    // A detect head outputs [batch, 4 + numClasses, numAnchors] — [1, 11, 8400] for the shipped
    // 7-class model. Anything else is reported as underivable rather than guessed at.
    internal static bool TryDeriveClassCount(IReadOnlyList<int>? outputDimensions, out int numClasses)
        => OnnxModelInspector.TryDeriveClassCount(outputDimensions, out numClasses);

    // Absent on models from other tooling, so a null map skips the name check; the shape check holds.
    private static IReadOnlyDictionary<int, string>? ReadModelClassNames(InferenceSession session)
    {
        try
        {
            return session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var names)
                ? ParseClassNames(names)
                : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VisualEventDetector: could not read the model class map, skipping the events.json name check");
            return null;
        }
    }

    internal static IReadOnlyDictionary<int, string>? ParseClassNames(string? names)
        => OnnxModelInspector.ParseClassNames(names);

    // events.json keys bookmarks by classId; the model decides what each emitted classId means.
    // Appended entries are safe for an older model, but removing or renaming its classes is not.
    internal static string? FindClassMapMismatch(IReadOnlyList<EventDefinition> definitions,
        int numClasses, IReadOnlyDictionary<int, string>? modelClassNames)
        => ModelEventCompatibility.FindMismatch(definitions, numClasses, modelClassNames);

    internal static List<RegionGroup> BuildRuntimeRegionGroups(
        IReadOnlyList<EventDefinition> definitions, int numClasses)
    {
        var modelDefinitions = definitions
            .Where(definition => definition.DetectionKind == DetectionKind.Object
                && (uint)definition.ClassId < (uint)numClasses)
            .ToList();
        return DetectionFramePreprocessor.BuildRegionGroups(modelDefinitions);
    }

    private void OnFrame(in VideoFrame frame)
    {
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
            return;

        byte[]? buffer = null;
        var queued = false;

        try
        {
            var srcStride = (int)frame.GetLinesize(0);
            var width = (int)frame.Width;
            var height = (int)frame.Height;
            var rowBytes = width * 4;
            var timestamp = DateTime.Now;
            buffer = ArrayPool<byte>.Shared.Rent(height * rowBytes);

            var src = frame.GetPlane(0, (uint)height);
            DetectionFramePreprocessor.CopyPlane(src, srcStride, buffer, rowBytes, height);

            queued = _frameQueue.Writer.TryWrite(new FrameData
            {
                Buffer = buffer,
                Width = width,
                Height = height,
                Timestamp = timestamp,
            });

            // Independent copy for the OCR worker, only when it is idle and asking — never shared
            // with the object pipeline's buffer.
            if (_ocrActive && _ocrHungry)
            {
                var ocrBuffer = ArrayPool<byte>.Shared.Rent(height * rowBytes);
                try
                {
                    DetectionFramePreprocessor.CopyPlane(src, srcStride, ocrBuffer, rowBytes, height);
                    if (_ocrFrameQueue.Writer.TryWrite(new FrameData
                    {
                        Buffer = ocrBuffer,
                        Width = width,
                        Height = height,
                        Timestamp = timestamp,
                    }))
                    {
                        _ocrHungry = false;
                        ocrBuffer = null;
                    }
                }
                finally
                {
                    if (ocrBuffer is not null) ArrayPool<byte>.Shared.Return(ocrBuffer);
                }
            }

            if (queued && Interlocked.Increment(ref _diagnosticFrameCount) % 15 == 0)
                Log.Information("VisualEventDetector: received {Count} live frame(s), latest {Width}x{Height}",
                    _diagnosticFrameCount, width, height);

            // DropOldest only refuses once the channel is completed, which nothing does today.
            if (!queued)
                Log.Warning("VisualEventDetector: frame queue rejected a frame, dropping it");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VisualEventDetector: frame copy error");
        }
        finally
        {
            // Ownership passes to the channel only on a successful write; GetPlane and CopyPlane
            // both throw on a short plane.
            if (!queued && buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    // Synchronous on purpose. An async loop resumes its continuations on the thread pool after
    // the first await, so every iteration after that would run at the pool's Normal priority and
    // the BelowNormal thread this runs on would sit blocked, achieving nothing.
    private void DetectionLoop(CancellationToken ct)
    {
        var session = _session;
        if (session is null && !_ocrActive) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Returns true when the token is cancelled, false on timeout.
                if (ct.WaitHandle.WaitOne(_detectionIntervalMs)) break;

                if (!_frameQueue.Reader.TryRead(out var frameData))
                {
                    Log.Debug("DetectionLoop: no frame available");
                    continue;
                }

                Log.Debug("DetectionLoop: processing frame {W}x{H}", frameData.Width, frameData.Height);

                try
                {
                    var allResults = new List<DetectionResult>();
                    var fW = frameData.Width;
                    var fH = frameData.Height;

                    // Skip near-black frames (loading screens, transitions) — they can produce NaN
                    // in the model. Subsampled, so a lit region smaller than the 16px stride can
                    // fall entirely between probes and be missed — at most a 15x15 blob.
                    if (DetectionFramePreprocessor.IsNearBlack(frameData.Buffer, fW, fH))
                    {
                        Log.Debug("DetectionLoop: skipping near-black frame");
                        // A skipped frame is still a checked frame for the host's net-count state.
                        DetectionsAvailable?.Invoke(new DetectionBatch { FrameTimestamp = frameData.Timestamp });
                        continue;
                    }

                    // Both branches feed byte-identical buffers to inference; they differ only in
                    // how many pixels they convert. Chosen once at Start — the group set is fixed
                    // for the session, so deciding per frame would re-derive the same answer.
                    var frameGray = session is not null
                        && _grayscaleStrategy == GrayscaleStrategy.WholeFrameOnce
                        ? DetectionFramePreprocessor.BgraToGray(frameData.Buffer, fW, fH)
                        : null;

                    try
                    {
                        if (session is not null)
                        {
                            foreach (var group in _regionGroups)
                            {
                                if (!DetectionFramePreprocessor.TryGetCropRect(group, fW, fH, out var cropX,
                                        out var cropY, out var cropW, out var cropH))
                                    continue;

                                byte[] resized;
                                if (frameGray != null)
                                {
                                    resized = DetectionFramePreprocessor.CropAndResizeGray(frameGray, fW, fH,
                                        cropX, cropY, cropW, cropH, ModelInputSize, ModelInputSize);
                                }
                                else
                                {
                                    var crop = DetectionFramePreprocessor.CropBgraToGray(frameData.Buffer, fW,
                                        cropX, cropY, cropW, cropH);
                                    try
                                    {
                                        resized = DetectionFramePreprocessor.ResizeGray(crop, cropW, cropH,
                                            ModelInputSize, ModelInputSize);
                                    }
                                    finally
                                    {
                                        ArrayPool<byte>.Shared.Return(crop);
                                    }
                                }

                                try
                                {
                                    var results = RunInferenceOnGray(session, resized);
                                    if (results != null)
                                    {
                                        foreach (var result in results) result.Timestamp = frameData.Timestamp;
                                        DetectionFramePreprocessor.MapDetectionsToFullFrame(results, cropX, cropY,
                                            cropW, cropH, fW, fH);
                                        DetectionFramePreprocessor.FilterDetectionsToEventRegions(results,
                                            _objectDefinitions);
                                        allResults.AddRange(results);
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(resized);
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (frameGray != null) ArrayPool<byte>.Shared.Return(frameGray);
                    }

                    Log.Debug("DetectionLoop: {Count} results across {Groups} groups", allResults.Count, _regionGroups.Count);
                    if (allResults.Count == 0 && DateTime.UtcNow - _lastEmptyInferenceLog >= TimeSpan.FromSeconds(5))
                    {
                        _lastEmptyInferenceLog = DateTime.UtcNow;
                        Log.Information("DetectionLoop: processed live frame {W}x{H}; inference returned no detections",
                            fW, fH);
                    }
                    // Fold the latest OCR pass in so the host still gets object + OCR per batch.
                    DetectionsAvailable?.Invoke(new DetectionBatch
                    {
                        FrameTimestamp = frameData.Timestamp,
                        ObjectDetections = allResults,
                        OcrMatches = TakeOcrMatches(),
                    });
                }
                finally
                {
                    // OnFrame owns _isProcessing and clears it in its own finally; resetting it
                    // here could unlock an OnFrame still mid-copy.
                    frameData.ReturnBuffer();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warning(ex, "VisualEventDetector: detection error");
            }
        }
    }

    private List<OcrMatch> TakeOcrMatches()
    {
        var snapshot = _ocrSnapshot;
        return FreshMatches(snapshot?.Matches, snapshot?.CompletedAtUtc ?? default,
            DateTime.UtcNow, OcrSnapshotMaxAgeMs);
    }

    internal static List<OcrMatch> FreshMatches(List<OcrMatch>? matches, DateTime completedAtUtc,
        DateTime nowUtc, int maxAgeMs)
        // Absolute age so a clock step in either direction retires the snapshot.
        => matches is not null && (nowUtc - completedAtUtc).Duration() <= TimeSpan.FromMilliseconds(maxAgeMs)
            ? matches
            : [];

    private static List<OcrMatch> RunOcrPass(PaddleOcrRecognizer recognizer,
        IReadOnlyList<OcrRegionPlan> regionPlans, FrameData frame, CancellationToken token)
    {
        var matches = new List<OcrMatch>();
        var fW = frame.Width;
        var fH = frame.Height;
        if (fW <= 0 || fH <= 0) return matches;

        foreach (var plan in regionPlans)
        {
            token.ThrowIfCancellationRequested();
            if (!DetectionFramePreprocessor.TryGetCropRect(plan.Region, fW, fH,
                    out var cropX, out var cropY, out var cropW, out var cropH))
                continue;

            var recognitions = recognizer.RecognizeAll(frame.Buffer, fW, fH, cropX, cropY, cropW, cropH);
            foreach (var recognition in recognitions)
            foreach (var binding in plan.Bindings)
            {
                var ocr = binding.Definition.Ocr;
                if (ocr is null
                    || recognition.Confidence < OcrTokenTemplateMatcher.EffectiveMinimumConfidence(ocr.MinimumConfidence))
                    continue;
                var match = OcrTokenTemplateMatcher.FindBestMatch(recognition.Text, ocr.Patterns);
                if (match is null) continue;
                matches.Add(new OcrMatch
                {
                    EventId = binding.Definition.Id,
                    Text = recognition.Text,
                    NormalizedText = match.NormalizedText,
                    LanguageTag = match.LanguageTag,
                    SegmentId = binding.SegmentId,
                    Confidence = recognition.Confidence,
                    X = (float)recognition.X / fW,
                    Y = (float)recognition.Y / fH,
                    Width = (float)recognition.Width / fW,
                    Height = (float)recognition.Height / fH,
                });
            }
        }

        return matches;
    }

    // The divisor is relative to OBS's configured recording framerate, not the game's render rate:
    // OBS composites its canvas at obs_video_info fps_num/fps_den, which Tript sets from the user's
    // FrameRate setting (OBSService.ResetVideoSettings, called at OBSService.cs:873). A game
    // rendering at 144fps recorded at 60fps still delivers 60 frames/sec to the callback.
    internal static int ComputeFrameRateDivisor(int outputFps)
    {
        if (outputFps <= 0) return FpsDivisor;
        return Math.Max(1, outputFps / TargetCaptureFps);
    }

    // 1920x1080 unless the game has OCR events and OBS reports a larger output, in which case the
    // native size (capped) is requested so small text survives the crop.
    private static (int Width, int Height) ResolveSubscribeSize(bool hasOcr)
    {
        if (!hasOcr) return (ObsSubscribeWidth, ObsSubscribeHeight);
        try
        {
            if (FrameSourceRegistry.Current.GetVideoTiming() is { Width: > 0, Height: > 0 } timing)
            {
                return (
                    Math.Clamp((int)timing.Width, ObsSubscribeWidth, OcrSubscribeMaxWidth),
                    Math.Clamp((int)timing.Height, ObsSubscribeHeight, OcrSubscribeMaxHeight));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VisualEventDetector: could not read OBS output size, using {W}x{H}",
                ObsSubscribeWidth, ObsSubscribeHeight);
        }
        return (ObsSubscribeWidth, ObsSubscribeHeight);
    }

    // Returns 0 when the rate is unavailable, which ComputeFrameRateDivisor maps to the default.
    private static int GetConfiguredOutputFps()
    {
        try
        {
            var info = FrameSourceRegistry.Current.GetVideoTiming();
            if (info == null)
            {
                Log.Warning("VisualEventDetector: OBS reported no video info, using default divisor");
                return 0;
            }

            // OBS uses fractional rates (60000/1001 for 59.94), so the numerator alone is
            // meaningless. Rounding keeps 59.94 at 60 rather than truncating to 59.
            var num = info.Value.FpsNumerator;
            var den = info.Value.FpsDenominator;
            if (num == 0 || den == 0) return 0;
            return (int)Math.Round((double)num / den);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VisualEventDetector: could not read OBS output fps, using default divisor");
            return 0;
        }
    }

    private List<DetectionResult>? RunInferenceOnGray(
        InferenceSession session, byte[] grayData)
    {
        var buffer = _inputBuffer;
        var container = _inputContainer;
        var outputNames = _outputNames;
        var runOptions = _runOptions;
        if (buffer == null || container == null || outputNames == null || runOptions == null)
            return null;

        try
        {
            DetectionFramePreprocessor.FillInputTensor(grayData, buffer, ModelInputSize);

            using var results = session.Run(container, outputNames, runOptions);
            var tensor = results[0].AsTensor<float>();
            // Backed by memory the result owns; parse it before the using ends.
            var span = tensor is DenseTensor<float> dense
                ? dense.Buffer.Span
                : tensor.ToArray().AsSpan();
            return DetectionFramePreprocessor.ParseYoloOutputForInput(span, ModelInputSize, ModelInputSize, _numClasses);
        }
        catch (ObjectDisposedException)
        {
            Log.Debug("RunInferenceOnGray: session disposed");
            return null;
        }
        catch (OnnxRuntimeException ex)
        {
            Log.Warning(ex, "RunInferenceOnGray: ONNX error");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RunInferenceOnGray: inference error");
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        Stop();
        GC.SuppressFinalize(this);
    }
}
