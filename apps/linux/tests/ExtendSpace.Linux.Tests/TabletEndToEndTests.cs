using System.Diagnostics;
using ExtendSpace.Core.Input;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.ViewModels;
using ExtendSpace.Linux.X11;

namespace ExtendSpace.Linux.Tests;

/// <summary>
/// The real Linux host and the real tablet over Wi-Fi (gated: <c>EXTENDSPACE_LINUX_E2E=1</c> and
/// <c>EXTENDSPACE_TABLET_ADDRESS</c>). Drives the same <see cref="ConnectViewModel"/> as the Linux UI:
/// add by address, pair, Mirror with Mouse input. A person (or the lab script) acts on the tablet; the
/// steps meet through marker files in <c>EXTENDSPACE_E2E_DIR</c>.
/// </summary>
public sealed class TabletEndToEndTests
{
    private static string Dir => Environment.GetEnvironmentVariable("EXTENDSPACE_E2E_DIR") ?? "/tmp/es-e2e";

    private static void Log(string line)
    {
        string text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        TestContext.Current.SendDiagnosticMessage(text);
        File.AppendAllText(Path.Combine(Dir, "log.txt"), text + Environment.NewLine);
    }

    private static void Mark(string name, string content = "") => File.WriteAllText(Path.Combine(Dir, name), content);

    private static async Task WaitFor(string name, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(Dir, name)))
        {
            Assert.True(watch.Elapsed < timeout, $"timed out waiting for {name}");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < timeout, $"timed out: {what}");
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task The_Linux_host_pairs_and_mirrors_to_the_tablet_over_WiFi_with_mouse_input()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("EXTENDSPACE_LINUX_E2E") == "1", "set EXTENDSPACE_LINUX_E2E=1 and EXTENDSPACE_TABLET_ADDRESS");
        string address = Environment.GetEnvironmentVariable("EXTENDSPACE_TABLET_ADDRESS") ?? throw new InvalidOperationException("EXTENDSPACE_TABLET_ADDRESS");
        Directory.CreateDirectory(Dir);
        string data = Path.Combine(Path.GetTempPath(), "es-e2e-data");
        using LinuxHost host = LinuxHost.Create("0.2.0", line => Log($"host: {line}"), folder: data);
        Log($"environment {host.Environment.Describe}; virtual size {host.VirtualSize()}");
        ConnectViewModel vm = host.CreateViewModel();
        vm.RefreshDisplays();
        vm.SelectedTransport = TransportKind.Wireless;
        Assert.True(vm.AddWirelessAddress(address), vm.ManualAddressError);
        vm.RefreshWirelessDevices();
        vm.SelectedDevice = vm.Devices.First(d => d.WirelessDevice is not null);
        Log($"device: {vm.SelectedDevice.Title} — {vm.SelectedDevice.Detail}; can pair {vm.CanPair}");

        if (vm.CanPair)
        {
            Mark("ready-to-pair");
            await WaitFor("tablet-pairing", TimeSpan.FromMinutes(3));
            var result = await vm.PairSelectedAsync((code, name, _) =>
            {
                Log($"PAIRING CODE {code} with {name}");
                Mark("code.txt", code);
                return Task.FromResult(true);
            }, TestContext.Current.CancellationToken);
            Log($"pairing: {result}; status '{vm.PairingStatus}'");
            Assert.Equal(Core.Wireless.PairingStatus.Paired, result?.Status);
            vm.RefreshWirelessDevices();
            vm.SelectedDevice = vm.Devices.First(d => d.WirelessDevice is not null);
        }

        Mark("paired");
        await WaitFor("tablet-waiting", TimeSpan.FromMinutes(3));
        vm.SelectedMode = DisplayMode.Mirror;
        vm.SelectedInputMode = InputMode.Mouse;
        Log($"connect: can {vm.CanConnect}, blocked '{vm.ConnectBlockedReason}', display {vm.SelectedDisplay?.Title}");
        Assert.True(vm.Connect());
        await Until(() => host.Session.State.Phase == SessionPhase.Streaming, TimeSpan.FromSeconds(40), "streaming");
        Log($"STREAMING: {vm.StatusTitle} — {vm.StatusDetail}");
        Mark("streaming");

        var runner = new ProcessCommandRunner();
        string lastPointer = string.Empty;
        for (int i = 0; i < 25 && !File.Exists(Path.Combine(Dir, "stop")); i++)
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            string pointer = runner.Run("xdotool", "getmouselocation").Output.Trim();
            if (pointer != lastPointer)
            {
                Log($"POINTER {pointer}");
                lastPointer = pointer;
            }

            if (host.Diagnostics.Latest is { } snapshot)
            {
                Log("DIAG " + string.Join("; ", Core.Diagnostics.StreamDiagnosticsText.Lines(snapshot).Select(l => $"{l.Name}={l.Value}")));
            }
        }

        vm.Disconnect();
        await Until(() => host.Session.State.Phase == SessionPhase.Disconnected, TimeSpan.FromSeconds(15), "disconnected");
        Log($"ended: {vm.StatusTitle}");
        Mark("done");
    }
}
