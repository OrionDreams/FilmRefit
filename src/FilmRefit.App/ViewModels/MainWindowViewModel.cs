using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmRefit.App.Services;

namespace FilmRefit.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".mxf"
    };

    private readonly FilmRefitRuntime _runtime;
    private readonly MediaProbeService _mediaProbe;
    private readonly TranscodeService _transcodeService;
    private readonly PreviewPlaybackService _playbackService;
    private readonly IUserInteractionService _userInteraction;
    private readonly ObservableCollection<VideoClipViewModel> _clips = [];
    private readonly Stopwatch _playbackClock = new();
    private CancellationTokenSource? _renderCancellation;
    private CancellationTokenSource? _seekDebounceCancellation;
    private WriteableBitmap? _frontBuffer;
    private WriteableBitmap? _backBuffer;
    private TimeSpan _playbackBasePosition = TimeSpan.Zero;
    private bool _suppressSeekRequest;

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
    private double _thumbnailSize = 128;

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
    private string _selectionText = "No files selected";

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
        foreach (var item in _clips)
        {
            item.IsOpen = ReferenceEquals(item, clip);
        }

        SelectedClip = clip;
        await StopPlaybackAsync();
        ResetPlaybackState(clip);
        if (clip.PreviewFrame is null)
        {
            clip.PreviewFrame = await _mediaProbe.GenerateFrameAsync(clip.Path, 1280);
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
            var clip = new VideoClipViewModel(path);
            clip.PropertyChanged += OnClipPropertyChanged;
            _clips.Add(clip);
        }

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
            var metadata = await _mediaProbe.ProbeAsync(clip.Path);
            var thumbnail = await _mediaProbe.GenerateFrameAsync(clip.Path, 256);
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
        try
        {
            foreach (var clip in clips)
            {
                clip.Status = mode == TranscodeMode.Proxy ? "Creating proxy" : "Creating mezzanine";
                AppendLog("");
                AppendLog($"{clip.FileName}: starting {mode.ToString().ToLowerInvariant()}");
                var result = await _transcodeService.TranscodeAsync(clip.Path, mode, AppendLog);
                clip.Status = result.ExitCode == 0 ? "Done" : "Failed";
                if (result.ExitCode != 0)
                {
                    AppendLog($"{clip.FileName}: failed with exit code {result.ExitCode}");
                }
            }
        }
        finally
        {
            IsProcessing = false;
            RefreshActionState();
        }
    }

    private IReadOnlyList<VideoClipViewModel> GetActionClips()
    {
        if (SelectedClips.Count > 0)
        {
            return SelectedClips.ToList();
        }

        return SelectedClip is null ? [] : [SelectedClip];
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
        foreach (var clip in _clips.Where(clip => clip.IsSelected))
        {
            SelectedClips.Add(clip);
        }

        SelectedClips.CollectionChanged += OnSelectedClipsChanged;
        RefreshActionState();
    }

    private void RefreshActionState()
    {
        var count = GetActionClips().Count;
        SelectionText = count == 0 ? "No files selected" : $"{count} file(s) targeted";
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
        foreach (var group in _clips
                     .OrderBy(clip => clip.Directory, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(clip => clip.FileName, StringComparer.OrdinalIgnoreCase)
                     .GroupBy(clip => clip.Directory))
        {
            DirectoryGroups.Add(new DirectoryGroupViewModel(group.Key, group));
        }
    }

    private static IEnumerable<string> EnumerateVideoFiles(string directory, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, "*", option)
            .Where(path => SupportedVideoExtensions.Contains(Path.GetExtension(path)));
    }

    private void AppendLog(string line)
    {
        LogText = string.IsNullOrEmpty(LogText) ? line : $"{LogText}{Environment.NewLine}{line}";
    }

    private async Task EnsurePlaybackStartedAsync(VideoClipViewModel clip, TimeSpan position)
    {
        if (!string.Equals(_playbackService.State.Path, clip.Path, StringComparison.Ordinal)
            || Math.Abs((_playbackService.State.StartPosition - position).TotalSeconds) > 0.05)
        {
            await _playbackService.StartAsync(clip.Path, CreatePlaybackMetadata(clip), ClampPlaybackPosition(position));
        }
    }

    private void StartRenderLoop()
    {
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = new CancellationTokenSource();
        _playbackBasePosition = TimeSpan.FromSeconds(PlaybackPositionSeconds);
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

                    await DisplayFrameAsync(frame);
                    UpdatePlaybackPosition(frame.Position);
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
        await _playbackService.StartAsync(SelectedClip.Path, CreatePlaybackMetadata(SelectedClip), position);

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
                await DisplayFrameAsync(frame);
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

    private Task DisplayFrameAsync(PreviewFrame frame)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            var bitmap = GetNextBitmap(frame.Width, frame.Height);
            using (var locked = bitmap.Lock())
            {
                CopyFrame(frame.Buffer, frame.Length, locked.Address, locked.RowBytes, frame.Width, frame.Height);
            }

            if (SelectedClip is not null)
            {
                SelectedClip.PreviewFrame = bitmap;
            }
        }).GetTask();
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
        Dispatcher.UIThread.Post(() =>
        {
            _suppressSeekRequest = true;
            PlaybackPositionSeconds = Math.Clamp(position.TotalSeconds, 0, PlaybackDurationSeconds);
            _suppressSeekRequest = false;
        });
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
