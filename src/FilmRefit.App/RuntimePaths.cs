using System.Runtime.InteropServices;

namespace FilmRefit.App;

public sealed record FilmRefitRuntime(
    string RepositoryRoot,
    string TranscoderScript,
    string PythonExecutable);

public static class RuntimePaths
{
    public static FilmRefitRuntime Discover()
    {
        var root = FindRepositoryRoot();
        return new FilmRefitRuntime(
            root,
            Path.Combine(root, "transcoder", "filmrefit.py"),
            FindPythonExecutable(root));
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var script = Path.Combine(current, "transcoder", "filmrefit.py");
            if (File.Exists(script))
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

    private static IEnumerable<string> BundledPythonCandidates(string repositoryRoot)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe");
            yield break;
        }

        yield return Path.Combine(repositoryRoot, ".venv", "bin", "python3");
        yield return Path.Combine(repositoryRoot, ".venv", "bin", "python");
    }
}
