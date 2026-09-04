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
    // Past this, the loop is assumed to still be inside session.Run.
    private const int StopJoinTimeoutSeconds = 3;

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

    private InferenceSession? _session;
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
    private GrayscaleStrategy _grayscaleStrategy = GrayscaleStrategy.PerGroupCrop;
    private int _numClasses;
    private bool _disposed;
    private bool _quarantined;

    public event Action<List<DetectionResult>>? DetectionsAvailable;

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

        // Idempotent: returning the same array to the pool twice lets the pool hand it
        // to two callers at once.
        public void ReturnBuffer()
        {
            var buffer = Interlocked.Exchange(ref _buffer, Array.Empty<byte>());
            if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Start(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_detectionThread is not null || _quarantined)
                throw new InvalidOperationException("VisualEventDetector is already running or is still quiescing.");

            InferenceSession? session = null;
            IFrameSubscription? subscription = null;
            CancellationTokenSource? cts = null;
            RunOptions? runOptions = null;

            try
            {
                List<EventDefinition> definitions;
                while (true)
                {
                    session = ModelService.LoadModel(gameId);
                    definitions = ModelService.LoadEventDefinitions(gameId);
                    var metadata = OnnxModelInspector.Inspect(session, ModelService.GetModelPath(gameId));
                    var apiMismatch = ModelApiV1Compatibility.FindMismatch(definitions, metadata);
                    if (apiMismatch is null)
                        break;

                    ModelService.UnloadModel(gameId);
                    session = null;
                    if (!ModelService.RejectCurrentBundle(gameId, out var rejectedPath))
                    {
                        throw new InvalidDataException(
                            $"Model API v1 compatibility failed for {gameId}: {apiMismatch}");
                    }

                    Log.Warning(
                        "VisualEventDetector: skipping incompatible model bundle {ModelPath} for {GameId}: {Mismatch}",
                        rejectedPath, gameId, apiMismatch);
                }

                // Reused across every region of every cycle: a fresh float[640*640*3] per inference is
                // 4.9 MB straight to the LOH.
                var inputBuffer = new float[ModelInputSize * ModelInputSize * 3];
                var inputTensor = new DenseTensor<float>(
                    inputBuffer.AsMemory(), new[] { 1, 3, ModelInputSize, ModelInputSize });
                var outputNames = session.OutputMetadata.Keys.ToList();
                var inputContainer = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(session.InputNames[0], inputTensor)
                };

                var numClasses = ResolveClassCount(session, outputNames[0], definitions, gameId);
                var regionGroups = BuildRuntimeRegionGroups(definitions, numClasses);
                var grayscaleStrategy = DetectionFramePreprocessor.SelectGrayscaleStrategy(regionGroups);
                var divisor = ComputeFrameRateDivisor(GetConfiguredOutputFps());

                cts = new CancellationTokenSource();
                subscription = FrameSourceRegistry.Current.Subscribe(
                    FramePixelFormat.Bgra,
                    width: ObsSubscribeWidth,
                    height: ObsSubscribeHeight,
                    callback: OnFrame,
                    frameRateDivisor: (uint)divisor);
                runOptions = new RunOptions();

                _gameId = gameId;
                _session = session;
                _inputBuffer = inputBuffer;
                _inputTensor = inputTensor;
                _inputContainer = inputContainer;
                _outputNames = outputNames;
                _runOptions = runOptions;
                _regionGroups = regionGroups;
                _grayscaleStrategy = grayscaleStrategy;
                _numClasses = numClasses;
                _subscription = subscription;
                _cts = cts;

                var token = cts.Token;
                var thread = new Thread(() => RunDetectionThread(
                    token, session, gameId, runOptions, cts))
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
                _gameId = null;
                _quarantined = false;

                subscription?.Dispose();
                DrainFrameQueue();
                runOptions?.Dispose();
                cts?.Dispose();
                if (session is not null)
                    ModelService.UnloadModel(gameId);
                throw;
            }
        }
    }

    public void Stop()
    {
        Thread? thread;
        IFrameSubscription? subscription;

        lock (_lifecycleGate)
        {
            _cts?.Cancel();
            subscription = Interlocked.Exchange(ref _subscription, null);
            thread = _detectionThread;
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

    private void RunDetectionThread(CancellationToken token, InferenceSession session,
        string gameId, RunOptions runOptions, CancellationTokenSource cts)
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
            while (_frameQueue.Reader.TryRead(out var stale))
                stale.ReturnBuffer();

            var ownsRun = false;
            IFrameSubscription? orphanedSubscription = null;
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_session, session))
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
                    _gameId = null;
                }
            }

            if (ownsRun)
            {
                orphanedSubscription?.Dispose();
                DrainFrameQueue();
                ModelService.UnloadModel(gameId);
                runOptions.Dispose();
                cts.Dispose();

                lock (_lifecycleGate)
                    _quarantined = false;
            }
        }
    }

    private void DrainFrameQueue()
    {
        while (_frameQueue.Reader.TryRead(out var stale))
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
            numClasses = definitions.Count;
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
            .Where(definition => (uint)definition.ClassId < (uint)numClasses)
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
            buffer = ArrayPool<byte>.Shared.Rent(height * rowBytes);

            var src = frame.GetPlane(0, (uint)height);
            DetectionFramePreprocessor.CopyPlane(src, srcStride, buffer, rowBytes, height);

            queued = _frameQueue.Writer.TryWrite(new FrameData
            {
                Buffer = buffer,
                Width = width,
                Height = height
            });

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
        if (session == null) return;

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
                        DetectionsAvailable?.Invoke([]);
                        continue;
                    }

                    // Both branches feed byte-identical buffers to inference; they differ only in
                    // how many pixels they convert. Chosen once at Start — the group set is fixed
                    // for the session, so deciding per frame would re-derive the same answer.
                    var frameGray = _grayscaleStrategy == GrayscaleStrategy.WholeFrameOnce
                        ? DetectionFramePreprocessor.BgraToGray(frameData.Buffer, fW, fH)
                        : null;

                    try
                    {
                        foreach (var group in _regionGroups)
                        {
                            if (!DetectionFramePreprocessor.TryGetCropRect(group, fW, fH, out var cropX, out var cropY,
                                    out var cropW, out var cropH))
                                continue;

                            byte[] resized;
                            if (frameGray != null)
                            {
                                resized = DetectionFramePreprocessor.CropAndResizeGray(frameGray, fW, fH, cropX, cropY,
                                    cropW, cropH, ModelInputSize, ModelInputSize);
                            }
                            else
                            {
                                var crop = DetectionFramePreprocessor.CropBgraToGray(frameData.Buffer, fW, cropX, cropY, cropW, cropH);
                                try
                                {
                                    resized = DetectionFramePreprocessor.ResizeGray(crop, cropW, cropH, ModelInputSize, ModelInputSize);
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
                                    DetectionFramePreprocessor.MapDetectionsToFullFrame(results, cropX, cropY, cropW, cropH, fW, fH);
                                    allResults.AddRange(results);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(resized);
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
                    DetectionsAvailable?.Invoke(allResults);
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

    // The divisor is relative to OBS's configured recording framerate, not the game's render rate:
    // OBS composites its canvas at obs_video_info fps_num/fps_den, which Tript sets from the user's
    // FrameRate setting (OBSService.ResetVideoSettings, called at OBSService.cs:873). A game
    // rendering at 144fps recorded at 60fps still delivers 60 frames/sec to the callback.
    internal static int ComputeFrameRateDivisor(int outputFps)
    {
        if (outputFps <= 0) return FpsDivisor;
        return Math.Max(1, outputFps / TargetCaptureFps);
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
