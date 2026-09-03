# Video Playback / Preview Architecture

This document describes the recommended playback implementation for the application.

The application is primarily a transcoding tool, not a general-purpose media player. The playback feature exists mainly so the user can quickly identify and inspect a clip before transcoding it.

## Goals

The preview player should support:

- Play / pause.
- Seeking and scrubbing with a normal progress bar.
- Optional single-frame step forward/backward.
- 24, 30, 60, and potentially 120 fps sources.
- Graceful behavior when the machine cannot decode/render the source in real time.
- The broadest possible codec compatibility with the application's transcoding pipeline.

Audio is not required for the first implementation.

## Recommended Architecture

Use the **same bundled FFmpeg executable that the application uses for transcoding**.

Do not introduce libmpv, VLC, FFmpeg.AutoGen, or another playback stack unless the playback requirements later grow substantially.

The primary reason is codec consistency:

> If the bundled FFmpeg can decode/transcode a file, the preview system should generally be able to decode it too.

The basic pipeline should be:

```text
Input video
    |
    v
Bundled FFmpeg process
    |
    | decode, preferably using HW acceleration when available
    | scale to preview resolution
    | convert to BGRA
    v
stdout rawvideo pipe
    |
    v
small bounded frame queue
    |
    v
Avalonia rendering surface / WriteableBitmap
```

Use `ffprobe` separately to obtain metadata such as:

- Duration.
- Width / height.
- Frame rate.
- Codec.
- Pixel format.
- Rotation / display orientation if applicable.

## Why not libmpv?

libmpv is an excellent choice for a real embedded media player. It already solves:

- A/V sync.
- Frame timing.
- Seeking.
- Buffering.
- Variable-frame-rate playback.
- Hardware decode.
- Audio output.
- Frame dropping.

However, for this application it introduces a second multimedia stack alongside the bundled FFmpeg build.

That creates a possible mismatch where:

- FFmpeg can transcode a file.
- The particular mpv/libavcodec build cannot decode or hardware-decode it in the same way.

For a preview-only feature, using the application's own FFmpeg binary provides a simpler and more predictable compatibility story.

## Why not FFmpeg.AutoGen?

FFmpeg.AutoGen exposes the FFmpeg libraries directly and gives maximum control, but it would require implementing a significant amount of media-player infrastructure:

- Demuxing.
- Decode loops.
- Frame queues.
- Timestamp handling.
- Seeking/reset behavior.
- Rendering integration.
- Potential A/V synchronization later.

That is unnecessary for the current scope.

## Why not FFMpegCore?

FFMpegCore is useful as a .NET abstraction for invoking FFmpeg operations, but it does not fundamentally solve playback. The application already needs specialized FFmpeg command construction for hardware acceleration, codecs, and platform-specific behavior.

Direct `Process` invocation is likely simpler and easier to reason about.

## Why not ffplay?

`ffplay` is useful for debugging but is not a good embedded application player.

Embedding or controlling its window would introduce platform-specific window-management problems across:

- Windows.
- X11.
- Wayland.
- macOS.

## Preview Resolution

Do **not** pipe full-resolution 4K RGB/BGRA frames unless absolutely necessary.

For example, a 3840x2160 BGRA frame is approximately 33 MB. At 60 fps this would represent roughly 2 GB/s of raw frame traffic.

Instead, decode and scale to approximately the actual preview viewport size.

For example:

```bash
ffmpeg \
  -hwaccel auto \
  -i INPUT \
  -an -sn -dn \
  -vf "scale=1280:-2" \
  -pix_fmt bgra \
  -f rawvideo \
  pipe:1
```

`bgra` is preferable to `rgb24` for an Avalonia preview because:

- It has a simple 4-byte-per-pixel layout.
- It often maps more naturally to UI bitmap formats.
- Frame-size calculations are straightforward.

The exact preview width should ideally follow the current viewport rather than always using 1280.

## Hardware Decode

The player should attempt hardware decoding where appropriate, but must be able to fall back to software decoding.

A simple first implementation can use:

```text
-hwaccel auto
```

Later, platform-specific probing can select the application's preferred hardware path explicitly.

For example, on the known Intel Linux system discussed during development, HEVC 4:2:2 10-bit decoding works through VAAPI with:

```text
/dev/dri/renderD129
```

The playback architecture should not hard-code that device globally; hardware selection should be discovered/configured by the application's existing FFmpeg capability layer.

## Play / Pause

The application should own the playback clock.

FFmpeg decodes frames into a **small bounded queue**. The renderer consumes frames according to presentation timing.

Example conceptual flow:

```text
FFmpeg ---> [frame][frame][frame][frame] ---> renderer
                bounded queue
```

Use a small queue, for example 3-10 frames.

When paused:

- Stop advancing the presentation clock.
- Stop consuming frames.
- Allow pipe backpressure / the bounded queue to stall decoding naturally.

Do not allow FFmpeg to decode the entire file into memory while playback is paused.

## Playback Timing

For normal constant-frame-rate camera footage, the player can initially use:

```text
frameDuration = 1 / fps
```

Examples:

- 23.976 fps -> approximately 41.708 ms/frame.
- 29.97 fps -> approximately 33.367 ms/frame.
- 59.94 fps -> approximately 16.683 ms/frame.

Use a monotonic high-resolution clock such as `Stopwatch`, not a simple repeated `Task.Delay(frameDuration)` loop, because delay drift will accumulate.

A better model is:

```text
presentationTime = playbackStartTimestamp + frameIndex * frameDuration
```

Then wait until the calculated presentation time for each frame.

## High Frame Rate Sources

The preview does not need to display every source frame.

For example, a 120 fps source displayed on a 60 Hz UI can safely drop frames while preserving playback time.

The key rule is:

> Ten seconds of source video should take approximately ten seconds to preview, even if not every decoded frame is rendered.

If the renderer falls behind:

- Drop stale frames.
- Prefer showing the most recent frame appropriate for the current playback clock.

Do not allow a 120 fps source to play at half speed merely because the UI only manages 60 rendered frames per second.

## Seeking

Seeking should be implemented by **restarting the FFmpeg preview process**.

Do not attempt to create a complicated command/control protocol around one long-running rawvideo process.

For a seek to 47.3 seconds:

1. Cancel/kill the current FFmpeg process.
2. Clear the frame queue.
3. Start a new FFmpeg process with `-ss` before `-i`.
4. Resume frame ingestion from the requested location.

Example:

```bash
ffmpeg \
  -ss 47.3 \
  -hwaccel auto \
  -i INPUT \
  -an -sn -dn \
  -vf "scale=1280:-2" \
  -pix_fmt bgra \
  -f rawvideo \
  pipe:1
```

Putting `-ss` before `-i` allows FFmpeg to use container/keyframe seeking rather than decoding from the beginning of the file.

For preview use, this is the right performance/accuracy tradeoff.

## Scrubbing

Do **not** restart FFmpeg for every tiny slider movement.

Debounce scrub seeks, for example by roughly 100-200 ms.

Conceptually:

```text
slider moves
    |
    v
wait briefly
    |
    v
if position is still current -> seek
```

When the user releases the slider, perform the final seek immediately.

While actively scrubbing, an optional optimization is to decode only one frame:

```bash
ffmpeg \
  -ss POSITION \
  -i INPUT \
  -frames:v 1 \
  -an -sn -dn \
  -vf "scale=1280:-2" \
  -pix_fmt bgra \
  -f rawvideo \
  pipe:1
```

This provides a responsive thumbnail-like scrub preview.

## Frame Step Forward

Frame-forward is straightforward while paused:

- Consume/display the next decoded frame from the queue.
- If necessary, request further frames from the running FFmpeg process.

For the initial CFR-oriented implementation, advancing one frame may use the nominal frame duration.

## Frame Step Backward

Backward frame stepping is inherently more difficult for inter-frame codecs such as H.264 and HEVC because the decoder generally cannot simply decode one frame backward.

Recommended approach:

1. Keep a small cache of recently displayed frames.
2. Step backward instantly while the requested frame exists in the cache.
3. If the user steps beyond the cache, seek backward to an earlier point and decode forward again.

Since backward frame stepping is non-critical, this can be implemented after the core player works.

## Variable Frame Rate

Rawvideo does not contain timestamps.

For the first implementation, normal CFR camera footage can be handled using metadata from `ffprobe` and a playback clock.

If robust VFR playback becomes important later, this architecture becomes more complicated because exact per-frame presentation timestamps must be obtained separately.

At that point, reevaluate whether a real playback library such as libmpv is justified.

## Process Lifecycle

Every preview process must support clean cancellation.

On:

- file change,
- seek,
- application shutdown,
- preview panel close,

cancel the reader, terminate FFmpeg if it is still running, dispose streams, and clear queued frames.

Never leave stale FFmpeg processes running in the background.

## stderr Handling

FFmpeg writes logs to stderr.

If `RedirectStandardError = true`, stderr must be consumed asynchronously. Otherwise FFmpeg can eventually block if the stderr pipe fills.

Either:

- redirect stderr and continuously read it, or
- do not redirect it.

For a GUI application, redirecting and consuming stderr is preferable so errors can be surfaced in logs without opening a console window.

## Evaluating the Initial C# Example

The following code is **conceptually correct as a minimal proof of concept**, but should not be used unchanged in production:

```csharp
var psi = new ProcessStartInfo
{
    FileName = "ffmpeg",
    Arguments = $"-i {filePath} -f rawvideo -pix_fmt rgb24 -",
    RedirectStandardOutput = true,
    UseShellExecute = false
};

using var process = Process.Start(psi)!;
var frameBytes = width * height * 3;
var buffer = new byte[frameBytes];

while (process.StandardOutput.BaseStream.Read(buffer, 0, frameBytes) == frameBytes)
{
    // Convert buffer to an Avalonia Bitmap and display it
}
```

It demonstrates the correct basic idea:

```text
FFmpeg subprocess -> stdout rawvideo -> fixed-size frame reads -> UI
```

However, it has several important problems.

### Problems with the minimal example

1. **Unsafe command-line construction**

   `Arguments = $"-i {filePath} ..."` breaks when the path contains spaces, quotes, or other special characters.

   Use `ProcessStartInfo.ArgumentList` instead.

2. **`Read()` is not guaranteed to fill the requested buffer**

   A stream read can return fewer bytes than requested even though EOF has not been reached.

   The code must keep reading until one complete frame has been assembled.

3. **Full-resolution `rgb24` is wasteful**

   The preview should be scaled before leaving FFmpeg, and `bgra` is usually a better format for Avalonia.

4. **No stderr handling**

   Production code should capture FFmpeg errors/logging safely.

5. **Synchronous blocking I/O**

   Frame ingestion should run asynchronously and support cancellation.

6. **No process cancellation / restart behavior**

   Seeking and changing files require stopping the current FFmpeg process cleanly.

7. **No playback timing**

   Frames should not be displayed as fast as FFmpeg can decode them. The application must schedule them according to the source timeline.

8. **No bounded queue**

   Decode and rendering should be separated by a small bounded queue so the decoder cannot consume unlimited memory.

9. **UI-thread safety**

   Bitmap updates must be coordinated with Avalonia's UI thread / rendering model.

## Recommended C# Starting Point

The following is a better example for Codex to use as the basis of the implementation.

It is still illustrative rather than a complete player, but it establishes the right process and pipe behavior.

```csharp
using System.Diagnostics;

public static Process StartPreviewFfmpeg(
    string ffmpegPath,
    string filePath,
    int previewWidth,
    double? seekSeconds = null)
{
    var psi = new ProcessStartInfo
    {
        FileName = ffmpegPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    // Fast seek before input when requested.
    if (seekSeconds is not null)
    {
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(seekSeconds.Value.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
    }

    psi.ArgumentList.Add("-hwaccel");
    psi.ArgumentList.Add("auto");

    psi.ArgumentList.Add("-i");
    psi.ArgumentList.Add(filePath);

    // Preview is currently video-only.
    psi.ArgumentList.Add("-an");
    psi.ArgumentList.Add("-sn");
    psi.ArgumentList.Add("-dn");

    psi.ArgumentList.Add("-vf");
    psi.ArgumentList.Add($"scale={previewWidth}:-2");

    psi.ArgumentList.Add("-pix_fmt");
    psi.ArgumentList.Add("bgra");

    psi.ArgumentList.Add("-f");
    psi.ArgumentList.Add("rawvideo");

    psi.ArgumentList.Add("pipe:1");

    return Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to start FFmpeg.");
}
```

### Reading complete frames

Do not assume a single `ReadAsync()` call returns a whole frame.

Use a helper such as:

```csharp
public static async Task<bool> ReadExactlyOrEofAsync(
    Stream stream,
    Memory<byte> buffer,
    CancellationToken cancellationToken)
{
    var offset = 0;

    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(
            buffer[offset..],
            cancellationToken);

        if (read == 0)
        {
            // Clean EOF only if no partial frame was read.
            return offset == 0;
        }

        offset += read;
    }

    return true;
}
```

A production implementation may want to distinguish clean EOF from a truncated partial frame more explicitly.

Example frame ingestion:

```csharp
var process = StartPreviewFfmpeg(
    ffmpegPath,
    filePath,
    previewWidth,
    seekSeconds);

// Determine the actual scaled output dimensions ahead of time.
// For BGRA there are 4 bytes per pixel.
var frameBytes = previewWidth * previewHeight * 4;
var buffer = new byte[frameBytes];

var stderrTask = Task.Run(async () =>
{
    while (await process.StandardError.ReadLineAsync() is { } line)
    {
        // Send to application logging.
        Debug.WriteLine(line);
    }
});

while (!cancellationToken.IsCancellationRequested)
{
    var gotFrame = await ReadExactlyOrEofAsync(
        process.StandardOutput.BaseStream,
        buffer,
        cancellationToken);

    if (!gotFrame)
        break;

    // IMPORTANT:
    // Do not perform slow UI rendering directly in this read loop.
    // Copy/rent a frame buffer and enqueue it into a small bounded queue.
    // The playback/render loop consumes frames according to the playback clock.
}
```

## Recommended Internal Components

Codex should preferably split the implementation into components rather than putting all logic in the view model.

Suggested structure:

```text
Playback/
  VideoProbeService.cs
  FfmpegPreviewProcess.cs
  VideoFrame.cs
  VideoFrameQueue.cs
  PlaybackClock.cs
  VideoPreviewController.cs
```

### `VideoProbeService`

Responsible for running `ffprobe` and returning:

- Duration.
- Source width / height.
- Display aspect ratio / rotation.
- Nominal frame rate.
- Codec / pixel format.

### `FfmpegPreviewProcess`

Responsible for:

- Building the FFmpeg command.
- Starting/stopping FFmpeg.
- Reading complete raw frames.
- Reading stderr.
- Cancellation.
- Restarting at a requested seek time.

### `VideoFrame`

A simple structure containing at minimum:

```text
byte[] / IMemoryOwner<byte> pixel data
sequence/frame number
approximate presentation time
width
height
stride
```

Prefer pooled buffers (`ArrayPool<byte>` or `MemoryPool<byte>`) rather than allocating a new large array for every frame.

### `VideoFrameQueue`

Use a bounded `Channel<VideoFrame>` or equivalent.

The queue should be deliberately small.

### `PlaybackClock`

Use `Stopwatch` to map wall-clock time to source playback position.

It should support:

- Start.
- Pause.
- Resume.
- Seek/reset.

### `VideoPreviewController`

Coordinates:

- Current file.
- Play/pause.
- Seeking.
- Scrub debounce.
- Frame queue.
- FFmpeg lifecycle.
- Current displayed frame.

## Avalonia Rendering

Prefer reusing a `WriteableBitmap` or another reusable rendering buffer rather than constructing a brand-new Avalonia `Bitmap` object for every decoded frame.

Repeated allocation of bitmap objects at 30/60 fps will create unnecessary GC pressure.

The implementation should:

1. Allocate/recreate the rendering target when preview dimensions change.
2. Copy the BGRA frame into the writable framebuffer.
3. Invalidate/update the UI.

The raw-frame ingestion thread must not directly manipulate Avalonia controls from a background thread.

## Memory Management

At 1280x720 BGRA:

```text
1280 * 720 * 4 = 3,686,400 bytes
```

or roughly 3.5 MiB per frame.

A five-frame queue therefore consumes roughly 18 MiB, which is reasonable.

At full 4K BGRA, each frame would be roughly 32 MiB, which is another reason to scale inside FFmpeg.

Use buffer pooling where practical.

## Initial Implementation Scope

The recommended first version should implement:

1. Probe video metadata using `ffprobe`.
2. Start bundled FFmpeg and emit scaled BGRA rawvideo.
3. Read complete frames asynchronously.
4. Render through a reusable Avalonia bitmap.
5. Play/pause with a `Stopwatch`-based playback clock.
6. Bounded buffering.
7. Frame dropping when rendering falls behind.
8. Seek by cancelling/restarting FFmpeg.
9. Debounced scrub seeking.
10. Software-decode fallback when HW decode fails.

Implement later if desired:

- Frame-forward.
- Cached frame-backward.
- Exact VFR timing.
- Audio.

## Important Design Principle

Do not turn this into a general media-player implementation unless product requirements change.

The target is:

> A reliable, low-complexity visual preview driven by the exact FFmpeg stack that the application already trusts for transcoding.

If requirements later expand to full A/V playback, subtitles, exact VFR presentation, playback-rate changes, audio-device management, etc., then reevaluate libmpv rather than continually rebuilding a complete media player around raw FFmpeg pipes.
