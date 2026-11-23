using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ExtendSpace.Core.Connection;
using ExtendSpace.Core.Protocol;
using ExtendSpace.Core.Sessions;
using ExtendSpace.Core.Tests.Connection;
using ExtendSpace.Core.Tests.Protocol;
using ExtendSpace.Core.Transport;
using ExtendSpace.Core.Wireless;

namespace ExtendSpace.Core.Tests.Wireless;

/// <summary>
/// The tablet's Wi-Fi endpoint, reduced to what these tests need: a real TLS 1.3 server on loopback
/// that requires a client certificate and negotiates the two ALPN names, as the Android app does.
/// </summary>
internal sealed class FakeTablet : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public FakeTablet()
    {
        _listener.Start();
    }

    public HostIdentity Identity { get; } = HostIdentity.Create("CN=fake tablet");

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);

    /// <summary>Accepts one connection and runs the server handshake. Returns the session's client, the agreed ALPN and the PC's ID.</summary>
    public async Task<(TestClient Client, string Protocol, string PeerId)> AcceptAsync()
    {
        Socket socket = await _listener.AcceptSocketAsync().Within();
        string? peerId = null;
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            peerId = certificate is null ? null : DeviceIds.Of(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
            return certificate is not null;
        });
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(Identity.Certificate, null, offline: true),
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13,
            ApplicationProtocols = [new SslApplicationProtocol(WirelessTls.SessionProtocol), new SslApplicationProtocol(WirelessTls.PairingProtocol)],
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }).Within();
        return (new TestClient(socket, tls), tls.NegotiatedApplicationProtocol.ToString(), peerId!);
    }

    public void Dispose() => _listener.Stop();
}

public sealed class WirelessSessionTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTablet _tablet = new();
    private readonly HostIdentity _host = HostIdentity.Create();
    private readonly TrustedDeviceStore _trust;
    private readonly WirelessDirectory _directory;
    private readonly HostDeviceSelection _selection = new() { Transport = TransportKind.Wireless };

    public WirelessSessionTests()
    {
        _trust = new TrustedDeviceStore(_folder.File("trusted.json"));
        _directory = new WirelessDirectory(_trust);
    }

    public void Dispose()
    {
        _tablet.Dispose();
        _folder.Dispose();
    }

    private HostConnectionController Controller(string pairedId)
    {
        _trust.Add(new TrustedDevice(pairedId, "Fake Pad", DateTimeOffset.UnixEpoch));
        _directory.Upsert(new DiscoveredService("k", "Fake Pad", pairedId[..16], [IPAddress.Loopback], _tablet.Port, false, 0));
        _selection.WirelessDeviceId = pairedId;
        var wireless = new WirelessTransport(() => _host, _trust, _directory, () => _selection.WirelessDeviceId);
        return new HostConnectionController(new RoutedTransport(_selection, new LoopbackTransport(), wireless));
    }

    [Fact]
    public async Task A_paired_tablet_gets_the_unchanged_session_over_mutual_TLS_and_video_arrives()
    {
        HostConnectionController controller = Controller(_tablet.Identity.DeviceId);

        Task<AttemptOutcome> open = controller.OpenAsync(TestContext.Current.CancellationToken);
        (TestClient tablet, string protocol, string hostId) = await _tablet.AcceptAsync();
        using (tablet)
        {
            Assert.Equal(OutcomeKind.Succeeded, (await open.Within()).Kind);
            Assert.Equal((WirelessTls.SessionProtocol, _host.DeviceId), (protocol, hostId));

            Task<AttemptOutcome<ClientCapabilities>> exchange = controller.ExchangeCapabilitiesAsync(TestContext.Current.CancellationToken);
            Task<(HelloMessage Hello, StreamConfigMessage Config)> handshake = tablet.HandshakeAsync();
            Assert.Equal(OutcomeKind.Succeeded, (await exchange.Within()).Kind);
            Assert.Equal(OutcomeKind.Succeeded, (await controller.ConfigureStreamAsync(Loopback.Stream, TestContext.Current.CancellationToken).Within()).Kind);
            (HelloMessage hello, _) = await handshake.Within();
            Assert.Equal("host", hello.Role);

            controller.OfferVideo(H264Samples.IdrAccessUnit, 1_000);
            (FrameHeader header, uint sequence, _, _) = await tablet.NextVideoAsync().Within();
            Assert.Equal(0u, sequence);
            Assert.True(((VideoFlags)header.Flags).HasFlag(VideoFlags.Keyframe));

            Task close = controller.CloseAsync("done", null, TestContext.Current.CancellationToken);
            Assert.Contains(await tablet.ReadUntilClosedAsync().Within(), message => message is ByeMessage);
            await close.Within();
        }

        Assert.Equal(("127.0.0.1", (int?)_tablet.Port), (_trust.Find(_tablet.Identity.DeviceId)!.LastAddress, _trust.Find(_tablet.Identity.DeviceId)!.LastPort));
    }

    [Fact]
    public async Task A_tablet_with_another_key_than_the_paired_one_is_refused_terminally()
    {
        HostConnectionController controller = Controller(new string('e', 64));

        Task<AttemptOutcome> open = controller.OpenAsync(TestContext.Current.CancellationToken);
        // The tablet's handshake fails too once the PC rejects its key; only its end is awaited.
        Task accept = Task.Run(async () =>
        {
            try
            {
                (await _tablet.AcceptAsync()).Client.Dispose();
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException)
            {
            }
        }, TestContext.Current.CancellationToken);

        AttemptOutcome outcome = await open.Within();
        Assert.Equal(OutcomeKind.Failed, outcome.Kind);
        Assert.Equal((WirelessFaultCodes.AuthenticationFailed, false), (outcome.Fault!.Code, outcome.Fault.IsRetryable));
        Assert.Equal(SessionErrorCode.AuthenticationFailed, SessionErrors.From(outcome.Fault, hasStreamed: false).Code);
        await Task.WhenAny(accept, Task.Delay(Loopback.Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_tablet_that_does_not_trust_this_PC_says_not_paired_and_the_session_does_not_retry()
    {
        HostConnectionController controller = Controller(_tablet.Identity.DeviceId);
        Task<AttemptOutcome> open = controller.OpenAsync(TestContext.Current.CancellationToken);
        (TestClient tablet, _, _) = await _tablet.AcceptAsync();
        using (tablet)
        {
            Assert.Equal(OutcomeKind.Succeeded, (await open.Within()).Kind);

            Task<AttemptOutcome<ClientCapabilities>> exchange = controller.ExchangeCapabilitiesAsync(TestContext.Current.CancellationToken);
            await tablet.SendAsync([.. WireFormat.Preface, .. ControlCodec.Encode(new ErrorMessage(WireErrorCode.NotPaired, "This PC is not paired.", true))]);
            tablet.CloseAfterSending();

            AttemptOutcome<ClientCapabilities> outcome = await exchange.Within();
            Assert.Equal((WireErrorCode.NotPaired, false), (outcome.Fault!.Code, outcome.Fault.IsRetryable));
            Assert.Equal(SessionErrorCode.AuthenticationFailed, SessionErrors.From(outcome.Fault, hasStreamed: false).Code);
        }
    }
}

public sealed class WirelessPairingTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTablet _tablet = new();
    private readonly HostIdentity _host = HostIdentity.Create();
    private readonly TrustedDeviceStore _trust;

    public WirelessPairingTests()
    {
        _trust = new TrustedDeviceStore(_folder.File("trusted.json"));
    }

    public void Dispose()
    {
        _tablet.Dispose();
        _folder.Dispose();
    }

    /// <summary>The tablet's half of the ceremony, as the Android app runs it. Returns the code it showed and what it received.</summary>
    private async Task<(string Code, bool CommitmentHeld, PairResultMessage? HostResult)> TabletSideAsync(bool accept, bool checkCommitment = true)
    {
        (TestClient tablet, string protocol, string hostId) = await _tablet.AcceptAsync();
        using (tablet)
        {
            Assert.Equal(WirelessTls.PairingProtocol, protocol);
            HelloMessage hello = await tablet.ExpectAsync<HelloMessage>();
            Assert.Equal("pair", hello.Purpose);
            await tablet.SendHelloAsync("Fake Pad");
            PairCommitMessage commit = await tablet.ExpectAsync<PairCommitMessage>();
            byte[] tabletNonce = RandomNumberGenerator.GetBytes(32);
            await tablet.SendAsync(new PairNonceMessage(tabletNonce));
            PairRevealMessage reveal = await tablet.ExpectAsync<PairRevealMessage>();
            bool held = PairingCode.Commit(reveal.Nonce).AsSpan().SequenceEqual(commit.Commitment);
            string code = PairingCode.Compute(reveal.Nonce, tabletNonce, DeviceIds.ToBytes(hostId), DeviceIds.ToBytes(_tablet.Identity.DeviceId));
            await tablet.SendAsync(new PairResultMessage(accept));
            PairResultMessage? result = null;
            foreach (object message in await tablet.ReadUntilClosedAsync())
            {
                result ??= message as PairResultMessage;
            }

            return (code, held, result);
        }
    }

    private Task<PairingResult> PairAsync(Func<string, Task<bool>> confirm) =>
        new WirelessPairing(_host, _trust, new PairingOptions { DecisionTimeout = TimeSpan.FromSeconds(5) })
            .PairAsync(_tablet.EndPoint, "Advertised Pad", (code, _, _) => confirm(code), TestContext.Current.CancellationToken);

    [Fact]
    public async Task When_both_accept_both_show_the_same_code_and_the_tablet_is_trusted()
    {
        string? shownHere = null;
        Task<(string Code, bool CommitmentHeld, PairResultMessage? HostResult)> tablet = TabletSideAsync(accept: true);

        PairingResult result = await PairAsync(code => { shownHere = code; return Task.FromResult(true); }).Within();
        (string shownThere, bool held, PairResultMessage? hostResult) = await tablet.Within();

        Assert.Equal(PairingStatus.Paired, result.Status);
        Assert.Equal(shownThere, shownHere);
        Assert.True(held);
        Assert.True(hostResult!.Accepted);
        Assert.Equal(("Fake Pad", _tablet.Identity.DeviceId), (_trust.Find(_tablet.Identity.DeviceId)!.Name, result.Device!.DeviceId));
    }

    [Fact]
    public async Task When_the_tablet_declines_nothing_is_stored_and_the_question_here_is_withdrawn()
    {
        var asked = new TaskCompletionSource<bool>();
        bool withdrawn = false;
        Task tablet = TabletSideAsync(accept: false);

        PairingResult result = await new WirelessPairing(_host, _trust).PairAsync(_tablet.EndPoint, "Pad", async (_, _, cancellation) =>
        {
            await using CancellationTokenRegistration registration = cancellation.Register(() => { withdrawn = true; asked.TrySetCanceled(); });
            return await asked.Task;
        }, TestContext.Current.CancellationToken).Within();
        await tablet.Within();

        Assert.Equal(PairingStatus.DeclinedOnTablet, result.Status);
        Assert.True(withdrawn);
        Assert.Empty(_trust.List());
    }

    [Fact]
    public async Task When_this_PC_accepts_first_and_the_tablet_then_declines_nothing_is_stored()
    {
        Task tablet = Task.Run(async () =>
        {
            (TestClient client, _, string hostId) = await _tablet.AcceptAsync();
            using (client)
            {
                await client.ExpectAsync<HelloMessage>();
                await client.SendHelloAsync("Fake Pad");
                await client.ExpectAsync<PairCommitMessage>();
                await client.SendAsync(new PairNonceMessage(RandomNumberGenerator.GetBytes(32)));
                await client.ExpectAsync<PairRevealMessage>();

                // The tablet's user answers only after the PC's has said yes.
                Assert.True((await client.ExpectAsync<PairResultMessage>()).Accepted);
                await client.SendAsync(new PairResultMessage(false));
                await client.ReadUntilClosedAsync();
                Assert.True(DeviceIds.IsValid(hostId));
            }
        }, TestContext.Current.CancellationToken);

        PairingResult result = await PairAsync(_ => Task.FromResult(true)).Within();
        await tablet.Within();

        Assert.Equal(PairingStatus.DeclinedOnTablet, result.Status);
        Assert.Empty(_trust.List());
    }

    [Fact]
    public async Task When_this_PC_declines_the_tablet_is_told_and_nothing_is_stored()
    {
        Task<(string Code, bool CommitmentHeld, PairResultMessage? HostResult)> tablet = TabletSideAsync(accept: true);

        PairingResult result = await PairAsync(_ => Task.FromResult(false)).Within();

        Assert.Equal(PairingStatus.DeclinedHere, result.Status);
        Assert.False((await tablet.Within()).HostResult!.Accepted);
        Assert.Empty(_trust.List());
    }

    [Fact]
    public async Task A_tablet_that_is_not_in_pairing_mode_says_so()
    {
        Task tablet = Task.Run(async () =>
        {
            (TestClient client, _, _) = await _tablet.AcceptAsync();
            using (client)
            {
                await client.SendAsync([.. WireFormat.Preface, .. ControlCodec.Encode(new ErrorMessage(WireErrorCode.PairingNotOpen, "not open", true))]);
                client.CloseAfterSending();
            }
        }, TestContext.Current.CancellationToken);

        PairingResult result = await PairAsync(_ => Task.FromResult(true)).Within();
        await tablet.Within();

        Assert.Equal(PairingStatus.NotOpenOnTablet, result.Status);
        Assert.Empty(_trust.List());
    }

    [Fact]
    public async Task Nothing_listening_is_a_failure_not_a_hang()
    {
        _tablet.Dispose();
        PairingResult result = await PairAsync(_ => Task.FromResult(true)).Within();
        Assert.Equal(PairingStatus.Failed, result.Status);
    }
}
