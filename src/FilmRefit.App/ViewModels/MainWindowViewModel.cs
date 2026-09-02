using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private readonly IUserInteractionService _userInteraction;
    private readonly ObservableCollection<VideoClipViewModel> _clips = [];

    [ObservableProperty]
    private VideoClipViewModel? _selectedClip;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProxyCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateMezzanineCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddDirectoryRecursiveCommand))]
    private bool _isProcessing;

    [ObservableProperty]
    private double _thumbnailSize = 128;

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
        IUserInteractionService userInteraction)
    {
        _runtime = runtime;
        _mediaProbe = mediaProbe;
        _transcodeService = transcodeService;
        _userInteraction = userInteraction;
        SelectedClips.CollectionChanged += OnSelectedClipsChanged;
    }

    public ObservableCollection<DirectoryGroupViewModel> DirectoryGroups { get; } = [];

    public ObservableCollection<VideoClipViewModel> SelectedClips { get; } = [];

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
        if (clip.PreviewFrame is null)
        {
            clip.PreviewFrame = await _mediaProbe.GenerateFrameAsync(clip.Path, 1280);
        }
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
}
