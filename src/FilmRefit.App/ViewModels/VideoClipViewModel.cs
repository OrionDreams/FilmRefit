using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmRefit.App.Services;

namespace FilmRefit.App.ViewModels;

public partial class VideoClipViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _status = "Queued";

    [ObservableProperty]
    private string _resolution = "--";

    [ObservableProperty]
    private string _frameRate = "--";

    [ObservableProperty]
    private string _duration = "--";

    [ObservableProperty]
    private string _videoCodec = "--";

    [ObservableProperty]
    private string _audioCodec = "--";

    [ObservableProperty]
    private string _pixelFormat = "--";

    [ObservableProperty]
    private string _bitDepth = "--";

    [ObservableProperty]
    private string _colorSpace = "--";

    [ObservableProperty]
    private string _timecode = "";

    [ObservableProperty]
    private string _camera = "";

    [ObservableProperty]
    private string _lens = "";

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private Bitmap? _previewFrame;

    public VideoClipViewModel(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Directory = System.IO.Path.GetDirectoryName(path) ?? "";
    }

    public string Path { get; }

    public string FileName { get; }

    public string Directory { get; }

    public string TimecodeDisplay => string.IsNullOrWhiteSpace(Timecode) ? "No timecode" : Timecode;

    public string CameraDisplay => string.IsNullOrWhiteSpace(Camera) ? "Unknown camera" : Camera;

    public string LensDisplay => string.IsNullOrWhiteSpace(Lens) ? "Unknown lens" : Lens;

    public void ApplyMetadata(VideoMetadata metadata)
    {
        Resolution = metadata.Resolution;
        FrameRate = metadata.FrameRate;
        Duration = metadata.Duration;
        VideoCodec = metadata.VideoCodec;
        AudioCodec = metadata.AudioCodec;
        PixelFormat = metadata.PixelFormat;
        BitDepth = metadata.BitDepth;
        ColorSpace = metadata.ColorSpace;
        Timecode = metadata.Timecode;
        Camera = metadata.Camera;
        Lens = metadata.Lens;
        OnPropertyChanged(nameof(TimecodeDisplay));
        OnPropertyChanged(nameof(CameraDisplay));
        OnPropertyChanged(nameof(LensDisplay));
    }
}
