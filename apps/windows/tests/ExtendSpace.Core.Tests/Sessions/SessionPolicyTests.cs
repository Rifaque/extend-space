using ExtendSpace.Core.Sessions;

namespace ExtendSpace.Core.Tests.Sessions;

public class RetryBackoffTests
{
    [Fact]
    public void Delays_double_from_250_ms_to_a_4_s_cap()
    {
        var backoff = new RetryBackoff(TimeSpan.FromMinutes(5));

        double[] delays = [.. Enumerable.Range(0, 7).Select(_ => backoff.NextDelay(TimeSpan.Zero)!.Value.TotalMilliseconds)];

        Assert.Equal([250, 500, 1000, 2000, 4000, 4000, 4000], delays);
    }

    [Fact]
    public void The_last_delay_ends_on_the_budget_and_a_spent_budget_gives_no_delay()
    {
        var backoff = new RetryBackoff(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 5; i++)
        {
            backoff.NextDelay(TimeSpan.Zero);
        }

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay(TimeSpan.FromSeconds(29)));
        Assert.Null(backoff.NextDelay(TimeSpan.FromSeconds(30)));
        Assert.Null(backoff.NextDelay(TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void Reset_starts_again_at_250_ms()
    {
        var backoff = new RetryBackoff(TimeSpan.FromSeconds(30));
        backoff.NextDelay(TimeSpan.Zero);
        backoff.NextDelay(TimeSpan.Zero);

        backoff.Reset();

        Assert.Equal(TimeSpan.FromMilliseconds(250), backoff.NextDelay(TimeSpan.Zero));
    }
}

public class StreamConfigurationPolicyTests
{
    private static ClientCapabilities With(params (int Width, int Height)[] decoders) =>
        new("Pad", "0.1.0", 2560, 1600, 60, [.. decoders.Select((size, i) => new DecoderCapability($"decoder{i}", true, false, size.Width, size.Height))]);

    [Fact]
    public void A_decoder_that_takes_1080p_gets_1080p60_at_12_mbit()
    {
        Assert.Equal(new EncodingRequest(1920, 1080, 60, 12_000_000), StreamConfigurationPolicy.Choose(FakePipeline.Monitor, With((1280, 720), (4096, 2304))));
    }

    [Fact]
    public void Otherwise_720p30_at_6_mbit_and_otherwise_nothing()
    {
        Assert.Equal(new EncodingRequest(1280, 720, 30, 6_000_000), StreamConfigurationPolicy.Choose(FakePipeline.Monitor, With((1920, 1000))));
        Assert.Null(StreamConfigurationPolicy.Choose(FakePipeline.Monitor, With((1279, 720))));
        Assert.Null(StreamConfigurationPolicy.Choose(FakePipeline.Monitor, With()));
    }
}

/// <summary>
/// Runs <c>tests/session-faults/error-mapping.tsv</c>, at the repository root, against the Windows
/// mapping. The Android suite runs the same file against its own.
/// </summary>
public class SessionErrorMappingTableTests
{
    public static TheoryData<int> Lines => [.. Enumerable.Range(0, MappingTable.ForWindows.Count)];

    [Fact]
    public void Every_category_and_every_raised_code_of_this_platform_is_in_the_shared_table()
    {
        Assert.Equal(
            Enum.GetValues<FaultCategory>().Order(),
            MappingTable.ForWindows.Select(line => line.Category).Distinct().Order());

        // Nothing raises AuthenticationFailed until pairing exists.
        Assert.Equal(
            Enum.GetValues<SessionErrorCode>().Where(code => code != SessionErrorCode.AuthenticationFailed).Order(),
            MappingTable.ForWindows.Select(line => line.Error).Distinct().Order());
    }

    [Theory]
    [MemberData(nameof(Lines))]
    public void A_fault_maps_as_the_shared_table_says_and_is_kept_as_the_cause(int index)
    {
        MappingLine line = MappingTable.ForWindows[index];
        foreach (bool hasStreamed in line.HasStreamed is { } value ? [value] : new[] { false, true })
        {
            var fault = new SessionFault(line.Category, line.Code, "Something.", FaultTreatment.Terminal);

            SessionError error = SessionErrors.From(fault, hasStreamed);

            Assert.True(error.Code == line.Error, $"{line.Category} {line.Code} (has streamed: {hasStreamed}) maps to {error.Code}, the table says {line.Error}: {line.Note}");
            Assert.Same(fault, error.Cause);
        }
    }
}

internal sealed record MappingLine(FaultCategory Category, string Code, bool? HasStreamed, SessionErrorCode Error, string Note);

internal static class MappingTable
{
    private static readonly Lazy<IReadOnlyList<MappingLine>> Lines = new(Load);

    public static IReadOnlyList<MappingLine> ForWindows => Lines.Value;

    private static IReadOnlyList<MappingLine> Load()
    {
        string path = SharedTestData.Find("tests/session-faults/error-mapping.tsv");
        string[] lines = [.. File.ReadAllLines(path).Where(line => line.Length > 0 && !line.StartsWith('#'))];
        Assert.Equal("category	code	has_streamed	error	platforms	note", lines[0]);

        var mappings = new List<MappingLine>();
        foreach (string[] fields in lines.Skip(1).Select(line => line.Split('	')))
        {
            Assert.True(fields.Length == 6, $"A line of the shared mapping table does not have 6 fields: {string.Join(' ', fields)}");
            if (fields[4] is not ("both" or "windows"))
            {
                Assert.Equal("android", fields[4]);
                continue;
            }

            mappings.Add(new MappingLine(
                Enum.Parse<FaultCategory>(fields[0], ignoreCase: true),
                fields[1],
                fields[2] switch { "yes" => true, "no" => false, "-" => null, _ => throw new InvalidOperationException($"has_streamed must be yes, no or -: {fields[2]}") },
                Enum.Parse<SessionErrorCode>(fields[3].Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true),
                fields[5]));
        }

        return mappings;
    }
}

/// <summary>Finds a file of the shared, cross-platform test data at the repository root.</summary>
internal static class SharedTestData
{
    public static string Find(string relativePath)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"'{relativePath}' was not found in any directory above {AppContext.BaseDirectory}.");
    }
}

public class SessionErrorsTests
{
    [Fact]
    public void The_fault_message_is_shown_only_when_the_user_can_act_on_it()
    {
        var actionable = new SessionFault(FaultCategory.Transport, "adb_not_found", "Install the Android platform tools.", FaultTreatment.Terminal, UserActionable: true);
        var internalOnly = actionable with { UserActionable = false };

        Assert.Equal("Install the Android platform tools.", SessionErrors.From(actionable, false).Detail);
        Assert.Null(SessionErrors.From(internalOnly, false).Detail);
        Assert.Equal("Given.", SessionErrors.From(internalOnly, false, "Given.").Detail);
    }
}
