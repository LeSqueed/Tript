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
    private const int ModelInputSize = DetectionModelLoader.ModelInputSize;
    private const int StopJoinTimeoutSeconds = 3;

    private const int OcrStopJoinSeconds = 3;

    private const int OcrSnapshotMaxAgeMs = 2500;

    private const int OcrPollIntervalMs = 100;

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
                (var definitions, session) = DetectionModelLoader.LoadCompatibleModel(gameId);
                modelLoaded = session is not null;

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

                var inference = session is null
                    ? null
                    : DetectionModelLoader.CreateInferenceState(session, definitions, gameId);
                var regionGroups = inference?.RegionGroups ?? [];
                var ocrRegionPlans = OcrRegionPlanner.Build(definitions,
                    ModelService.LoadRegionGroups(gameId));
                var grayscaleStrategy = DetectionFramePreprocessor.SelectGrayscaleStrategy(regionGroups);
                var divisor = DetectionCaptureSettings.ComputeFrameRateDivisor(
                    DetectionCaptureSettings.GetConfiguredOutputFps());

                var (subscribeWidth, subscribeHeight) =
                    DetectionCaptureSettings.ResolveSubscribeSize(ocrDefinitions.Count > 0);

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
                _inputBuffer = inference?.InputBuffer;
                _inputTensor = inference?.InputTensor;
                _inputContainer = inference?.InputContainer;
                _outputNames = inference?.OutputNames;
                _runOptions = runOptions;
                _regionGroups = regionGroups;
                _objectDefinitions = definitions
                    .Where(definition => definition.DetectionKind == DetectionKind.Object)
                    .ToList();
                _grayscaleStrategy = grayscaleStrategy;
                _numClasses = inference?.NumClasses ?? 0;
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

        if (ocrThread is not null && !ocrThread.Join(TimeSpan.FromSeconds(OcrStopJoinSeconds)))
            Log.Warning("VisualEventDetector: OCR worker for {GameId} did not exit within {TimeoutSeconds}s; it will finish and release in the background",
                _gameId, OcrStopJoinSeconds);

        DrainFrameQueue();

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
                    var matches = OcrFramePass.Run(recognizer, regionPlans, frame.Buffer, frame.Width,
                        frame.Height, token);
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
            var timestamp = DateTime.UtcNow;
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
                Log.Debug("VisualEventDetector: received {Count} live frame(s), latest {Width}x{Height}",
                    _diagnosticFrameCount, width, height);

            if (!queued)
                Log.Warning("VisualEventDetector: frame queue rejected a frame, dropping it");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VisualEventDetector: frame copy error");
        }
        finally
        {
            if (!queued && buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    private void DetectionLoop(CancellationToken ct)
    {
        var session = _session;
        if (session is null && !_ocrActive) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ct.WaitHandle.WaitOne(_detectionIntervalMs)) break;

                if (!_frameQueue.Reader.TryRead(out var frameData))
                {
                    Log.Debug("DetectionLoop: no frame available");
                    continue;
                }

                while (_frameQueue.Reader.TryRead(out var newer))
                {
                    frameData.ReturnBuffer();
                    frameData = newer;
                }

                Log.Debug("DetectionLoop: processing frame {W}x{H}", frameData.Width, frameData.Height);

                try
                {
                    var fW = frameData.Width;
                    var fH = frameData.Height;

                    if (DetectionFramePreprocessor.IsNearBlack(frameData.Buffer, fW, fH))
                    {
                        Log.Debug("DetectionLoop: skipping near-black frame");

                        DetectionsAvailable?.Invoke(new DetectionBatch { FrameTimestamp = frameData.Timestamp });
                        continue;
                    }

                    List<DetectionResult> allResults = session is null
                        ? []
                        : DetectObjects(session, frameData);

                    Log.Debug("DetectionLoop: {Count} results across {Groups} groups", allResults.Count, _regionGroups.Count);
                    if (allResults.Count == 0 && DateTime.UtcNow - _lastEmptyInferenceLog >= TimeSpan.FromSeconds(5))
                    {
                        _lastEmptyInferenceLog = DateTime.UtcNow;
                        Log.Information("DetectionLoop: processed live frame {W}x{H}; inference returned no detections",
                            fW, fH);
                    }

                    DetectionsAvailable?.Invoke(new DetectionBatch
                    {
                        FrameTimestamp = frameData.Timestamp,
                        ObjectDetections = allResults,
                        OcrMatches = TakeOcrMatches(),
                    });
                }
                finally
                {
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

    private List<DetectionResult> DetectObjects(InferenceSession session, FrameData frameData)
    {
        var allResults = new List<DetectionResult>();
        var fW = frameData.Width;
        var fH = frameData.Height;
        var frameGray = _grayscaleStrategy == GrayscaleStrategy.WholeFrameOnce
            ? DetectionFramePreprocessor.BgraToGray(frameData.Buffer, fW, fH)
            : null;

        try
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
        finally
        {
            if (frameGray != null) ArrayPool<byte>.Shared.Return(frameGray);
        }

        return allResults;
    }

    private List<OcrMatch> TakeOcrMatches()
    {
        var snapshot = _ocrSnapshot;
        return OcrFramePass.FreshMatches(snapshot?.Matches, snapshot?.CompletedAtUtc ?? default,
            DateTime.UtcNow, OcrSnapshotMaxAgeMs);
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
