using System.Runtime.InteropServices;

namespace FilmRefit.App;

public sealed record FilmRefitRuntime(
    string RepositoryRoot,
    string TranscoderScript,
    string PythonExecutable,
    string? TranscoderExecutable,
    string FfmpegExecutable,
    string FfprobeExecutable,
    string? FfmpegDirectory)
{
    public bool UsesBundledTranscoderExecutable => !string.IsNullOrWhiteSpace(TranscoderExecutable);
}

public static class RuntimePaths
{
    public static FilmRefitRuntime Discover()
    {
        var root = FindRepositoryRoot();
        return new FilmRefitRuntime(
            root,
            Path.Combine(root, "transcoder", "filmrefit.py"),
            FindPythonExecutable(root),
            FindTranscoderExecutable(root),
            FindRuntimeTool(root, "ffmpeg"),
            FindRuntimeTool(root, "ffprobe"),
            FindRuntimeToolDirectory(root));
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var script = Path.Combine(current, "transcoder", "filmrefit.py");
            var transcoder = Path.Combine(current, "runtime", "transcoder", TranscoderExecutableName());
            if (File.Exists(script) || File.Exists(transcoder))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        var workingDirectoryScript = Path.Combine(Environment.CurrentDirectory, "transcoder", "filmrefit.py");
        if (File.Exists(workingDirectoryScript))
        {
            return Environment.CurrentDirectory;
        }

        throw new DirectoryNotFoundException("Could not find FilmRefit's transcoder/filmrefit.py from the application directory.");
    }

    private static string FindPythonExecutable(string repositoryRoot)
    {
        foreach (var candidate in BundledPythonCandidates(repositoryRoot))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
    }

    private static string? FindTranscoderExecutable(string repositoryRoot)
    {
        var candidate = Path.Combine(repositoryRoot, "runtime", "transcoder", TranscoderExecutableName());
        return File.Exists(candidate) ? candidate : null;
    }

    private static string TranscoderExecutableName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "filmrefit-transcoder.exe"
            : "filmrefit-transcoder";

    private static string FindRuntimeTool(string repositoryRoot, string name)
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{name}.exe" : name;
        var candidate = Path.Combine(repositoryRoot, "runtime", "ffmpeg", executableName);
        return File.Exists(candidate) ? candidate : executableName;
    }

    private static string? FindRuntimeToolDirectory(string repositoryRoot)
    {
        var candidate = Path.Combine(repositoryRoot, "runtime", "ffmpeg");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static IEnumerable<string> BundledPythonCandidates(string repositoryRoot)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(repositoryRoot, "runtime", "python", "python.exe");
            yield return Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe");
            yield break;
        }

        yield return Path.Combine(repositoryRoot, "runtime", "python", "bin", "python3");
        yield return Path.Combine(repositoryRoot, "runtime", "python", "bin", "python");
        yield return Path.Combine(repositoryRoot, ".venv", "bin", "python3");
        yield return Path.Combine(repositoryRoot, ".venv", "bin", "python");
    }
}
