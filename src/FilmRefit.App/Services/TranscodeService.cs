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
            _runtime.TranscoderScript,
            "--mode",
            mode == TranscodeMode.Proxy ? "proxy" : "hqx",
            inputPath
        };

        return ProcessRunner.RunAsync(
            _runtime.PythonExecutable,
            arguments,
            _runtime.RepositoryRoot,
            log,
            log,
            cancellationToken);
    }
}

public enum TranscodeMode
{
    Proxy,
    Mezzanine
}
