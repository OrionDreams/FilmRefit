namespace FilmRefit.App.Services;

public sealed class TranscodeService
{
    private readonly FilmRefitRuntime _runtime;

    public TranscodeService(FilmRefitRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<ProcessResult> TranscodeAsync(
        string inputPath,
        TranscodeMode mode,
        Action<string>? log,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "--mode",
            mode == TranscodeMode.Proxy ? "proxy" : "hqx",
            inputPath
        };

        return ProcessRunner.RunAsync(
            TranscoderExecutable(),
            TranscoderArguments(arguments),
            _runtime.RepositoryRoot,
            log,
            log,
            _runtime.FfmpegDirectory,
            cancellationToken);
    }

    public Task<ProcessResult> RepairTimecodeAsync(
        string inputPath,
        Action<string>? log,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "--repair-timecode",
            "--replace",
            inputPath
        };

        return ProcessRunner.RunAsync(
            TranscoderExecutable(),
            TranscoderArguments(arguments),
            _runtime.RepositoryRoot,
            log,
            log,
            _runtime.FfmpegDirectory,
            cancellationToken);
    }

    private string TranscoderExecutable() =>
        _runtime.TranscoderExecutable ?? _runtime.PythonExecutable;

    private IEnumerable<string> TranscoderArguments(IEnumerable<string> arguments)
    {
        if (_runtime.TranscoderExecutable is null)
        {
            yield return _runtime.TranscoderScript;
        }

        foreach (var argument in arguments)
        {
            yield return argument;
        }
    }
}

public enum TranscodeMode
{
    Proxy,
    Mezzanine
}
