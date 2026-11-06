using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Apogee.Native.Tests;
using Xunit;

namespace Apogee.Network.Tests;

/// <summary>Two network systems of one process talk over the loopback interface: the same code path as two machines, minus the wire.</summary>
public sealed class NetTests : IDisposable
{
    private readonly NetSystem _server;
    private readonly NetSystem _client;

    public NetTests()
    {
        NativeRequirements.Require(typeof(Apogee.Native.Net.Net).Assembly, "apogee_net");
        _server = new NetSystem();
        _client = new NetSystem();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    /// <summary>Updates both systems until the condition holds (or fails after five seconds).</summary>
    private void Pump(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting");
            _server.Update();
            _client.Update();
            Thread.Sleep(2);
        }
    }

    private (NetConnection Client, NetConnection Server) Connect()
    {
        NetConnection? serverSide = null;
        NetConnection? clientSide = null;
        bool clientUp = false;
        _server.Connected.Connect(c => serverSide = c);
        _client.Connected.Connect(_ => clientUp = true);
        int port = _server.Listen();
        Assert.True(port > 0);
        clientSide = _client.Connect($"127.0.0.1:{port}");
        Assert.NotNull(clientSide);
        Assert.Equal(NetState.Connecting, clientSide.State);
        Pump(() => clientUp && serverSide is not null);
        Assert.Equal(NetState.Connected, clientSide.State);
        return (clientSide, serverSide!);
    }

    [Fact]
    public void AClientConnectsAndMessagesGoBothWays()
    {
        Assert.True(_server.IsActive, _server.Status);
        var received = new List<string>();
        var replies = new List<string>();
        _server.Message.Connect((c, text, lane) =>
        {
            received.Add(text);
            c.Send("re: " + text);
        });
        _client.Message.Connect((_, text, _) => replies.Add(text));

        (NetConnection client, NetConnection server) = Connect();
        Assert.True(server.IsIncoming);
        Assert.False(client.IsIncoming);
        Assert.StartsWith("127.0.0.1:", server.Address, StringComparison.Ordinal);

        Assert.True(client.Send("hello"));
        Assert.True(client.Send("wörld ✓", reliable: false));
        Pump(() => replies.Count == 2);

        Assert.Contains("hello", received);
        Assert.Contains("wörld ✓", received); // unreliable, but not on a lossy wire
        Assert.Contains("re: hello", replies);
        Assert.InRange(client.Ping, 0, 1000);
    }

    [Fact]
    public void ReliableMessagesArriveInOrder()
    {
        var received = new List<string>();
        _server.Message.Connect((_, text, _) => received.Add(text));
        (NetConnection client, _) = Connect();

        for (int i = 0; i < 300; i++)
        {
            Assert.True(client.Send(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        Pump(() => received.Count == 300);
        Assert.Equal(Enumerable.Range(0, 300).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), received);
    }

    [Fact]
    public void BytesAreNotChanged()
    {
        byte[]? got = null;
        _server.BytesReceived += (_, bytes, _) => got = bytes;
        (NetConnection client, _) = Connect();
        byte[] payload = [0, 1, 2, 255, 254, 0, 0, 128, 7];
        Assert.True(client.SendBytes(payload));
        Pump(() => got is not null);
        Assert.Equal(payload, got);
    }

    [Fact]
    public void ALargeMessageIsSplitAndPutBackTogether()
    {
        string? got = null;
        _server.Message.Connect((_, text, _) => got = text);
        (NetConnection client, _) = Connect();
        string big = new string('x', 200_000);
        Assert.True(client.Send(big));
        Pump(() => got is not null);
        Assert.Equal(big, got);
    }

    [Fact]
    public void ClosingTellsTheOtherSideAfterTheMessagesBeforeIt()
    {
        var log = new List<string>();
        _server.Message.Connect((_, text, _) => log.Add("message " + text));
        _server.Disconnected.Connect((_, text, reason) => log.Add($"closed {text} {reason}"));
        (NetConnection client, NetConnection server) = Connect();

        client.Send("last words");
        client.Close("bye");
        Assert.Equal(NetState.Closed, client.State);
        Assert.False(client.Send("too late"));
        Pump(() => log.Count == 2);

        Assert.Equal("message last words", log[0]);
        Assert.StartsWith("closed ", log[1], StringComparison.Ordinal);
        Assert.Equal(NetState.Closed, server.State);

        // A frame later they are destroyed.
        _server.Update();
        Assert.True(server.IsDestroyed);
    }

    [Fact]
    public void AHandlerCanRefuseAConnection()
    {
        string? requestFrom = null;
        int? reason = null;
        _server.ConnectionRequested.Connect((c, address) =>
        {
            requestFrom = address;
            c.Refuse();
        });
        _client.Disconnected.Connect((_, _, code) => reason = code);
        int port = _server.Listen();
        NetConnection? client = _client.Connect($"127.0.0.1:{port}");
        Assert.NotNull(client);
        Pump(() => reason is not null);

        Assert.StartsWith("127.0.0.1:", requestFrom, StringComparison.Ordinal);
        Assert.Equal(NetConnection.RefusedReason, reason);
        Assert.Equal(NetState.Closed, client.State);
    }

    [Fact]
    public void ANoOneListeningIsATimeoutOrAReset()
    {
        int? reason = null;
        _client.Disconnected.Connect((_, _, code) => reason = code);
        int port = _server.Listen();
        _server.StopListening();
        Assert.Equal(0, _server.ListeningPort);
        NetConnection? client = _client.Connect($"127.0.0.1:{port}");
        Assert.NotNull(client);
        Assert.Null(_client.Connect("not an address"));
        Assert.Null(_client.Connect("127.0.0.1:0"));
        var clock = Stopwatch.StartNew();
        // GNS gives up after its connect timeout (10 s by default): this only checks that the attempt stays pending without blocking anything.
        for (int i = 0; i < 20; i++)
        {
            _client.Update();
            Thread.Sleep(5);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(NetState.Connecting, client.State);
        client.Close();
    }

    [Fact]
    public void LanesCarryTheirNumber()
    {
        var lanes = new List<int>();
        _server.Message.Connect((_, _, lane) => lanes.Add(lane));
        NetConnection? serverSide = null;
        _server.Connected.Connect(c => serverSide = c);
        int port = _server.Listen();
        NetConnection client = _client.Connect($"127.0.0.1:{port}")!;
        Assert.True(client.ConfigureLanes(3));
        Pump(() => client.State == NetState.Connected && serverSide is not null);
        Assert.True(serverSide!.ConfigureLanes(3));

        Assert.True(client.Send("a", lane: 2));
        Assert.True(client.Send("b", lane: 1));
        Assert.True(client.Send("c"));
        Pump(() => lanes.Count == 3);
        Assert.Equal([0, 1, 2], lanes.Order());
    }

    [Fact]
    public void AddressesAreParsedAndNamesResolved()
    {
        Assert.True(NetSystem.TryResolve("127.0.0.1:27020", out string ip));
        Assert.Equal("127.0.0.1:27020", ip);
        Assert.True(NetSystem.TryResolve("[::1]:27020", out ip));
        Assert.Equal("[::1]:27020", ip);
        Assert.True(NetSystem.TryResolve("localhost:5", out ip));
        Assert.EndsWith(":5", ip, StringComparison.Ordinal);
        Assert.False(NetSystem.TryResolve("nope", out _));
        Assert.False(NetSystem.TryResolve("1.2.3.4:abc", out _));
        Assert.False(NetSystem.TryResolve("[::1]", out _));
        Assert.False(NetSystem.TryResolve("1.2.3.4:70000", out _));
        Assert.Equal("héllo", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("héllo")));
    }

    [Fact]
    public void AnInactiveSystemDoesNothing()
    {
        using NetSystem off = NetSystem.CreateInactive();
        Assert.False(off.IsActive);
        Assert.Equal(0, off.Listen(0));
        Assert.Null(off.Connect("127.0.0.1:1"));
        off.Update();
        Assert.Contains("off", off.Status, StringComparison.OrdinalIgnoreCase);
    }
}
