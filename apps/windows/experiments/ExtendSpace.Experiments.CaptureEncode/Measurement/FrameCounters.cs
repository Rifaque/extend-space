namespace ExtendSpace.Experiments.CaptureEncode.Measurement;

/// <summary>
/// Where every frame the capture API delivered ended up. Each delivered frame is counted in exactly
/// one of the outcome fields, so <see cref="Unaccounted"/> is zero for a correct run.
/// </summary>
internal sealed record FrameCounters
{
    /// <summary>Frames taken from the capture frame pool.</summary>
    public long Delivered { get; init; }

    /// <summary>Discarded on arrival because they came sooner than the target frame interval.</summary>
    public long SkippedByPacer { get; init; }

    /// <summary>Accepted, then replaced by a newer frame before the encoder was ready for input.</summary>
    public long DroppedEncoderBusy { get; init; }

    /// <summary>Accepted, but no encoder input surface was free to convert them into.</summary>
    public long DroppedNoInputSurface { get; init; }

    /// <summary>Still waiting for the encoder when the run stopped.</summary>
    public long DiscardedAtStop { get; init; }

    /// <summary>Handed to the encoder.</summary>
    public long Submitted { get; init; }

    /// <summary>Encoded access units received from the encoder and written to the file.</summary>
    public long Encoded { get; init; }

    public long Dropped => DroppedEncoderBusy + DroppedNoInputSurface;

    public long Unaccounted => Delivered - SkippedByPacer - Dropped - DiscardedAtStop - Submitted;

    /// <summary>Frames the encoder accepted but never returned.</summary>
    public long LostInEncoder => Submitted - Encoded;
}
