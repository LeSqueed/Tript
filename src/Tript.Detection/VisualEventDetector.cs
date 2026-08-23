// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Channels;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tript.Obs;
using Serilog;
using Serilog.Events;

namespace Tript.Detection;

public class VisualEventDetector : IDisposable
{
    private const int ModelInputSize = 640;
    private const int FpsDivisor = 30;
    private const int TargetCaptureFps = 3;
    private const int ObsSubscribeWidth = 1920;
    private const int ObsSubscribeHeight = 1080;
    private const int BlackCheckStride = 16;
    private const int BlackCheckLumaThreshold = 15;
    private const int BlackCheckMinBrightSamples = 0;

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

    internal enum GrayscaleStrategy
    {
        // Convert each group's crop rectangle on its own, straight from BGRA.
        PerGroupCrop,

        // Convert the frame once, then cut every group out of that one grey buffer.
        WholeFrameOnce
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
                session = ModelService.LoadModel(gameId);

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

                var definitions = ModelService.LoadEventDefinitions(gameId);
                var numClasses = ResolveClassCount(session, outputNames[0], definitions, gameId);
                var regionGroups = BuildRegionGroups(definitions);
                var grayscaleStrategy = SelectGrayscaleStrategy(regionGroups);
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

    // ParseYoloOutput strides the tensor by (4 + numClasses), so a count disagreeing with the
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

    // events.json keys bookmarks by classId; the model decides what each classId means. An entry
    // added, removed or renamed without retraining mislabels every detection. Null when they agree.
    internal static string? FindClassMapMismatch(IReadOnlyList<EventDefinition> definitions,
        int numClasses, IReadOnlyDictionary<int, string>? modelClassNames)
        => ModelEventCompatibility.FindMismatch(definitions, numClasses, modelClassNames);

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
            CopyPlane(src, srcStride, buffer, rowBytes, height);

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
                    if (IsNearBlack(frameData.Buffer, fW, fH))
                    {
                        Log.Debug("DetectionLoop: skipping near-black frame");
                        continue;
                    }

                    // Both branches feed byte-identical buffers to inference; they differ only in
                    // how many pixels they convert. Chosen once at Start — the group set is fixed
                    // for the session, so deciding per frame would re-derive the same answer.
                    var frameGray = _grayscaleStrategy == GrayscaleStrategy.WholeFrameOnce
                        ? BgraToGray(frameData.Buffer, fW, fH)
                        : null;

                    try
                    {
                        foreach (var group in _regionGroups)
                        {
                            if (!TryGetCropRect(group, fW, fH, out var cropX, out var cropY,
                                    out var cropW, out var cropH))
                                continue;

                            byte[] resized;
                            if (frameGray != null)
                            {
                                resized = CropAndResizeGray(frameGray, fW, fH, cropX, cropY,
                                    cropW, cropH, ModelInputSize, ModelInputSize);
                            }
                            else
                            {
                                var crop = CropBgraToGray(frameData.Buffer, fW, cropX, cropY, cropW, cropH);
                                try
                                {
                                    resized = ResizeGray(crop, cropW, cropH, ModelInputSize, ModelInputSize);
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
                                    MapDetectionsToFullFrame(results, cropX, cropY, cropW, cropH, fW, fH);
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

    // Takes the crop rect the detections came from, not the group: TryGetCropRect trims a region
    // overhanging a frame edge, and the untrimmed size would misplace every box.
    internal static void MapDetectionsToFullFrame(List<DetectionResult> detections,
        int cropX, int cropY, int cropW, int cropH, int frameW, int frameH)
    {
        foreach (var det in detections)
        {
            det.X = (det.X * cropW + cropX) / frameW;
            det.Y = (det.Y * cropH + cropY) / frameH;
            det.Width = det.Width * cropW / frameW;
            det.Height = det.Height * cropH / frameH;
        }
    }

    internal static List<RegionGroup> BuildRegionGroups(List<EventDefinition> definitions)
    {
        var groups = new List<RegionGroup>();
        bool hasFullFrame = false;

        foreach (var def in definitions)
        {
            if (def.ScreenRegionW.HasValue && def.ScreenRegionW.Value > 0)
            {
                groups.Add(new RegionGroup
                {
                    X = def.ScreenRegionX ?? 0,
                    Y = def.ScreenRegionY ?? 0,
                    W = def.ScreenRegionW.Value,
                    H = def.ScreenRegionH ?? 0
                });
            }
            else
            {
                hasFullFrame = true;
            }
        }

        // Merging grows a group's bounds, which can open overlaps with groups already passed
        // over. Repeating until a pass finds nothing makes the result independent of the order
        // events appear in events.json; the previous first-match-wins pass was not.
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < groups.Count && !changed; i++)
            {
                for (int j = i + 1; j < groups.Count; j++)
                {
                    if (!RegionsOverlap(groups[i], groups[j])) continue;
                    MergeRegions(groups[i], groups[j]);
                    groups.RemoveAt(j);
                    changed = true;
                    break;
                }
            }
        }

        if (hasFullFrame)
            groups.Add(new RegionGroup { X = 0, Y = 0, W = 1, H = 1 });

        if (groups.Count == 0)
            groups.Add(new RegionGroup { X = 0, Y = 0, W = 1, H = 1 });

        return groups;
    }

    // The per-group path converts each crop independently, so its cost is the sum of the crop
    // areas — which can exceed the frame. A full-frame group is the obvious way to get there
    // (it alone converts every pixel, and every other group is then pure surplus), but several
    // large overlapping groups reach the same point without one. Coverage catches both.
    internal static GrayscaleStrategy SelectGrayscaleStrategy(IReadOnlyList<RegionGroup> groups)
    {
        float coverage = 0f;
        foreach (var g in groups)
            coverage += g.W * g.H;

        // A tie goes to the per-group path: the same conversions, minus the intermediate
        // crop copy CropAndResizeGray makes.
        return coverage > 1f ? GrayscaleStrategy.WholeFrameOnce : GrayscaleStrategy.PerGroupCrop;
    }

    // The pixels one cycle converts to greyscale, which is the quantity the two strategies
    // trade off. Shares TryGetCropRect with the detection loop so the two cannot drift.
    internal static int CountGrayscalePixels(IReadOnlyList<RegionGroup> groups, int frameW, int frameH)
    {
        if (SelectGrayscaleStrategy(groups) == GrayscaleStrategy.WholeFrameOnce)
            return frameW * frameH;

        var total = 0;
        foreach (var g in groups)
        {
            if (TryGetCropRect(g, frameW, frameH, out _, out _, out var cropW, out var cropH))
                total += cropW * cropH;
        }

        return total;
    }

    internal static bool TryGetCropRect(RegionGroup group, int frameW, int frameH,
        out int cropX, out int cropY, out int cropW, out int cropH)
    {
        cropX = (int)(group.X * frameW);
        cropY = (int)(group.Y * frameH);
        cropW = (int)(group.W * frameW);
        cropH = (int)(group.H * frameH);

        if (cropW <= 0 || cropH <= 0) return false;
        if (cropX + cropW > frameW) cropW = frameW - cropX;
        if (cropY + cropH > frameH) cropH = frameH - cropY;
        return cropW > 0 && cropH > 0;
    }

    internal static bool RegionsOverlap(RegionGroup a, RegionGroup b)
    {
        if (a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y)
            return true;

        if (a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H)
            return true;

        if (b.X >= a.X && b.Y >= a.Y && b.X + b.W <= a.X + a.W && b.Y + b.H <= a.Y + a.H)
            return true;

        return false;
    }

    internal static void MergeRegions(RegionGroup a, RegionGroup b)
    {
        float x = Math.Min(a.X, b.X);
        float y = Math.Min(a.Y, b.Y);
        a.W = Math.Max(a.X + a.W, b.X + b.W) - x;
        a.H = Math.Max(a.Y + a.H, b.Y + b.H) - y;
        a.X = x;
        a.Y = y;
    }

    // OBS may pad each row to an alignment boundary. When it does not, the plane is one
    // contiguous block and the per-row loop is pure overhead for the same bytes moved.
    internal static void CopyPlane(ReadOnlySpan<byte> src, int srcStride, byte[] dst, int rowBytes, int height)
    {
        if (srcStride == rowBytes)
        {
            src.Slice(0, height * rowBytes).CopyTo(dst);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            src.Slice(y * srcStride, rowBytes)
               .CopyTo(new Span<byte>(dst, y * rowBytes, rowBytes));
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

    // Probes a 16x16 grid rather than every pixel and bails on the first sample above the
    // threshold: a frame that is genuinely black is black everywhere, so the sparse grid
    // answers the question without a full-frame pass.
    internal static bool IsNearBlack(byte[] bgra, int w, int h)
    {
        var srcRowStride = w * 4;
        var bright = 0;

        for (int y = 0; y < h; y += BlackCheckStride)
        {
            var rowOffset = y * srcRowStride;
            for (int x = 0; x < w; x += BlackCheckStride)
            {
                var i = rowOffset + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var luma = (byte)(0.299f * r + 0.587f * g + 0.114f * b);
                if (luma > BlackCheckLumaThreshold && ++bright > BlackCheckMinBrightSamples)
                    return false;
            }
        }

        return true;
    }

    internal static byte[] BgraToGray(byte[] bgra, int w, int h)
    {
        var pixels = w * h;
        var gray = ArrayPool<byte>.Shared.Rent(pixels);
        for (int i = 0; i < pixels; i++)
        {
            var srcIdx = i * 4;
            var b = bgra[srcIdx];
            var g = bgra[srcIdx + 1];
            var r = bgra[srcIdx + 2];
            gray[i] = (byte)(0.299f * r + 0.587f * g + 0.114f * b);
        }
        return gray;
    }

    // Greyscale is a pure per-pixel function, so converting only the crop rectangle gives the
    // same bytes as converting the whole frame and then cropping — at 19% of the work.
    internal static byte[] CropBgraToGray(byte[] bgra, int srcW, int cropX, int cropY,
        int cropW, int cropH)
    {
        var gray = ArrayPool<byte>.Shared.Rent(cropW * cropH);
        var srcRowStride = srcW * 4;

        for (int y = 0; y < cropH; y++)
        {
            var srcOffset = (cropY + y) * srcRowStride + cropX * 4;
            var dstOffset = y * cropW;
            for (int x = 0; x < cropW; x++)
            {
                var i = srcOffset + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                gray[dstOffset + x] = (byte)(0.299f * r + 0.587f * g + 0.114f * b);
            }
        }

        return gray;
    }

    internal static byte[] CropAndResizeGray(byte[] srcGray, int srcW, int srcH,
        int cropX, int cropY, int cropW, int cropH, int dstW, int dstH)
    {
        var crop = ArrayPool<byte>.Shared.Rent(cropW * cropH);
        try
        {
            for (int y = 0; y < cropH; y++)
            {
                Array.Copy(srcGray, (cropY + y) * srcW + cropX, crop, y * cropW, cropW);
            }

            return ResizeGray(crop, cropW, cropH, dstW, dstH);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(crop);
        }
    }

    // Reads only columns [0, cropW-1] and rows [0, cropH-1] of crop: both taps of each axis are
    // held inside the crop, so cropping before greyscale conversion stays byte-identical.
    internal static byte[] ResizeGray(byte[] crop, int cropW, int cropH, int dstW, int dstH)
    {
        // A 1px axis has no second tap: `extent - 1.001f` alone yields -0.001, leaving tap 1
        // outside the crop with a negative weight. Flooring the clamp and capping the tap collapses
        // both onto pixel 0. For extent >= 2 both are no-ops, so real crops stay byte-identical.
        var maxX = cropW - 1;
        var maxY = cropH - 1;

        var dst = ArrayPool<byte>.Shared.Rent(dstW * dstH);
        for (int dy = 0; dy < dstH; dy++)
        {
            float sy = (dy + 0.5f) * cropH / dstH - 0.5f;
            if (sy < 0) sy = 0;
            if (sy >= maxY) sy = Math.Max(cropH - 1.001f, 0f);
            int sy0 = (int)sy, sy1 = Math.Min(sy0 + 1, maxY);
            float fy = sy - sy0;

            for (int dx = 0; dx < dstW; dx++)
            {
                float sx = (dx + 0.5f) * cropW / dstW - 0.5f;
                if (sx < 0) sx = 0;
                if (sx >= maxX) sx = Math.Max(cropW - 1.001f, 0f);
                int sx0 = (int)sx, sx1 = Math.Min(sx0 + 1, maxX);
                float fx = sx - sx0;

                var v = (1 - fx) * (1 - fy) * crop[sy0 * cropW + sx0]
                      + fx * (1 - fy) * crop[sy0 * cropW + sx1]
                      + (1 - fx) * fy * crop[sy1 * cropW + sx0]
                      + fx * fy * crop[sy1 * cropW + sx1];
                dst[dy * dstW + dx] = (byte)v;
            }
        }
        return dst;
    }

    // The vector path reads four bytes at a time as a uint, so it needs little-endian lane order.
    private static bool VectorFillSupported =>
        Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian;

    internal static void FillInputTensor(byte[] grayData, float[] destination, int inputSize)
        => FillInputTensor(grayData, destination, inputSize, VectorFillSupported);

    internal static void FillInputTensor(byte[] grayData, float[] destination, int inputSize, bool useVectorPath)
        => FillInputTensor(grayData, destination, inputSize, inputSize, useVectorPath);

    internal static void FillInputTensor(byte[] grayData, float[] destination,
        int inputWidth, int inputHeight, bool useVectorPath)
    {
        var pixels = inputWidth * inputHeight;
        if (grayData.Length < pixels || destination.Length < pixels * 3)
            throw new ArgumentException(
                $"FillInputTensor needs {pixels} source bytes and {pixels * 3} destination floats, " +
                $"got {grayData.Length} and {destination.Length}.");

        int i = 0;

        if (useVectorPath)
        {
            ref byte src = ref MemoryMarshal.GetArrayDataReference(grayData);
            ref float dst = ref MemoryMarshal.GetArrayDataReference(destination);
            // A true divide, never a multiply by 1f/255f: the rounded reciprocal differs in the
            // last ulp for 126 of the 256 byte values, which would break the golden test.
            var divisor = Vector128.Create(255f);

            for (; i <= pixels - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                var packed = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, (nuint)i));
                var widened = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(packed).AsByte()));
                var v = Vector128.ConvertToSingle(widened.AsInt32()) / divisor;
                v.StoreUnsafe(ref dst, (nuint)i);
                v.StoreUnsafe(ref dst, (nuint)(i + pixels));
                v.StoreUnsafe(ref dst, (nuint)(i + 2 * pixels));
            }
        }

        for (; i < pixels; i++)
        {
            var val = grayData[i] / 255f;
            destination[i] = val;
            destination[i + pixels] = val;
            destination[i + 2 * pixels] = val;
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
            FillInputTensor(grayData, buffer, ModelInputSize);

            using var results = session.Run(container, outputNames, runOptions);
            var tensor = results[0].AsTensor<float>();
            // Backed by memory the result owns; ParseYoloOutput reads it before the using ends.
            var span = tensor is DenseTensor<float> dense
                ? dense.Buffer.Span
                : tensor.ToArray().AsSpan();
            return ParseYoloOutput(span, ModelInputSize, _numClasses);
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

    private static List<DetectionResult> ParseYoloOutput(
        ReadOnlySpan<float> output, int inputSize, int numClasses)
        => ParseYoloOutputForInput(output, inputSize, inputSize, numClasses);

    internal static List<DetectionResult> ParseYoloOutputForInput(
        ReadOnlySpan<float> output, int inputWidth, int inputHeight, int numClasses)
    {
        var results = new List<DetectionResult>();
        var numDetections = output.Length / (4 + numClasses);

        for (int i = 0; i < numDetections; i++)
        {
            var classId = 0;
            var maxConf = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                var conf = output[(4 + c) * numDetections + i];
                if (conf > maxConf)
                {
                    maxConf = conf;
                    classId = c;
                }
            }

            if (maxConf < 0.7f) continue;

            var cx = output[i] / inputWidth;
            var cy = output[1 * numDetections + i] / inputHeight;
            var w = output[2 * numDetections + i] / inputWidth;
            var h = output[3 * numDetections + i] / inputHeight;

            results.Add(new DetectionResult
            {
                ClassId = classId,
                Confidence = maxConf,
                X = cx - w / 2,
                Y = cy - h / 2,
                Width = w,
                Height = h,
                Timestamp = DateTime.Now
            });
        }

        // The rescan below re-walks the whole 8400-anchor tensor on the zero-detection path, for
        // every region group of every cycle. Ask the sink before paying for it.
        if (Log.IsEnabled(LogEventLevel.Debug))
        {
            var highestConf = results.Count > 0
                ? results.Max(r => r.Confidence)
                : 0f;

            if (results.Count == 0 && numDetections > 0)
            {
                for (int i = 0; i < numDetections; i++)
                {
                    for (int c = 0; c < numClasses; c++)
                    {
                        var conf = output[(4 + c) * numDetections + i];
                        if (conf > highestConf) highestConf = conf;
                    }
                }
            }

            var classIds = results.Count > 0
                ? string.Join(",", results.Select(r => $"{r.ClassId}({r.Confidence:F2})"))
                : "none";
            Log.Debug("ParseYoloOutput: {Results} results, highestConf={Conf:F4}, classIds=[{ClassIds}], {Total} detections, numClasses={Classes}",
                results.Count, highestConf, classIds, numDetections, numClasses);
        }

        return results;
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

