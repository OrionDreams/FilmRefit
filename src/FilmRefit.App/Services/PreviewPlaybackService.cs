using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace FilmRefit.App.Services;

public sealed class PreviewPlaybackService : IDisposable
{
    private const int MaxQueuedFrames = 6;
    private const int BytesPerPixel = 4;

    private CancellationTokenSource? _processCancellation;
    private Process? _process;
    private Channel<PreviewFrame> _frames = CreateFrameChannel();

    public ChannelReader<PreviewFrame> Frames => _frames.Reader;

    public PreviewPlaybackState State { get; private set; } = PreviewPlaybackState.Empty;

    public async Task StartAsync(
        string path,
        VideoMetadata metadata,
        TimeSpan startPosition,
        int maxWidth = 1280,
        CancellationToken cancellationToken = default)
    {
        await StopAsync();

        var size = CalculatePreviewSize(metadata.Width, metadata.Height, maxWidth);
        var frameRate = metadata.FrameRateValue.GetValueOrDefault(24);
        if (frameRate <= 0)
        {
            frameRate = 24;
        }

        var frameChannel = CreateFrameChannel();
        _frames = frameChannel;
        _processCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        State = new PreviewPlaybackState(path, size.Width, size.Height, TimeSpan.FromSeconds(1 / frameRate), startPosition);

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildArguments(path, startPosition, size.Width, size.Height))
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            throw new InvalidOperationException("Could not start ffmpeg for preview playback.");
        }

        _ = Task.Run(() => ReadFramesAsync(_process, State, frameChannel, _processCancellation.Token), CancellationToken.None);
        _ = Task.Run(() => DrainErrorsAsync(_process, _processCancellation.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        var cancellation = _processCancellation;
        _processCancellation = null;
        if (cancellation is not null)
        {
            await cancellation.CancelAsync();
            cancellation.Dispose();
        }

        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        while (_frames.Reader.TryRead(out var frame))
        {
            frame.Dispose();
        }

        _frames.Writer.TryComplete();
        State = PreviewPlaybackState.Empty;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private static Channel<PreviewFrame> CreateFrameChannel()
    {
        return Channel.CreateBounded<PreviewFrame>(new BoundedChannelOptions(MaxQueuedFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    private static IEnumerable<string> BuildArguments(string path, TimeSpan startPosition, int width, int height)
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-ss";
        yield return startPosition.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        yield return "-hwaccel";
        yield return "auto";
        yield return "-i";
        yield return path;
        yield return "-an";
        yield return "-sn";
        yield return "-dn";
        yield return "-vf";
        yield return $"scale={width}:{height}";
        yield return "-pix_fmt";
        yield return "bgra";
        yield return "-f";
        yield return "rawvideo";
        yield return "pipe:1";
    }

    private static (int Width, int Height) CalculatePreviewSize(int? sourceWidth, int? sourceHeight, int maxWidth)
    {
        if (sourceWidth is null || sourceHeight is null || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (maxWidth, 720);
        }

        var width = Math.Min(maxWidth, sourceWidth.Value);
        var height = (int)Math.Round(sourceHeight.Value * (width / (double)sourceWidth.Value));
        if (height % 2 != 0)
        {
            height++;
        }

        return (Math.Max(2, width), Math.Max(2, height));
    }

    private static async Task ReadFramesAsync(
        Process process,
        PreviewPlaybackState state,
        Channel<PreviewFrame> frameChannel,
        CancellationToken cancellationToken)
    {
        var frameSize = state.Width * state.Height * BytesPerPixel;
        var frameIndex = 0L;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
                var read = await ReadExactAsync(process.StandardOutput.BaseStream, buffer.AsMemory(0, frameSize), cancellationToken);
                if (read != frameSize)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                var frame = new PreviewFrame(
                    buffer,
                    frameSize,
                    state.Width,
                    state.Height,
                    state.StartPosition + TimeSpan.FromTicks(state.FrameDuration.Ticks * frameIndex));

                await frameChannel.Writer.WriteAsync(frame, cancellationToken);
                frameIndex++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exc)
        {
            frameChannel.Writer.TryComplete(exc);
            return;
        }

        frameChannel.Writer.TryComplete();
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task DrainErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && await process.StandardError.ReadLineAsync(cancellationToken) is not null)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed record PreviewPlaybackState(
    string? Path,
    int Width,
    int Height,
    TimeSpan FrameDuration,
    TimeSpan StartPosition)
{
    public static PreviewPlaybackState Empty { get; } = new(null, 0, 0, TimeSpan.FromSeconds(1.0 / 24), TimeSpan.Zero);
}

public sealed class PreviewFrame : IDisposable
{
    private byte[]? _buffer;

    public PreviewFrame(byte[] buffer, int length, int width, int height, TimeSpan position)
    {
        _buffer = buffer;
        Length = length;
        Width = width;
        Height = height;
        Position = position;
    }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PreviewFrame));

    public int Length { get; }

    public int Width { get; }

    public int Height { get; }

    public TimeSpan Position { get; }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
