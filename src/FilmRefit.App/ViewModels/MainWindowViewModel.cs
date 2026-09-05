using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmRefit.App.Services;

namespace FilmRefit.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int MaximumDisplayedLogLines = 2000;

    private static readonly TimeSpan MinimumPreviewRenderInterval = TimeSpan.FromSeconds(1.0 / 30);
    private static readonly TimeSpan TranscodeLogFlushInterval = TimeSpan.FromMilliseconds(100);
    private static readonly Regex FfmpegProgressRegex = new(
        @"(?:^|\s)frame=\s*(?<frame>\d+).*?\btime=(?<time>\d+:\d{2}:\d{2}(?:\.\d+)?).*?\bspeed=\s*(?<speed>\d+(?:\.\d+)?)x",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FfmpegDurationRegex = new(
        @"Duration:\s*(?<duration>\d+:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".mxf",
        ".lrf"
    };

    private readonly FilmRefitRuntime _runtime;
    private readonly MediaProbeService _mediaProbe;
    private readonly TranscodeService _transcodeService;
    private readonly PreviewPlaybackService _playbackService;
    private readonly IUserInteractionService _userInteraction;
    private readonly ObservableCollection<VideoClipViewModel> _clips = [];
    private readonly object _pendingTranscodeLogLock = new();
    private readonly Queue<string> _logLines = new();
    private readonly List<string> _pendingTranscodeLogLines = [];
    private readonly Stopwatch _playbackClock = new();
    private readonly Stopwatch _batchClock = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private CancellationTokenSource? _renderCancellation;
    private CancellationTokenSource? _seekDebounceCancellation;
    private WriteableBitmap? _frontBuffer;
    private WriteableBitmap? _backBuffer;
    private TimeSpan _playbackBasePosition = TimeSpan.Zero;
    private TimeSpan _lastPreviewRenderElapsed = TimeSpan.MinValue;
    private VideoClipViewModel? _processingClip;
    private double? _processingDurationSeconds;
    private int _processingClipIndex;
    private int _processingClipCount;
    private bool _suppressSeekRequest;
    private bool _transcodeLogFlushScheduled;
    private bool _disposed;

    [ObservableProperty]
    private VideoClipViewModel? _selectedClip;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProxyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateMezzanineCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddDirectoryRecursiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepPreviousFrameCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepNextFrameCommand))]
    private bool _isProcessing;

    [ObservableProperty]
    private double _thumbnailSize = 64;

    [ObservableProperty]
    private double _playbackPositionSeconds;

    [ObservableProperty]
    private double _playbackDurationSeconds;

    [ObservableProperty]
    private string _playbackPositionText = "0:00";

    [ObservableProperty]
    private string _playbackDurationText = "0:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepPreviousFrameCommand))]
    [NotifyCanExecuteChangedFor(nameof(StepNextFrameCommand))]
    private bool _canUsePlayback;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
    private bool _isPlaybackRunning;

    [ObservableProperty]
    private string _playPauseText = "Play";

    [ObservableProperty]
    private double _playbackRate = 1;

    [ObservableProperty]
    private string _playbackStatusText = "Load a clip to preview.";

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private string _statusText = "Load footage to begin.";

    [ObservableProperty]
    private string _proxyButtonText = "Create proxy";

    [ObservableProperty]
    private string _mezzanineButtonText = "Create mezzanine";

    [ObservableProperty]
    private string _selectionText = "0 selected";

    [ObservableProperty]
    private string _progressCurrentFileText = "No active transcode";

    [ObservableProperty]
    private string _currentFileProgressText = "Current file: 0%";

    [ObservableProperty]
    private string _currentFileEtaText = "ETA --";

    [ObservableProperty]
    private double _currentFileProgressValue;

    [ObservableProperty]
    private string _overallProgressText = "Batch: 0 of 0";

    [ObservableProperty]
    private string _overallEtaText = "ETA --";

    [ObservableProperty]
    private double _overallProgressValue;

    public MainWindowViewModel(
        FilmRefitRuntime runtime,
        MediaProbeService mediaProbe,
        TranscodeService transcodeService,
        PreviewPlaybackService playbackService,
        IUserInteractionService userInteraction)
    {
        _runtime = runtime;
        _mediaProbe = mediaProbe;
        _transcodeService = transcodeService;
        _playbackService = playbackService;
        _userInteraction = userInteraction;
        SelectedClips.CollectionChanged += OnSelectedClipsChanged;
    }

    public ObservableCollection<DirectoryGroupViewModel> DirectoryGroups { get; } = [];

    public ObservableCollection<VideoClipViewModel> SelectedClips { get; } = [];

    public IReadOnlyList<double> PlaybackRates { get; } = [0.5, 1, 2, 4, 8];

    public bool HasSelectedClip => SelectedClip is not null;

    public string RuntimeText => $"Python: {_runtime.PythonExecutable} | Transcoder: {_runtime.TranscoderScript}";

    [RelayCommand(CanExecute = nameof(CanLoadFiles))]
    private async Task AddFilesAsync()
    {
        var paths = await _userInteraction.OpenVideoFilesAsync();
        await AddClipPathsAsync(paths);
    }

    [RelayCommand(CanExecute = nameof(CanLoadFiles))]
    private async Task AddDirectoryAsync()
    {
        var directory = await _userInteraction.ChooseFolderAsync("Load video directory");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        await AddClipPathsAsync(EnumerateVideoFiles(directory, false));
    }

    [RelayCommand(CanExecute = nameof(CanLoadFiles))]
    private async Task AddDirectoryRecursiveAsync()
    {
        var directory = await _userInteraction.ChooseFolderAsync("Load video directory recursively");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        await AddClipPathsAsync(EnumerateVideoFiles(directory, true));
    }

    [RelayCommand]
    private async Task OpenClipAsync(VideoClipViewModel clip)
    {
        try
        {
            foreach (var item in _clips)
            {
                item.IsOpen = ReferenceEquals(item, clip);
            }

            SelectedClip = clip;
            await StopPlaybackAsync();
            ResetPlaybackState(clip);
            if (clip.PreviewFrame is null)
            {
                clip.PreviewFrame = await _mediaProbe.GenerateFrameAsync(clip.Path, 1280, _shutdownCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanTogglePlayback))]
    private async Task TogglePlaybackAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }

        if (IsPlaybackRunning)
        {
            PausePlayback();
            return;
        }

        await EnsurePlaybackStartedAsync(SelectedClip, TimeSpan.FromSeconds(PlaybackPositionSeconds));
        StartRenderLoop();
    }

    [RelayCommand(CanExecute = nameof(CanUsePlaybackControls))]
    private async Task StepPreviousFrameAsync()
    {
        await StepFramesAsync(-1);
    }

    [RelayCommand(CanExecute = nameof(CanUsePlaybackControls))]
    private async Task StepNextFrameAsync()
    {
        await StepFramesAsync(1);
    }

    [RelayCommand]
    private void RefreshSelection()
    {
        RebuildSelectedClips();
    }

    [RelayCommand(CanExecute = nameof(CanCreateOutput))]
    private Task CreateProxyAsync()
    {
        return RunTranscodeBatchAsync(TranscodeMode.Proxy);
    }

    [RelayCommand(CanExecute = nameof(CanCreateOutput))]
    private Task CreateMezzanineAsync()
    {
        return RunTranscodeBatchAsync(TranscodeMode.Mezzanine);
    }

    private bool CanLoadFiles() => !IsProcessing;

    private bool CanCreateOutput() => !IsProcessing && GetActionClips().Count > 0;

    private bool CanTogglePlayback() => CanUsePlayback && !IsProcessing;

    private bool CanUsePlaybackControls() => CanUsePlayback && !IsProcessing;

    private async Task AddClipPathsAsync(IEnumerable<string> paths)
    {
        var newPaths = paths
            .Where(path => SupportedVideoExtensions.Contains(Path.GetExtension(path)))
            .Where(path => _clips.All(clip => !string.Equals(clip.Path, path, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in newPaths)
        {
            AddClip(path);
        }

        AddKnownOutputClips(loadDetails: false);
        RegroupClips();
        StatusText = newPaths.Count == 0
            ? "No new supported video files were found."
            : $"Loaded {newPaths.Count} new file(s).";
        AppendLog(StatusText);

        foreach (var clip in _clips.Where(clip => clip.Status == "Queued").ToList())
        {
            _ = LoadClipDetailsAsync(clip);
        }

        if (SelectedClip is null && _clips.Count > 0)
        {
            await OpenClipAsync(_clips[0]);
        }
    }

    private async Task LoadClipDetailsAsync(VideoClipViewModel clip)
    {
        try
        {
            clip.Status = "Reading metadata";
            var metadata = await _mediaProbe.ProbeAsync(clip.Path, _shutdownCancellation.Token);
            var thumbnail = await _mediaProbe.GenerateFrameAsync(clip.Path, 256, _shutdownCancellation.Token);
            Dispatcher.UIThread.Post(() =>
            {
                clip.ApplyMetadata(metadata);
                clip.Thumbnail = thumbnail;
                clip.Status = "Ready";
                if (ReferenceEquals(SelectedClip, clip))
                {
                    ResetPlaybackState(clip);
                }
            });
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exc)
        {
            Dispatcher.UIThread.Post(() =>
            {
                clip.Status = "Metadata failed";
                AppendLog($"{clip.FileName}: {exc.Message}");
            });
        }
    }

    private async Task RunTranscodeBatchAsync(TranscodeMode mode)
    {
        var clips = GetActionClips();
        if (clips.Count == 0)
        {
            return;
        }

        if (IsPlaybackRunning)
        {
            PausePlayback();
        }

        IsProcessing = true;
        StartProgressBatch(clips.Count);
        try
        {
            for (var index = 0; index < clips.Count; index++)
            {
                var clip = clips[index];
                StartProgressFile(clip, index + 1, clips.Count);
                clip.Status = mode == TranscodeMode.Proxy ? "Creating proxy" : "Creating mezzanine";
                AppendLog("");
                AppendLog($"{clip.FileName}: starting {mode.ToString().ToLowerInvariant()}");
                var result = await _transcodeService.TranscodeAsync(clip.Path, mode, OnTranscodeLogLine, _shutdownCancellation.Token);
                await Dispatcher.UIThread.InvokeAsync(FlushPendingTranscodeLogLines);
                clip.Status = result.ExitCode == 0 ? "Done" : "Failed";
                if (result.ExitCode != 0)
                {
                    AppendLog($"{clip.FileName}: failed with exit code {result.ExitCode}");
                }
                else
                {
                    AddClipIfNew(TranscoderOutputNaming.BuildOutputPath(clip.Path, mode), loadDetails: true);
                    AddKnownOutputClips(loadDetails: true);
                    RegroupClips();
                }

                FinishProgressFile(result.ExitCode == 0);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            IsProcessing = false;
            FinishProgressBatch();
            RefreshActionState();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownCancellation.Cancel();
        _renderCancellation?.Cancel();
        _seekDebounceCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _seekDebounceCancellation?.Dispose();
        _shutdownCancellation.Dispose();
    }

    private IReadOnlyList<VideoClipViewModel> GetActionClips()
    {
        var selectedOriginals = SelectedClips.Where(clip => clip.IsOriginal).ToList();
        if (selectedOriginals.Count > 0)
        {
            return selectedOriginals;
        }

        return SelectedClip switch
        {
            { IsOriginal: true } clip => [clip],
            { ParentClip: not null } clip => [clip.ParentClip],
            _ => []
        };
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(VideoClipViewModel.IsSelected))
        {
            RebuildSelectedClips();
        }
    }

    private void OnSelectedClipsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RefreshActionState();
    }

    private void RebuildSelectedClips()
    {
        SelectedClips.CollectionChanged -= OnSelectedClipsChanged;
        SelectedClips.Clear();
        foreach (var clip in _clips.Where(clip => clip.IsSelected && clip.IsOriginal))
        {
            SelectedClips.Add(clip);
        }

        SelectedClips.CollectionChanged += OnSelectedClipsChanged;
        RefreshActionState();
    }

    private void RefreshActionState()
    {
        var count = GetActionClips().Count;
        SelectionText = $"{count} selected";
        ProxyButtonText = count <= 1 ? "Create proxy" : $"Create {count} proxies";
        MezzanineButtonText = count <= 1 ? "Create mezzanine" : $"Create {count} mezzanines";
        CreateProxyCommand.NotifyCanExecuteChanged();
        CreateMezzanineCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedClipChanged(VideoClipViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedClip));
        RefreshActionState();
    }

    partial void OnPlaybackPositionSecondsChanged(double value)
    {
        PlaybackPositionText = FormatDuration(TimeSpan.FromSeconds(Math.Clamp(value, 0, PlaybackDurationSeconds)));
        if (_suppressSeekRequest || SelectedClip is null || !CanUsePlayback)
        {
            return;
        }

        _ = DebounceSeekAsync(value);
    }

    partial void OnPlaybackDurationSecondsChanged(double value)
    {
        PlaybackDurationText = FormatDuration(TimeSpan.FromSeconds(value));
    }

    partial void OnIsPlaybackRunningChanged(bool value)
    {
        PlayPauseText = value ? "Pause" : "Play";
    }

    partial void OnPlaybackRateChanged(double value)
    {
        if (value <= 0)
        {
            PlaybackRate = 1;
        }
    }

    private void RegroupClips()
    {
        DirectoryGroups.Clear();
        foreach (var clip in _clips)
        {
            clip.ParentClip = null;
            clip.OutputClips.Clear();
        }

        foreach (var group in _clips.GroupBy(clip => clip.Directory)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var clips = group.ToList();
            var originalsByStem = clips
                .Where(clip => clip.IsOriginal)
                .GroupBy(clip => clip.SourceStem, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(stemGroup => stemGroup.Key, stemGroup => stemGroup.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var output in clips.Where(clip => clip.IsOutput).OrderBy(GetOutputSortOrder).ThenBy(clip => clip.FileName, StringComparer.OrdinalIgnoreCase))
            {
                if (!originalsByStem.TryGetValue(output.SourceStem, out var original))
                {
                    continue;
                }

                output.ParentClip = original;
                original.OutputClips.Add(output);
            }

            var displayClips = clips
                .Where(clip => clip.IsOriginal || clip.ParentClip is null)
                .OrderBy(clip => clip.SourceStem, StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetOutputSortOrder)
                .ThenBy(clip => clip.FileName, StringComparer.OrdinalIgnoreCase);

            DirectoryGroups.Add(new DirectoryGroupViewModel(group.Key, displayClips));
        }
    }

    private VideoClipViewModel AddClip(string path)
    {
        var clip = new VideoClipViewModel(path);
        clip.PropertyChanged += OnClipPropertyChanged;
        _clips.Add(clip);
        return clip;
    }

    private VideoClipViewModel? AddClipIfNew(string path, bool loadDetails)
    {
        if (!File.Exists(path)
            || !SupportedVideoExtensions.Contains(Path.GetExtension(path))
            || _clips.Any(clip => string.Equals(clip.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var clip = AddClip(path);
        if (loadDetails)
        {
            _ = LoadClipDetailsAsync(clip);
        }

        return clip;
    }

    private void AddKnownOutputClips(bool loadDetails)
    {
        foreach (var original in _clips.Where(clip => clip.IsOriginal).ToList())
        {
            AddClipIfNew(FindExistingPath(TranscoderOutputNaming.BuildDjiProxyPath(original.Path)), loadDetails);
            AddClipIfNew(TranscoderOutputNaming.BuildOutputPath(original.Path, TranscodeMode.Proxy), loadDetails);
            AddClipIfNew(TranscoderOutputNaming.BuildOutputPath(original.Path, TranscodeMode.Mezzanine), loadDetails);
        }
    }

    private static string FindExistingPath(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return path;
        }

        var fileName = Path.GetFileName(path);
        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(candidate => string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase))
            ?? path;
    }

    private static int GetOutputSortOrder(VideoClipViewModel clip)
    {
        return clip.OutputKind switch
        {
            TranscoderOutputKind.Original => 0,
            TranscoderOutputKind.DjiProxy => 1,
            TranscoderOutputKind.Proxy => 2,
            TranscoderOutputKind.Mezzanine => 3,
            _ => 4
        };
    }

    private static IEnumerable<string> EnumerateVideoFiles(string directory, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, "*", option)
            .Where(path => SupportedVideoExtensions.Contains(Path.GetExtension(path)));
    }

    private void AppendLog(string line)
    {
        AppendLogLines([line]);
    }

    private void AppendLogLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            _logLines.Enqueue(line);
        }

        while (_logLines.Count > MaximumDisplayedLogLines)
        {
            _logLines.Dequeue();
        }

        LogText = string.Join(Environment.NewLine, _logLines);
    }

    private void OnTranscodeLogLine(string line)
    {
        lock (_pendingTranscodeLogLock)
        {
            _pendingTranscodeLogLines.Add(line);
            if (_transcodeLogFlushScheduled)
            {
                return;
            }

            _transcodeLogFlushScheduled = true;
        }

        Dispatcher.UIThread.Post(() =>
            DispatcherTimer.RunOnce(FlushPendingTranscodeLogLines, TranscodeLogFlushInterval),
            DispatcherPriority.Background);
    }

    private void FlushPendingTranscodeLogLines()
    {
        string[] lines;
        lock (_pendingTranscodeLogLock)
        {
            lines = _pendingTranscodeLogLines.ToArray();
            _pendingTranscodeLogLines.Clear();
            _transcodeLogFlushScheduled = false;
        }

        if (lines.Length == 0 || _disposed)
        {
            return;
        }

        AppendLogLines(lines);
        foreach (var line in lines)
        {
            UpdateTranscodeProgress(line);
        }
    }

    private void StartProgressBatch(int clipCount)
    {
        _batchClock.Restart();
        _processingClip = null;
        _processingClipIndex = 0;
        _processingClipCount = clipCount;
        CurrentFileProgressValue = 0;
        OverallProgressValue = 0;
        ProgressCurrentFileText = "Preparing transcode";
        CurrentFileProgressText = "Current file: 0%";
        CurrentFileEtaText = "ETA --";
        OverallProgressText = clipCount <= 1 ? "Batch: 0%" : $"Batch: 0 of {clipCount}";
        OverallEtaText = "ETA --";
    }

    private void StartProgressFile(VideoClipViewModel clip, int index, int count)
    {
        _processingClip = clip;
        _processingDurationSeconds = clip.DurationSeconds;
        _processingClipIndex = index;
        _processingClipCount = count;
        CurrentFileProgressValue = 0;
        ProgressCurrentFileText = count <= 1
            ? clip.FileName
            : $"{clip.FileName} ({index} of {count})";
        CurrentFileProgressText = FormatCurrentFileProgressText("0%");
        CurrentFileEtaText = "ETA --";
        UpdateOverallProgress(0);
    }

    private void FinishProgressFile(bool succeeded)
    {
        if (_processingClip is null)
        {
            return;
        }

        CurrentFileProgressValue = succeeded ? 100 : CurrentFileProgressValue;
        CurrentFileProgressText = FormatCurrentFileProgressText(succeeded ? "100%" : "failed");
        CurrentFileEtaText = succeeded ? "ETA 0:00" : "ETA --";
        UpdateOverallProgress(succeeded ? 1 : CurrentFileProgressValue / 100);
    }

    private void FinishProgressBatch()
    {
        _batchClock.Stop();
        if (_shutdownCancellation.IsCancellationRequested)
        {
            return;
        }

        if (_processingClipCount > 0 && OverallProgressValue >= 100)
        {
            ProgressCurrentFileText = "Transcode complete";
            OverallEtaText = "ETA 0:00";
        }
    }

    private void UpdateTranscodeProgress(string line)
    {
        var durationMatch = FfmpegDurationRegex.Match(line);
        if (durationMatch.Success && TryParseFfmpegTime(durationMatch.Groups["duration"].Value, out var duration))
        {
            _processingDurationSeconds = duration.TotalSeconds;
        }

        if (_processingDurationSeconds is not > 0)
        {
            return;
        }

        var match = FfmpegProgressRegex.Match(line);
        if (!match.Success)
        {
            return;
        }

        if (!TryParseFfmpegTime(match.Groups["time"].Value, out var encodedTime))
        {
            return;
        }

        var durationSeconds = _processingDurationSeconds.Value;
        var encodedSeconds = Math.Clamp(encodedTime.TotalSeconds, 0, durationSeconds);
        var fileFraction = durationSeconds <= 0 ? 0 : encodedSeconds / durationSeconds;
        CurrentFileProgressValue = Math.Clamp(fileFraction * 100, 0, 100);
        CurrentFileProgressText = FormatCurrentFileProgressText($"{CurrentFileProgressValue:0}%");

        if (double.TryParse(match.Groups["speed"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            && speed > 0)
        {
            CurrentFileEtaText = $"ETA {FormatDuration(TimeSpan.FromSeconds((durationSeconds - encodedSeconds) / speed))}";
        }
        else
        {
            CurrentFileEtaText = "ETA --";
        }

        UpdateOverallProgress(fileFraction);
    }

    private string FormatCurrentFileProgressText(string status)
    {
        return _processingClip is null
            ? $"Current file: {status}"
            : $"{_processingClip.FileName}: {status}";
    }

    private void UpdateOverallProgress(double currentFileFraction)
    {
        if (_processingClipCount <= 0)
        {
            OverallProgressValue = 0;
            OverallProgressText = "Batch: 0 of 0";
            OverallEtaText = "ETA --";
            return;
        }

        var completedFiles = Math.Max(0, _processingClipIndex - 1);
        var batchFraction = Math.Clamp((completedFiles + Math.Clamp(currentFileFraction, 0, 1)) / _processingClipCount, 0, 1);
        OverallProgressValue = batchFraction * 100;
        OverallProgressText = _processingClipCount <= 1
            ? $"Batch: {OverallProgressValue:0}%"
            : $"Batch: {completedFiles + currentFileFraction:0.0} of {_processingClipCount}";

        OverallEtaText = batchFraction > 0 && _batchClock.IsRunning
            ? $"ETA {FormatDuration(TimeSpan.FromSeconds(_batchClock.Elapsed.TotalSeconds / batchFraction - _batchClock.Elapsed.TotalSeconds))}"
            : "ETA --";
    }

    private static bool TryParseFfmpegTime(string value, out TimeSpan time)
    {
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time);
    }

    private async Task EnsurePlaybackStartedAsync(VideoClipViewModel clip, TimeSpan position)
    {
        if (!string.Equals(_playbackService.State.Path, clip.Path, StringComparison.Ordinal)
            || Math.Abs((_playbackService.State.StartPosition - position).TotalSeconds) > 0.05)
        {
            await _playbackService.StartAsync(
                clip.Path,
                CreatePlaybackMetadata(clip),
                ClampPlaybackPosition(position),
                cancellationToken: _shutdownCancellation.Token);
        }
    }

    private void StartRenderLoop()
    {
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = new CancellationTokenSource();
        _playbackBasePosition = TimeSpan.FromSeconds(PlaybackPositionSeconds);
        _lastPreviewRenderElapsed = TimeSpan.MinValue;
        _playbackClock.Restart();
        IsPlaybackRunning = true;
        PlaybackStatusText = $"Playing at {PlaybackRate.ToString("0.##", CultureInfo.InvariantCulture)}x";
        _ = RenderPlaybackAsync(_renderCancellation.Token);
    }

    private void PausePlayback()
    {
        _renderCancellation?.Cancel();
        _playbackClock.Stop();
        IsPlaybackRunning = false;
        PlaybackStatusText = "Paused";
    }

    private async Task StopPlaybackAsync()
    {
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = null;
        _seekDebounceCancellation?.Cancel();
        _seekDebounceCancellation?.Dispose();
        _seekDebounceCancellation = null;
        _playbackClock.Reset();
        IsPlaybackRunning = false;
        await _playbackService.StopAsync();
    }

    private async Task RenderPlaybackAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _playbackService.Frames.ReadAsync(cancellationToken);
                try
                {
                    var frameDuration = _playbackService.State.FrameDuration;
                    while (_playbackService.Frames.TryRead(out var newer))
                    {
                        frame.Dispose();
                        frame = newer;
                        if (!ShouldDropFrame(frame.Position, frameDuration))
                        {
                            break;
                        }
                    }

                    var targetElapsed = TimeSpan.FromTicks(
                        (long)((frame.Position - _playbackBasePosition).Ticks / Math.Max(PlaybackRate, 0.01)));
                    var delay = targetElapsed - _playbackClock.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }

                    if (ShouldRenderPreviewFrame())
                    {
                        await DisplayFrameAsync(frame, cancellationToken);
                        _lastPreviewRenderElapsed = _playbackClock.Elapsed;
                        UpdatePlaybackPosition(frame.Position);
                    }
                }
                finally
                {
                    frame.Dispose();
                }

                if (PlaybackDurationSeconds > 0 && PlaybackPositionSeconds >= PlaybackDurationSeconds)
                {
                    PausePlayback();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsPlaybackRunning = false;
                PlaybackStatusText = "Preview ended";
            });
        }
        catch (Exception exc)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsPlaybackRunning = false;
                PlaybackStatusText = $"Preview failed: {exc.Message}";
                AppendLog(PlaybackStatusText);
            });
        }
    }

    private bool ShouldDropFrame(TimeSpan framePosition, TimeSpan frameDuration)
    {
        var playbackPosition = _playbackBasePosition + TimeSpan.FromTicks((long)(_playbackClock.Elapsed.Ticks * Math.Max(PlaybackRate, 0.01)));
        return framePosition < playbackPosition - frameDuration;
    }

    private bool ShouldRenderPreviewFrame()
    {
        return _lastPreviewRenderElapsed == TimeSpan.MinValue
            || _playbackClock.Elapsed - _lastPreviewRenderElapsed >= MinimumPreviewRenderInterval;
    }

    private async Task StepFramesAsync(int frameCount)
    {
        if (SelectedClip is null)
        {
            return;
        }

        PausePlayback();
        var frameDuration = TimeSpan.FromSeconds(1 / SelectedClip.FrameRateValue.GetValueOrDefault(24));
        var target = ClampPlaybackPosition(TimeSpan.FromSeconds(PlaybackPositionSeconds) + TimeSpan.FromTicks(frameDuration.Ticks * frameCount));
        await SeekAsync(target, displayOnly: true);
    }

    private async Task DebounceSeekAsync(double seconds)
    {
        _seekDebounceCancellation?.Cancel();
        _seekDebounceCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _seekDebounceCancellation = cancellation;

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellation.Token);
            await SeekAsync(TimeSpan.FromSeconds(seconds), displayOnly: !IsPlaybackRunning);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SeekAsync(TimeSpan position, bool displayOnly)
    {
        if (SelectedClip is null)
        {
            return;
        }

        var wasRunning = IsPlaybackRunning && !displayOnly;
        PausePlayback();
        position = ClampPlaybackPosition(position);
        UpdatePlaybackPosition(position);
        PlaybackStatusText = "Seeking";
        await _playbackService.StartAsync(
            SelectedClip.Path,
            CreatePlaybackMetadata(SelectedClip),
            position,
            cancellationToken: _shutdownCancellation.Token);

        if (wasRunning)
        {
            StartRenderLoop();
            return;
        }

        await DisplayNextDecodedFrameAsync();
        PlaybackStatusText = "Paused";
    }

    private async Task DisplayNextDecodedFrameAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await _playbackService.Frames.ReadAsync(timeout.Token);
            using (frame)
            {
                await DisplayFrameAsync(frame, timeout.Token);
                UpdatePlaybackPosition(frame.Position);
            }
        }
        catch (OperationCanceledException)
        {
            PlaybackStatusText = "No preview frame available";
        }
        catch (ChannelClosedException)
        {
            PlaybackStatusText = "No preview frame available";
        }
    }

    private Task DisplayFrameAsync(PreviewFrame frame, CancellationToken cancellationToken)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var bitmap = GetNextBitmap(frame.Width, frame.Height);
            using (var locked = bitmap.Lock())
            {
                CopyFrame(frame.Buffer, frame.Length, locked.Address, locked.RowBytes, frame.Width, frame.Height);
            }

            if (SelectedClip is not null)
            {
                SelectedClip.PreviewFrame = bitmap;
            }
        }, DispatcherPriority.Background, cancellationToken).GetTask();
    }

    private WriteableBitmap GetNextBitmap(int width, int height)
    {
        if (_frontBuffer is null || _frontBuffer.PixelSize.Width != width || _frontBuffer.PixelSize.Height != height)
        {
            _frontBuffer = CreateBitmap(width, height);
            _backBuffer = CreateBitmap(width, height);
            return _frontBuffer;
        }

        (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
        return _frontBuffer!;
    }

    private static WriteableBitmap CreateBitmap(int width, int height)
    {
        return new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
    }

    private static void CopyFrame(byte[] source, int length, nint destination, int rowBytes, int width, int height)
    {
        var sourceRowBytes = width * 4;
        if (rowBytes == sourceRowBytes)
        {
            Marshal.Copy(source, 0, destination, length);
            return;
        }

        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(source, row * sourceRowBytes, destination + row * rowBytes, sourceRowBytes);
        }
    }

    private void ResetPlaybackState(VideoClipViewModel clip)
    {
        PlaybackDurationSeconds = clip.DurationSeconds.GetValueOrDefault();
        UpdatePlaybackPosition(TimeSpan.Zero);
        CanUsePlayback = clip.DurationSeconds is > 0 && clip.FrameRateValue is > 0;
        PlaybackStatusText = CanUsePlayback ? "Ready to preview." : "Metadata required before playback.";
    }

    private void UpdatePlaybackPosition(TimeSpan position)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _suppressSeekRequest = true;
            PlaybackPositionSeconds = Math.Clamp(position.TotalSeconds, 0, PlaybackDurationSeconds);
            _suppressSeekRequest = false;
        }, DispatcherPriority.Background);
    }

    private TimeSpan ClampPlaybackPosition(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return PlaybackDurationSeconds > 0 && position.TotalSeconds > PlaybackDurationSeconds
            ? TimeSpan.FromSeconds(PlaybackDurationSeconds)
            : position;
    }

    private static VideoMetadata CreatePlaybackMetadata(VideoClipViewModel clip)
    {
        return new VideoMetadata(
            ParseResolutionPart(clip.Resolution, 0),
            ParseResolutionPart(clip.Resolution, 1),
            clip.FrameRateValue,
            clip.DurationSeconds,
            clip.Resolution,
            clip.FrameRate,
            clip.Duration,
            clip.VideoCodec,
            clip.AudioCodec,
            clip.PixelFormat,
            clip.BitDepth,
            clip.ColorSpace,
            clip.Timecode,
            clip.TimecodeSource,
            clip.Camera,
            clip.Lens);
    }

    private static int? ParseResolutionPart(string resolution, int index)
    {
        var parts = resolution.Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string FormatDuration(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
