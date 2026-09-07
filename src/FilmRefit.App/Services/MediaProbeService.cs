using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace FilmRefit.App.Services;

public sealed record VideoMetadata(
    int? Width,
    int? Height,
    double? FrameRateValue,
    double? DurationSeconds,
    long? VideoFrameCount,
    string Resolution,
    string FrameRate,
    string Duration,
    string VideoCodec,
    string AudioCodec,
    string PixelFormat,
    string BitDepth,
    string ColorSpace,
    string Timecode,
    string TimecodeSource,
    string Camera,
    string Lens);

public sealed class MediaProbeService
{
    private readonly FilmRefitRuntime _runtime;
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "FilmRefit", "thumbnails");

    public MediaProbeService(FilmRefitRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<VideoMetadata> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            TranscoderExecutable(),
            TranscoderArguments(["--probe-json", path]),
            _runtime.RepositoryRoot,
            environmentPathPrepend: _runtime.FfmpegDirectory,
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        var width = ReadInt(root, "width");
        var height = ReadInt(root, "height");
        var duration = ReadString(root, "duration");

        var frameRateValue = ParseFrameRate(ReadString(root, "avg_fps"), ReadString(root, "fps"));
        var durationSeconds = ParseDurationSeconds(duration);
        var videoFrameCount = ReadLong(root, "video_frames");

        return new VideoMetadata(
            width,
            height,
            frameRateValue,
            durationSeconds,
            videoFrameCount,
            width is not null && height is not null ? $"{width} x {height}" : "Unknown",
            FormatFrameRate(frameRateValue, ReadString(root, "avg_fps"), ReadString(root, "fps")),
            FormatDuration(duration),
            ReadString(root, "codec") ?? "Unknown",
            ReadString(root, "audio_codec") ?? "None",
            ReadString(root, "pix_fmt") ?? "Unknown",
            InferBitDepth(ReadString(root, "pix_fmt"), ReadInt(root, "bits_per_raw_sample")),
            ReadString(root, "color_space") ?? ReadString(root, "color_transfer") ?? "Unknown",
            ReadString(root, "timecode") ?? "",
            ReadString(root, "timecode_source") ?? "",
            ReadString(root, "camera") ?? "",
            ReadString(root, "lens") ?? "");
    }

    public async Task<Bitmap?> GenerateFrameAsync(string path, int maxWidth, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var outputPath = Path.Combine(_cacheDirectory, $"{HashPath(path)}-{maxWidth}.png");
        if (!File.Exists(outputPath))
        {
            var result = await ProcessRunner.RunAsync(
                _runtime.FfmpegExecutable,
                [
                    "-y",
                    "-v", "error",
                    "-i", path,
                    "-frames:v", "1",
                    "-vf", $"scale={maxWidth}:-1",
                    outputPath
                ],
                environmentPathPrepend: _runtime.FfmpegDirectory,
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0 || !File.Exists(outputPath))
            {
                return null;
            }
        }

        await using var stream = File.OpenRead(outputPath);
        return new Bitmap(stream);
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

    private static string HashPath(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var value = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static double? ParseFrameRate(string? averageFrameRate, string? realFrameRate)
    {
        var raw = averageFrameRate is not "0/0" and not null ? averageFrameRate : realFrameRate;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string FormatFrameRate(double? frameRate, string? averageFrameRate, string? realFrameRate)
    {
        if (frameRate is not null)
        {
            return frameRate.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        var raw = averageFrameRate is not "0/0" and not null ? averageFrameRate : realFrameRate;
        return string.IsNullOrWhiteSpace(raw) ? "Unknown" : raw;
    }

    private static double? ParseDurationSeconds(string? duration)
    {
        return double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    private static string FormatDuration(string? duration)
    {
        var seconds = ParseDurationSeconds(duration);
        if (seconds is null)
        {
            return "Unknown";
        }

        var value = TimeSpan.FromSeconds(seconds.Value);
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string InferBitDepth(string? pixelFormat, int? rawBits)
    {
        if (rawBits is not null)
        {
            return $"{rawBits} bit";
        }

        if (string.IsNullOrWhiteSpace(pixelFormat))
        {
            return "Unknown";
        }

        return pixelFormat.Contains("12", StringComparison.Ordinal) ? "12 bit"
            : pixelFormat.Contains("10", StringComparison.Ordinal) ? "10 bit"
            : pixelFormat.Contains("p16", StringComparison.Ordinal) ? "16 bit"
            : "8 bit";
    }
}
