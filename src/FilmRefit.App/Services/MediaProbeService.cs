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
    string Resolution,
    string FrameRate,
    string Duration,
    string VideoCodec,
    string AudioCodec,
    string PixelFormat,
    string BitDepth,
    string ColorSpace,
    string Timecode,
    string Camera,
    string Lens);

public sealed class MediaProbeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "FilmRefit", "thumbnails");

    public async Task<VideoMetadata> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "ffprobe",
            [
                "-v", "error",
                "-show_streams",
                "-show_format",
                "-of", "json",
                path
            ],
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var format = document.RootElement.TryGetProperty("format", out var formatElement)
            ? formatElement
            : default;
        var video = streams.FirstOrDefault(stream => ReadString(stream, "codec_type") == "video");
        var audio = streams.FirstOrDefault(stream => ReadString(stream, "codec_type") == "audio");

        var width = ReadInt(video, "width");
        var height = ReadInt(video, "height");
        var duration = ReadString(format, "duration");
        var videoTags = ReadObject(video, "tags");
        var formatTags = ReadObject(format, "tags");

        var frameRateValue = ParseFrameRate(ReadString(video, "avg_frame_rate"), ReadString(video, "r_frame_rate"));
        var durationSeconds = ParseDurationSeconds(duration);

        return new VideoMetadata(
            width,
            height,
            frameRateValue,
            durationSeconds,
            width is not null && height is not null ? $"{width} x {height}" : "Unknown",
            FormatFrameRate(frameRateValue, ReadString(video, "avg_frame_rate"), ReadString(video, "r_frame_rate")),
            FormatDuration(duration),
            ReadString(video, "codec_name") ?? "Unknown",
            ReadString(audio, "codec_name") ?? "None",
            ReadString(video, "pix_fmt") ?? "Unknown",
            InferBitDepth(ReadString(video, "pix_fmt"), ReadInt(video, "bits_per_raw_sample")),
            ReadString(video, "color_space") ?? ReadString(video, "color_transfer") ?? "Unknown",
            ReadTag(videoTags, "timecode") ?? ReadTag(formatTags, "timecode") ?? "",
            ReadTag(formatTags, "com.apple.quicktime.make") ?? ReadTag(formatTags, "make") ?? "",
            ReadTag(formatTags, "com.apple.quicktime.lens") ?? ReadTag(formatTags, "lens") ?? "");
    }

    public async Task<Bitmap?> GenerateFrameAsync(string path, int maxWidth, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var outputPath = Path.Combine(_cacheDirectory, $"{HashPath(path)}-{maxWidth}.png");
        if (!File.Exists(outputPath))
        {
            var result = await ProcessRunner.RunAsync(
                "ffmpeg",
                [
                    "-y",
                    "-v", "error",
                    "-i", path,
                    "-frames:v", "1",
                    "-vf", $"scale={maxWidth}:-1",
                    outputPath
                ],
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0 || !File.Exists(outputPath))
            {
                return null;
            }
        }

        await using var stream = File.OpenRead(outputPath);
        return new Bitmap(stream);
    }

    private static string HashPath(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static JsonElement? ReadObject(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property
            : null;
    }

    private static string? ReadTag(JsonElement? tags, string tagName)
    {
        if (tags is null || tags.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in tags.Value.EnumerateObject())
        {
            if (string.Equals(property.Name, tagName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
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
