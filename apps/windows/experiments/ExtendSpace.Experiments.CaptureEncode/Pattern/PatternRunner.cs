using System.Diagnostics;
using System.Globalization;
using ExtendSpace.Experiments.CaptureEncode.Graphics;
using Windows.Win32.Graphics.Dxgi;

namespace ExtendSpace.Experiments.CaptureEncode.Pattern;

/// <summary>
/// Shows the test pattern and reports, once a second and at the end, how often it really reached
/// the screen. The pattern is a source for the other commands to capture; this one captures nothing.
/// </summary>
internal sealed class PatternRunner(PatternOptions options, int? monitorIndex, TimeSpan? duration, TextWriter log)
{
    /// <returns>True if the pattern ran and was visible throughout.</returns>
    public bool Run(CancellationToken cancellation)
    {
        DisplayMonitor monitor = ExperimentRunner.SelectMonitor(DisplayMonitors.Enumerate(), monitorIndex);
        using var window = new PatternWindow(monitor, options);
        int? refreshRate = DisplayMonitors.RefreshRate(monitor);
        var timeline = new PresentTimeline(refreshRate is { } hertz ? (double)hertz / options.SyncInterval : null, Stopwatch.Frequency);

        log.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Test pattern on {monitor.DeviceName} (monitor {monitor.Index}, {monitor.Width} x {monitor.Height}): {window.Width} x {window.Height} at ({window.Left}, {window.Top})"
            + $"{(window.CoversMonitor ? ", covering the whole monitor" : string.Empty)}; {Describe(options.Model)}; a new picture every {Ordinal(options.SyncInterval)} refresh;"
            + $" drawn on '{window.AdapterName}'; {(options.Topmost ? "kept above other windows" : "other windows may cover it")}."));
        log.WriteLine(refreshRate is { } rate
            ? $"Windows reports this monitor at {rate} Hz, so a full second is about {rate / options.SyncInterval} pictures."
            : "Windows does not report this monitor's refresh rate, so seconds cannot be judged short or full.");
        log.WriteLine(duration is { } limit
            ? string.Create(CultureInfo.InvariantCulture, $"Runs for {limit.TotalSeconds:0.#} s. Esc, a right-click on it or Ctrl+C closes it sooner.")
            : "Runs until closed: Esc, a right-click on it or Ctrl+C.");

        DXGI_FRAME_STATISTICS? statisticsAtStart = null;
        long start = Stopwatch.GetTimestamp();
        long frame = 0;
        string stopped = "the window was closed";

        while (window.PumpMessages())
        {
            if (cancellation.IsCancellationRequested)
            {
                stopped = "cancelled (Ctrl+C or Ctrl+Break)";
                break;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            if (duration is { } runFor && elapsed >= runFor)
            {
                stopped = "the requested duration elapsed";
                break;
            }

            window.Draw(frame, (long)elapsed.TotalMilliseconds);
            if (window.Present(options.SyncInterval))
            {
                frame++;
                if (timeline.Add(Stopwatch.GetTimestamp()) is { } second)
                {
                    log.WriteLine(second.Describe());
                }

                // DXGI's counters mean nothing until a few pictures have been shown.
                if (statisticsAtStart is null && frame >= StatisticsAfterPresents)
                {
                    statisticsAtStart = window.FrameStatistics();
                }
            }
            else
            {
                // Nothing was shown and nothing was waited for; do not spin.
                timeline.AddOccluded();
                Thread.Sleep(5);
            }
        }

        DXGI_FRAME_STATISTICS? statisticsAtEnd = window.PumpMessages() ? window.FrameStatistics() : null;
        WriteSummary(timeline, stopped, statisticsAtStart, statisticsAtEnd);
        return timeline.Presented > 0 && timeline.Occluded == 0;
    }

    private const int StatisticsAfterPresents = 60;

    private static string Describe(PresentModel model) => model == PresentModel.Flip
        ? "flip-model swap chain (Windows may put it on screen without composing it)"
        : "blt-model swap chain (always copied through the compositor)";

    private static string Ordinal(int interval) => interval switch
    {
        1 => "display",
        2 => "2nd",
        3 => "3rd",
        _ => $"{interval}th",
    };

    private void WriteSummary(PresentTimeline timeline, string stopped, DXGI_FRAME_STATISTICS? atStart, DXGI_FRAME_STATISTICS? atEnd)
    {
        double seconds = timeline.Elapsed.TotalSeconds;
        log.WriteLine();
        log.WriteLine("==== ExtendSpace test pattern: what was presented ====");
        log.WriteLine($"Stopped because: {stopped}");
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Pictures presented: {timeline.Presented} in {seconds:0.000} s = {(seconds > 0 ? (timeline.Presented - 1) / seconds : 0):0.00} per second [measured]"));
        log.WriteLine(timeline.SecondsShort == 0
            ? "Short seconds:      none after the first: every second had at least as many new pictures as the display has refreshes [measured]"
            : $"Short seconds:      {timeline.SecondsShort} second(s) after the first had fewer new pictures than the display has refreshes, {timeline.Shortfall} picture(s) in all [measured]");
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Longest interval:   {timeline.LongestIntervalTicks * 1000.0 / Stopwatch.Frequency:0.00} ms between two presents returning"));
        log.WriteLine(timeline.Occluded == 0
            ? "Visibility:         Windows showed every present"
            : $"Visibility:         {timeline.Occluded} presents were NOT shown: the window was covered or minimised. This run is not a valid source.");

        if (atStart is { } first && atEnd is { } last && last.PresentCount > first.PresentCount)
        {
            log.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"DXGI's own count:   {last.PresentCount - first.PresentCount} presents over {last.PresentRefreshCount - first.PresentRefreshCount} display refreshes [reported by DXGI]"));
        }
        else
        {
            log.WriteLine("DXGI's own count:   not available for this kind of swap chain");
        }
    }
}
