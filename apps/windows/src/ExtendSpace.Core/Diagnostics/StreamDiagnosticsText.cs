using System.Globalization;
using ExtendSpace.Core.Connection;

namespace ExtendSpace.Core.Diagnostics;

/// <summary>A named value on the Diagnostics page.</summary>
public sealed record DiagnosticLine(string Name, string Value);

/// <summary>
/// Words for the stream's health and figures (D67): one sentence for the Connect screen, a table for
/// the Diagnostics page. The sentence never calls a still screen a fault.
/// </summary>
public static class StreamDiagnosticsText
{
    /// <summary>The Connect screen's line for a stream; null when there is nothing worth saying.</summary>
    public static string? Summary(StreamHealthReport health) => health.State switch
    {
        StreamHealthState.Flowing => string.Create(CultureInfo.InvariantCulture, $"Streaming at {health.Rates.Sent:0} frames per second."),
        StreamHealthState.Quiet => health.SinceLastCapturedFrame is { TotalSeconds: >= 2 } since
            ? $"No new picture from {HostPlatform.Name} for {Seconds(since)}. This is normal while nothing on the screen changes."
            : "The screen is not changing, so few new pictures are sent.",
        StreamHealthState.EncoderStalled => "The video encoder has stopped producing pictures. If this lasts, disconnect and connect again.",
        StreamHealthState.TransportStalled => "The USB connection is not keeping up and frames are being dropped. Check the cable.",
        StreamHealthState.ClientStalled => "The device is receiving video but not showing it. ExtendSpace is asking it to restart the picture.",
        StreamHealthState.NoClientReport => "The device has not reported for a few seconds.",
        _ => null,
    };

    /// <summary>Whether <see cref="Summary"/> describes a problem rather than normal operation.</summary>
    public static bool IsProblem(StreamHealthState state) =>
        state is StreamHealthState.EncoderStalled or StreamHealthState.TransportStalled or StreamHealthState.ClientStalled or StreamHealthState.NoClientReport;

    /// <summary>The Diagnostics page's stream table. Each figure is named for where it is measured.</summary>
    public static IReadOnlyList<DiagnosticLine> Lines(StreamDiagnosticsSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Health.State == StreamHealthState.NotStreaming)
        {
            return [new("Stream", snapshot is null ? "Not sampled yet." : $"Not streaming ({snapshot.Phase}).")];
        }

        StreamRates rates = snapshot.Health.Rates;
        PipelineDiagnosticCounters? pipeline = snapshot.Pipeline;
        HostConnectionCounters connection = snapshot.Connection;
        ClientReport? client = snapshot.Client;
        var lines = new List<DiagnosticLine>
        {
            new("Health", StreamDiagnostics.Describe(snapshot.Health.State)),
            new("Streaming for", snapshot.StreamingFor is { } duration ? duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "—"),
            new("Stream", pipeline?.Stream ?? "not yet checked"),
            new("Target frame rate", pipeline is { TargetFrameRate: > 0 } p ? $"{p.TargetFrameRate} fps" : "—"),
            new($"Captured ({HostPlatform.Name} → host)", Fps(rates.Captured)),
            new("Encoded", Fps(rates.Encoded)),
            new("Sent to the device", Fps(rates.Sent)),
            new("Dropped at the send queue", Fps(rates.DroppedAtQueue)),
            new("Received by the device", rates.ClientReceived is { } r ? Fps(r) : "no report yet"),
            new("Shown by the device", rates.ClientRendered is { } s ? Fps(s) : "no report yet"),
            new($"Last new picture from {HostPlatform.Name}", snapshot.Health.SinceLastCapturedFrame is { } since ? $"{Seconds(since)} ago" : "none yet"),
            new("Frames sent / dropped (queue full, awaiting keyframe)", $"{connection.Transmitted} / {connection.DroppedQueueFull}, {connection.DroppedAwaitingKeyframe}"),
            new("Keyframes sent / asked for", $"{connection.KeyframesTransmitted} / {connection.KeyframesWanted}"),
            new("Keyframes forced by the encoder", pipeline is null ? "—" : $"{pipeline.KeyframesForced} (repeated pictures {pipeline.FramesRepeated})"),
        };

        if (client is not null)
        {
            lines.Add(new("Device: missing / discarded / decoder errors", $"{client.FramesMissing} / {client.FramesDiscarded} / {client.DecoderErrors}"));
            lines.Add(new("Device: render latency", client.RenderLatencyMeanMs is { } mean
                ? string.Create(CultureInfo.InvariantCulture, $"mean {mean:0.0} ms, p95 {client.RenderLatencyP95Ms ?? 0:0.0} ms")
                : "—"));
        }

        if (snapshot.Clock is { } clock)
        {
            lines.Add(new("Round trip", string.Create(CultureInfo.InvariantCulture, $"{clock.RoundTripMicros / 1000.0:0.0} ms")));
        }

        return lines;
    }

    private static string Fps(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0.0} fps");

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(span.TotalSeconds):0} s")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(span.TotalMinutes):0} min {span.Seconds} s");
}
