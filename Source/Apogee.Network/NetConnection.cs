using System;
using System.Text;
using Apogee.Interop;
using Apogee.Native.Net;
using Apogee.Reflection;
using NetApi = Apogee.Native.Net.Net;

namespace Apogee.Network;

/// <summary>Where a <see cref="NetConnection"/> is in its life.</summary>
public enum NetState : byte
{
    /// <summary>Being made: the other side has not answered yet.</summary>
    Connecting = 0,

    /// <summary>Up: messages can be sent.</summary>
    Connected = 1,

    /// <summary>Over, whoever ended it. It is destroyed a frame later.</summary>
    Closed = 2,
}

/// <summary>One connection to another program, made by <see cref="NetSystem.Connect"/> or accepted by a <see cref="NetSystem.Listen"/>. Messages are sent on it and arrive as signals of the <see cref="NetSystem"/>.</summary>
[ApogeeClass(ModsBlocked = true)]
public sealed unsafe partial class NetConnection : EngineObject
{
    /// <summary>The reason code of a connection that a handler of <see cref="NetSystem.ConnectionRequested"/> refused (the application range starts at 1000).</summary>
    public const int RefusedReason = 1001;

    private readonly NetSystem _system;

    internal NetConnection(NetSystem system, uint id, string address, bool incoming)
    {
        _system = system;
        Id = id;
        Address = address;
        IsIncoming = incoming;
    }

    internal uint Id { get; }

    internal bool Refused { get; private set; }

    /// <summary>The other side's address (the one that was asked for, for a connection we made).</summary>
    [Property]
    public string Address { get; }

    /// <summary>True for a connection that somebody made to our port, false for one we made.</summary>
    [Property]
    public bool IsIncoming { get; }

    [Property]
    public NetState State { get; internal set; }

    /// <summary>The round trip time in milliseconds, or -1 when it is not known yet.</summary>
    [Property]
    public int Ping => Info(out AnConnectionInfo info) ? info.PingMs : -1;

    /// <summary>0 to 1: the share of our packets that arrive, as the other side measures it.</summary>
    [Property]
    public float Quality => Info(out AnConnectionInfo info) ? info.Quality : 0;

    /// <summary>Bytes a second being sent.</summary>
    [Property]
    public float BytesOut => Info(out AnConnectionInfo info) ? info.OutBytesPerSecond : 0;

    /// <summary>Bytes a second being received.</summary>
    [Property]
    public float BytesIn => Info(out AnConnectionInfo info) ? info.InBytesPerSecond : 0;

    private bool Info(out AnConnectionInfo info)
    {
        info = default;
        AnConnectionInfo local;
        if (State == NetState.Closed || IsDestroyed || !NetApi.ConnectionInfo(_system.Host, Id, &local))
        {
            return false;
        }

        info = local;
        return true;
    }

    /// <summary>
    /// Sends text (UTF-8) to the other side. <paramref name="reliable"/> messages arrive, once and in order; the others may be lost, never twice, and are for what is out of date a moment later (positions).
    /// <paramref name="lane"/> is the lane set by <see cref="ConfigureLanes"/> (0 is the default one). Returns false when it could not be queued.
    /// </summary>
    [Method]
    public bool Send(string data, bool reliable = true, int lane = 0) => SendBytes(Encoding.UTF8.GetBytes(data), reliable, lane);

    /// <summary>Sends bytes: what <see cref="Send"/> does for text.</summary>
    public bool SendBytes(ReadOnlySpan<byte> data, bool reliable = true, int lane = 0)
    {
        if (State != NetState.Connected || IsDestroyed || _system.IsDestroyed)
        {
            return false;
        }

        fixed (byte* bytes = data)
        {
            return NetApi.Send(_system.Host, Id, bytes, data.Length, reliable, lane);
        }
    }

    /// <summary>Gives the connection <paramref name="count"/> lanes: messages on one lane arrive in order and a slow one does not hold up the others. Before the first message.</summary>
    [Method]
    public bool ConfigureLanes(int count) => !IsDestroyed && NetApi.ConfigureLanes(_system.Host, Id, count);

    /// <summary>
    /// Ends the connection. Messages still waiting are sent first when <paramref name="linger"/>. The other side gets <see cref="NetSystem.Disconnected"/> with <paramref name="reason"/>;
    /// here no signal is emitted (we asked for it).
    /// </summary>
    [Method]
    public void Close(string reason = "", bool linger = true)
    {
        if (State == NetState.Closed || IsDestroyed || _system.IsDestroyed)
        {
            return;
        }

        State = NetState.Closed;
        fixed (byte* text = Utf8.ToZeroTerminated(reason))
        {
            NetApi.Close(_system.Host, Id, 0, text, linger);
        }

        _system.Closed(this);
    }

    /// <summary>In a handler of <see cref="NetSystem.ConnectionRequested"/>: turns the connection down.</summary>
    [Method]
    public void Refuse() => Refused = true;
}
