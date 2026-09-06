using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FilmRefit.App.Services;

public sealed class ResolvePluginService(FilmRefitRuntime runtime)
{
    public const string ScriptFileName = "FilmRefit Proxy Linker.lua";

    public string SourceScriptPath => Path.Combine(runtime.RepositoryRoot, "resolve_importer", ScriptFileName);

    public IReadOnlyList<string> CandidateScriptsDirectories => GetCandidateScriptsDirectories().ToList();

    public string ScriptsDirectory => CandidateScriptsDirectories.FirstOrDefault(Directory.Exists)
        ?? CandidateScriptsDirectories[0];

    public string InstalledScriptPath => Path.Combine(ScriptsDirectory, ScriptFileName);

    public bool IsInstalled => File.Exists(InstalledScriptPath);

    public void Install()
    {
        if (!File.Exists(SourceScriptPath))
        {
            throw new FileNotFoundException("Could not find the FilmRefit Resolve script.", SourceScriptPath);
        }

        Directory.CreateDirectory(ScriptsDirectory);
        File.Copy(SourceScriptPath, InstalledScriptPath, overwrite: true);
    }

    public void OpenScriptsDirectory()
    {
        Directory.CreateDirectory(ScriptsDirectory);
        OpenDirectory(ScriptsDirectory);
    }

    private static IEnumerable<string> GetCandidateScriptsDirectories()
    {
        return GetCandidateResolveSupportDirectories()
            .Select(path => Path.Combine(path, "Fusion", "Scripts", "Utility"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetCandidateResolveSupportDirectories()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                yield return Path.Combine(appData, "Blackmagic Design", "DaVinci Resolve", "Support");
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                yield return Path.Combine(
                    userProfile,
                    "AppData",
                    "Roaming",
                    "Blackmagic Design",
                    "DaVinci Resolve",
                    "Support");
            }

            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return Path.Combine(home, "Library", "Application Support", "Blackmagic Design", "DaVinci Resolve");
            yield break;
        }

        yield return Path.Combine(home, ".local", "share", "DaVinciResolve");
    }

    private static void OpenDirectory(string directory)
    {
        ProcessStartInfo startInfo;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo = new ProcessStartInfo("explorer.exe");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            startInfo = new ProcessStartInfo("open");
        }
        else
        {
            startInfo = new ProcessStartInfo("xdg-open");
        }

        startInfo.UseShellExecute = false;
        startInfo.ArgumentList.Add(directory);
        Process.Start(startInfo);
    }
}
