using Avalonia;
using Avalonia.Controls;
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
            var mediaProbe = new MediaProbeService(runtime);
            var playbackService = new PreviewPlaybackService(runtime);
            var viewModel = new MainWindowViewModel(
                runtime,
                mediaProbe,
                new TranscodeService(runtime),
                playbackService,
                new AvaloniaUserInteractionService(),
                new ResolvePluginService(runtime));
            var cleanupStarted = 0;
            void Cleanup()
            {
                if (Interlocked.Exchange(ref cleanupStarted, 1) != 0)
                {
                    return;
                }

                _ = Task.Run(() =>
                {
                    viewModel.Dispose();
                    playbackService.Dispose();
                });
            }

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) => Cleanup();

            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };
            mainWindow.Closing += (_, _) =>
            {
                Cleanup();
            };
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
