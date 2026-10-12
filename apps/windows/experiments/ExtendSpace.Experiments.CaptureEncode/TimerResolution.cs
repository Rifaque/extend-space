using Windows.Win32;

namespace ExtendSpace.Experiments.CaptureEncode;

/// <summary>
/// Requests a finer Windows timer for this process for as long as it is alive. The default tick is
/// 15.6 ms, and anything that sleeps or waits with a timeout, inside this process or inside a
/// component it hosts such as an encoder transform, is rounded up to it. The request is released
/// when the object is disposed, and by Windows when the process exits.
/// </summary>
internal sealed class TimerResolution : IDisposable
{
    private const uint PeriodMilliseconds = 1;

    private bool _raised;

    private TimerResolution(bool raised, string description)
    {
        _raised = raised;
        Description = description;
    }

    public string Description { get; }

    public static TimerResolution Request()
    {
        uint result = PInvoke.timeBeginPeriod(PeriodMilliseconds);
        return result == PInvoke.TIMERR_NOERROR
            ? new TimerResolution(true, $"{PeriodMilliseconds} ms requested for this process while capturing (timeBeginPeriod), accepted")
            : new TimerResolution(false, $"{PeriodMilliseconds} ms was requested but Windows refused (timeBeginPeriod returned {result}); the default applies");
    }

    public static TimerResolution LeaveDefault() =>
        new(false, "left at the Windows default (--default-timer)");

    public void Dispose()
    {
        if (_raised)
        {
            _raised = false;
            PInvoke.timeEndPeriod(PeriodMilliseconds);
        }
    }
}
