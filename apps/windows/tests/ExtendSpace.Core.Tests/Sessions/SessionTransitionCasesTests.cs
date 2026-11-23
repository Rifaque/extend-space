using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Tests.Sessions;

/// <summary>
/// Runs the transition cases in <c>tests/session-transitions/cases.tsv</c>, at the repository root,
/// against the Windows state machine. The Android suite runs the same file against its own, so the
/// two cannot come to disagree about a transition without one of them failing.
/// </summary>
public class SessionTransitionCasesTests
{
    private static readonly SessionRequest UsbMirror = new(TransportKind.Usb, DisplayMode.Mirror);

    private static readonly SessionFault Retryable = new(FaultCategory.Connection, "no_app_listening", "Nothing is listening on the device.", FaultTreatment.Retryable);

    private static readonly SessionFault Terminal = new(FaultCategory.Protocol, "protocol_version_unsupported", "The device speaks another protocol version.", FaultTreatment.Terminal);

    public static TheoryData<string> CaseIds => [.. TransitionCases.All.Select(transitionCase => transitionCase.Id)];

    [Fact]
    public void The_shared_table_was_found_and_covers_what_it_must()
    {
        IReadOnlyList<TransitionCase> cases = TransitionCases.All;

        Assert.True(cases.Count >= 50, $"only {cases.Count} cases were read from {TransitionCases.FilePath}");
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());

        // The table has to exercise every event and end in every phase, or it is not a table of the machine.
        Assert.Equal(
            ["acknowledge", "attempt_failed", "attempt_failed_terminal", "connect", "connection_lost", "connection_lost_terminal", "disconnect", "failed", "negotiation_completed", "teardown_completed", "transport_connected"],
            cases.Select(c => c.When).Distinct().Order());
        Assert.Equal(Enum.GetNames<SessionPhase>().Order(), cases.Select(c => c.Phase).Distinct().Order());
        Assert.Contains(cases, c => c.Accepted && c.When == "attempt_failed" && c.Phase == "Connecting");
        Assert.Contains(cases, c => c.Accepted && c.When == "attempt_failed" && c.Phase == "Reconnecting");
        Assert.Contains(cases, c => !c.Accepted && c.When == "attempt_failed");
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Case(string id)
    {
        TransitionCase transitionCase = TransitionCases.All.Single(candidate => candidate.Id == id);
        var machine = new SessionStateMachine();
        foreach (string given in transitionCase.Given)
        {
            Assert.True(machine.Dispatch(Event(given)), $"{id}: the setup event '{given}' was rejected in {machine.State.Phase}");
        }

        SessionState before = machine.State;
        int notifications = 0;
        machine.StateChanged += (_, _) => notifications++;

        bool accepted = machine.Dispatch(Event(transitionCase.When));

        SessionState after = machine.State;
        Assert.True(accepted == transitionCase.Accepted, $"{id} ({transitionCase.Note}): '{transitionCase.When}' in {before.Phase} was {(accepted ? "accepted" : "rejected")}");
        Assert.Equal(transitionCase.Phase, after.Phase.ToString());

        if (transitionCase.Accepted)
        {
            Assert.Equal(1, notifications);
        }
        else
        {
            // A rejected event must leave no trace at all.
            Assert.Equal(before, after);
            Assert.Equal(0, notifications);
        }

        if (transitionCase.HasStreamed is { } hasStreamed)
        {
            Assert.True(after.HasStreamed == hasStreamed, $"{id}: HasStreamed is {after.HasStreamed}");
        }
        else
        {
            // Outside the in-progress phases nothing of the old session's progress is carried.
            Assert.False(after.HasStreamed, $"{id}: HasStreamed survived into {after.Phase}");
        }

        if (transitionCase.AttemptFaultKept is { } kept)
        {
            Assert.Equal(kept ? Retryable : null, after.LastAttemptFault);
        }
        else
        {
            Assert.Null(after.LastAttemptFault);
        }

        // Invariants of every state, whatever the case.
        Assert.Equal(after.Phase == SessionPhase.Failed, after.Error is not null);
        Assert.Equal(after.Phase != SessionPhase.Disconnected, after.Request is not null);
    }

    private static SessionEvent Event(string name) => name switch
    {
        "connect" => new SessionEvent.ConnectRequested(UsbMirror),
        "transport_connected" => new SessionEvent.TransportConnected(),
        "negotiation_completed" => new SessionEvent.NegotiationCompleted(),
        "connection_lost" => new SessionEvent.ConnectionLost(Retryable),
        "connection_lost_terminal" => new SessionEvent.ConnectionLost(Terminal),
        "attempt_failed" => new SessionEvent.AttemptFailed(Retryable),
        "attempt_failed_terminal" => new SessionEvent.AttemptFailed(Terminal),
        "disconnect" => new SessionEvent.DisconnectRequested(),
        "teardown_completed" => new SessionEvent.TeardownCompleted(),
        "failed" => new SessionEvent.Failed(new SessionError(SessionErrorCode.ConnectionLost, Cause: Retryable)),
        "acknowledge" => new SessionEvent.ErrorAcknowledged(),
        _ => throw new InvalidOperationException($"The shared table names an event this suite does not know: '{name}'."),
    };
}

/// <param name="HasStreamed">Null where the table says the phase has no such notion.</param>
/// <param name="AttemptFaultKept">Null likewise.</param>
internal sealed record TransitionCase(string Id, string[] Given, string When, bool Accepted, string Phase, bool? HasStreamed, bool? AttemptFaultKept, string Note);

internal static class TransitionCases
{
    private const string RelativePath = "tests/session-transitions/cases.tsv";

    private static readonly Lazy<IReadOnlyList<TransitionCase>> Cases = new(Load);

    public static IReadOnlyList<TransitionCase> All => Cases.Value;

    public static string FilePath { get; } = Find();

    private static string Find()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, RelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"'{RelativePath}' was not found in any directory above {AppContext.BaseDirectory}.");
    }

    private static IReadOnlyList<TransitionCase> Load()
    {
        var cases = new List<TransitionCase>();
        string[] lines = [.. File.ReadAllLines(FilePath).Where(line => line.Length > 0 && !line.StartsWith('#'))];
        Assert.Equal("id\tgiven\twhen\taccepted\tphase\thas_streamed\tattempt_fault\tnote", lines[0]);

        foreach (string line in lines.Skip(1))
        {
            string[] fields = line.Split('\t');
            Assert.True(fields.Length == 8, $"A line of the shared table does not have 8 fields: {line}");
            cases.Add(new TransitionCase(
                fields[0],
                fields[1] == "-" ? [] : fields[1].Split(','),
                fields[2],
                YesNo(fields[3]) ?? throw new InvalidOperationException($"{fields[0]}: 'accepted' must be yes or no."),
                fields[4],
                YesNo(fields[5]),
                fields[6] switch
                {
                    "kept" => true,
                    "none" => false,
                    "-" => null,
                    _ => throw new InvalidOperationException($"{fields[0]}: 'attempt_fault' must be kept, none or -."),
                },
                fields[7]));
        }

        return cases;
    }

    private static bool? YesNo(string text) => text switch
    {
        "yes" => true,
        "no" => false,
        "-" => null,
        _ => throw new InvalidOperationException($"Expected yes, no or -, read '{text}'."),
    };
}
