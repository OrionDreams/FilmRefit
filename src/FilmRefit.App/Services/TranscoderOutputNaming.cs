namespace FilmRefit.App.Services;

public static class TranscoderOutputNaming
{
    public const string ProxySuffix = "_PROXY";
    public const string MezzanineSuffix = "_MEZZANINE";
    public const string DjiProxyExtension = ".LRF";

    public static TranscoderFilenameInfo Classify(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(Path.GetExtension(path), DjiProxyExtension, StringComparison.OrdinalIgnoreCase))
        {
            return new TranscoderFilenameInfo(TranscoderOutputKind.DjiProxy, stem);
        }

        if (stem.EndsWith(ProxySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return new TranscoderFilenameInfo(
                TranscoderOutputKind.Proxy,
                stem[..^ProxySuffix.Length]);
        }

        if (stem.EndsWith(MezzanineSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return new TranscoderFilenameInfo(
                TranscoderOutputKind.Mezzanine,
                stem[..^MezzanineSuffix.Length]);
        }

        return new TranscoderFilenameInfo(TranscoderOutputKind.Original, stem);
    }

    public static string BuildOutputPath(string inputPath, TranscodeMode mode)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var fileName = mode == TranscodeMode.Proxy
            ? $"{stem}{ProxySuffix}.mov"
            : $"{stem}{MezzanineSuffix}.mov";

        return Path.Combine(directory, fileName);
    }

    public static string BuildDjiProxyPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{stem}{DjiProxyExtension}");
    }
}

public readonly record struct TranscoderFilenameInfo(TranscoderOutputKind Kind, string SourceStem);

public enum TranscoderOutputKind
{
    Original,
    DjiProxy,
    Proxy,
    Mezzanine
}
