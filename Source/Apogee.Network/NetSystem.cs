using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Net;
using Apogee.Reflection;
using NetApi = Apogee.Native.Net.Net;

namespace Apogee.Network;

/// <summary>How a <see cref="NetSystem"/> is made.</summary>
public sealed class NetOptions
{
    /// <summary>False makes an inactive system, whatever is installed.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// The game's network: connections to other programs over UDP, with encryption, in order and reliably or not as each message asks (GameNetworkingSockets, Valve's open-source transport). One side
/// <see cref="Listen"/>s, the others <see cref="Connect"/>; messages are sent on a <see cref="NetConnection"/>. There is no replication here: what is sent, and what it means, is the game's.
/// <b>Without the library, <see cref="IsActive"/> is false and every call does nothing</b>, so a game that can also play alone works the same.
/// </summary>
/// <remarks>
/// Call <see cref="Update"/> once a frame: it runs the library's callbacks and turns what happened into signals, all on the calling thread (nothing of the game runs on the library's threads). Mods cannot use
/// this object: it is the game's. Messages reach scripts as text (UTF-8, such as JSON); C# can also send and receive bytes.
/// </remarks>
[ApogeeClass(ModsBlocked = true)]
public sealed unsafe partial class NetSystem : EngineObject, IDisposable
{
    private static int s_warned;

    private readonly Dictionary<uint, NetConnection> _connections = [];
    private readonly List<NetConnection> _closed = [];
    private readonly AnEvent[] _events = new AnEvent[256];
    private AnHost* _host;
    private uint _listenSocket;
    private bool _initialized;

    /// <summary>Starts the library. If it cannot be started the system is inactive, <see cref="Status"/> says why, and the reason is logged once.</summary>
    public NetSystem(NetOptions? options = null)
    {
        options ??= new NetOptions();
        if (!options.Enabled)
        {
            Status = "Networking is off for this run.";
            return;
        }

        if (!IsInstalled())
        {
            Fail("The network library (GameNetworkingSockets and apogee_net) was not found next to the program.");
            return;
        }

        byte[] error = new byte[256];
        fixed (byte* message = error)
        {
            if (!NetApi.Init(message, error.Length))
            {
                Fail("GameNetworkingSockets could not start: " + Utf8.FromPointer(message));
                return;
            }
        }

        _initialized = true;
        _host = NetApi.HostCreate();
        if (_host == null)
        {
            Fail("GameNetworkingSockets could not make a host.");
            return;
        }

        Status = "Ready.";
    }

    /// <summary>Makes a network system of its own from a script: for tests, which talk to themselves over the loopback interface. A game's own is the global <c>net</c>.</summary>
    [Method]
    public static NetSystem Create() => new();

    /// <summary>Makes an inactive network system from a script: for tests of what scripts see when there is no library.</summary>
    [Method]
    public static NetSystem CreateInactive() => new(new NetOptions { Enabled = false });

    /// <summary>Whether the library can be loaded: false on a machine without GameNetworkingSockets next to the program.</summary>
    [Method]
    public static bool IsInstalled() => NativeLibraries.TryLoad(typeof(NetApi).Assembly, "apogee_net", out _);

    /// <summary>True when the library started: connections can be made. False means every call does nothing.</summary>
    [Property]
    public bool IsActive => _host != null;

    /// <summary>Why the system is not active, or "Ready.".</summary>
    [Property]
    public string Status { get; private set; } = "";

    /// <summary>The port being listened on (the one asked for, or the free one the library chose), or 0.</summary>
    [Property]
    public int ListeningPort { get; private set; }

    private void Fail(string reason)
    {
        Status = reason;
        if (System.Threading.Interlocked.Exchange(ref s_warned, 1) == 0)
        {
            Log.Warn("Network", reason);
        }
    }

    // ---- Signals ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Somebody wants to connect to the port we listen on, with their address. The connection is accepted when the handlers return, unless one of them called <see cref="NetConnection.Refuse"/>. Messages
    /// cannot be sent on it yet: wait for <see cref="Connected"/>.
    /// </summary>
    [Signal]
    public Signal<NetConnection, string> ConnectionRequested { get; } = new();

    /// <summary>A connection is up: the one that <see cref="Connect"/> started was accepted, or a requested one is made. Messages can be sent now.</summary>
    [Signal]
    public Signal<NetConnection> Connected { get; } = new();

    /// <summary>A connection ended: the other side closed it, it timed out, or a connection that was being made failed. With the reason's text and code (1000 and up: the other side's own).</summary>
    [Signal]
    public Signal<NetConnection, string, int> Disconnected { get; } = new();

    /// <summary>A message arrived on a connection, as text, on a lane.</summary>
    [Signal]
    public Signal<NetConnection, string, int> Message { get; } = new();

    /// <summary>A message arrived, as bytes: for C#, which can send and receive anything. Called before <see cref="Message"/>.</summary>
    public event Action<NetConnection, byte[], int>? BytesReceived;

    // ---- Connections ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Listens on a UDP port, for IPv4 and IPv6 (0: any free port). Returns the port, or 0 if it could not be opened (it is in use, or the system is inactive). One port at a time: listening again
    /// closes the first (its connections stay).
    /// </summary>
    [Method]
    public int Listen(int port = 0)
    {
        if (!IsActive || port < 0 || port > ushort.MaxValue)
        {
            return 0;
        }

        StopListening();
        ushort bound = 0;
        uint socket = 0;
        if (port != 0)
        {
            socket = NetApi.Listen(_host, (ushort)port, &bound);
        }
        else
        {
            // The library will not choose a port, so one is picked from the dynamic range until one is free.
            for (int attempt = 0; attempt < 64 && socket == 0; attempt++)
            {
                socket = NetApi.Listen(_host, (ushort)Random.Shared.Next(49152, 65535), &bound);
            }
        }

        if (socket == 0)
        {
            return 0;
        }

        _listenSocket = socket;
        ListeningPort = bound;
        return bound;
    }

    /// <summary>Stops accepting new connections; the ones made stay.</summary>
    [Method]
    public void StopListening()
    {
        if (_listenSocket != 0)
        {
            NetApi.CloseListen(_host, _listenSocket);
            _listenSocket = 0;
            ListeningPort = 0;
        }
    }

    /// <summary>
    /// Starts connecting to <c>host:port</c> (an IP address, <c>[::1]:27020</c> for IPv6, or a name, which is looked up first and so can block for a moment). The result is a <see cref="Connected"/> or a
    /// <see cref="Disconnected"/> signal. Returns null when the address cannot be used.
    /// </summary>
    [Method]
    public NetConnection? Connect(string address)
    {
        if (!IsActive || !TryResolve(address, out string ip))
        {
            return null;
        }

        uint id;
        fixed (byte* text = Utf8.ToZeroTerminated(ip))
        {
            id = NetApi.Connect(_host, text);
        }

        return id == 0 ? null : Track(id, address, incoming: false);
    }

    /// <summary>Turns <c>name:port</c>, <c>1.2.3.4:port</c> and <c>[::1]:port</c> into the form the library parses (<c>ip:port</c>, <c>[ip]:port</c>).</summary>
    internal static bool TryResolve(string address, out string ip)
    {
        ip = "";
        string host;
        string port;
        if (address.StartsWith('['))
        {
            int end = address.IndexOf(']', StringComparison.Ordinal);
            if (end < 0 || end + 2 >= address.Length || address[end + 1] != ':')
            {
                return false;
            }

            host = address[1..end];
            port = address[(end + 2)..];
        }
        else
        {
            int colon = address.LastIndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            host = address[..colon];
            port = address[(colon + 1)..];
        }

        if (!ushort.TryParse(port, out ushort number) || number == 0)
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out IPAddress? parsed))
        {
            try
            {
                IPAddress[] found = Dns.GetHostAddresses(host);
                parsed = Array.Find(found, a => a.AddressFamily == AddressFamily.InterNetwork) ?? (found.Length > 0 ? found[0] : null);
            }
            catch (SocketException)
            {
                return false;
            }

            if (parsed is null)
            {
                return false;
            }
        }

        ip = parsed.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{parsed}]:{number}" : $"{parsed}:{number}";
        return true;
    }

    private NetConnection Track(uint id, string address, bool incoming)
    {
        var connection = new NetConnection(this, id, address, incoming);
        connection.Adopt();
        _connections[id] = connection;
        return connection;
    }

    internal AnHost* Host => _host;

    // ---- The frame --------------------------------------------------------------------------------------------------------------

    /// <summary>Once a frame: runs the library's callbacks and emits what happened as signals, in order: connections made, messages, connections ended.</summary>
    [Method]
    public void Update()
    {
        if (!IsActive)
        {
            return;
        }

        // Connections that ended last frame are not needed any more (their scripts had a frame to look at them).
        foreach (NetConnection closed in _closed)
        {
            closed.Destroy();
        }

        _closed.Clear();

        int count;
        do
        {
            fixed (AnEvent* events = _events)
            {
                count = NetApi.Poll(_host, events, _events.Length);
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    Dispatch(in _events[i]);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log.Error("Network", $"An event failed: {e}");
                }
            }
        }
        while (count == _events.Length);
    }

    private void Dispatch(in AnEvent e)
    {
        _connections.TryGetValue(e.Connection, out NetConnection? connection);
        switch ((AnEventType)e.Type)
        {
            case AnEventType.Incoming:
            {
                string address = Utf8.FromPointer(e.Address) ?? "";
                connection = Track(e.Connection, address, incoming: true);
                ConnectionRequested.Emit(connection, address);
                if (connection.Refused)
                {
                    fixed (byte* text = "refused\0"u8)
                    {
                        NetApi.Close(_host, e.Connection, NetConnection.RefusedReason, text, false);
                    }

                    Forget(connection);
                }
                else if (!NetApi.Accept(_host, e.Connection))
                {
                    Forget(connection);
                }

                break;
            }

            case AnEventType.Connected:
                if (connection is not null)
                {
                    connection.State = NetState.Connected;
                    Connected.Emit(connection);
                }

                break;

            case AnEventType.Message:
                if (connection is not null && !connection.IsDestroyed)
                {
                    byte[] bytes = new ReadOnlySpan<byte>(e.Data, e.Size).ToArray();
                    BytesReceived?.Invoke(connection, bytes, e.Lane);
                    Message.Emit(connection, Encoding.UTF8.GetString(bytes), e.Lane);
                }

                break;

            case AnEventType.Disconnected:
                if (connection is not null)
                {
                    connection.State = NetState.Closed;
                    Disconnected.Emit(connection, Utf8.FromPointer(e.Debug) ?? "", e.Reason);
                    Forget(connection);
                }

                break;
        }
    }

    private void Forget(NetConnection connection)
    {
        connection.State = NetState.Closed;
        _connections.Remove(connection.Id);
        _closed.Add(connection);
    }

    internal void Closed(NetConnection connection)
    {
        if (_connections.ContainsKey(connection.Id))
        {
            Forget(connection);
        }
    }

    // ---- Lifetime ---------------------------------------------------------------------------------------------------------------

    public void Dispose() => Destroy();

    protected override void OnDestroyed()
    {
        if (_host != null)
        {
            foreach (NetConnection connection in _connections.Values)
            {
                connection.State = NetState.Closed;
                connection.Destroy();
            }

            _connections.Clear();
            foreach (NetConnection closed in _closed)
            {
                closed.Destroy();
            }

            NetApi.HostDestroy(_host);
            _host = null;
        }

        if (_initialized)
        {
            _initialized = false;
            NetApi.Shutdown();
        }
    }
}
