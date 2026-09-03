using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FilmRefit.App.Services;
using FilmRefit.App.ViewModels;

namespace FilmRefit.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var runtime = RuntimePaths.Discover();
            var mediaProbe = new MediaProbeService();
            var playbackService = new PreviewPlaybackService();
            var viewModel = new MainWindowViewModel(
                runtime,
                mediaProbe,
                new TranscodeService(runtime),
                playbackService,
                new AvaloniaUserInteractionService());
            desktop.ShutdownRequested += (_, _) =>
            {
                viewModel.Dispose();
                playbackService.Dispose();
            };
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
