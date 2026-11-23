namespace ExtendSpace.Media.Encoding;

/// <summary>Which encoders are tried. The experiment's <c>--encoder</c> option.</summary>
internal enum EncoderPreference
{
    /// <summary>Hardware encoders first, then the software encoder.</summary>
    Auto,
    Hardware,
    Software,
}

/// <summary>The H.264 profile asked of the encoder. Production asks for High (decision D46).</summary>
internal enum H264Profile
{
    Baseline,
    Main,
    High,
}
