# Network

`Apogee.Network` connects the game to other programs over UDP: **GameNetworkingSockets** (GNS, Valve's open-source transport, BSD-3-Clause) behind one `NetSystem`, in scripts the global `net`. Connections are encrypted,
messages are reliable (arrive once, in order) or not (may be lost, never twice), large messages are split and put back together, and lanes keep a slow message on one from holding up the others.
There is no replication: what is sent, and what it means, is the game's.

> The base game's only: `NetSystem` is `ModsBlocked`, as are connections. The library is a prebuilt, self-contained shared library beside the program (`ThirdParty/gns/lib`, see `ThirdParty/README.md`); without it `net.is_active` is false and
> every call does nothing and returns nothing, so a game that can also play alone works the same.

```lua
-- the server
local port = net:listen(27020)            -- 0: a free port; returns the port, or 0 if it is in use
net.connection_requested:connect(function(connection, address)
    if banned[address] then connection:refuse() end    -- accepted when the handlers return, unless one refused
end)
net.connected:connect(function(connection)
    connection:send('{"hello":"player"}')
end)
net.message:connect(function(connection, text, lane)
    local command = json.decode(text)
    ...
end)
net.disconnected:connect(function(connection, reason, code) ... end)

-- a client
local connection = net:connect("192.168.1.20:27020")    -- "[::1]:27020" for IPv6; a name is looked up first
net.connected:connect(function(c) c:send("hello") end)
connection:send(positionText, false)                  -- unreliable: for what is out of date a moment later
```

## The system

`ScriptSession` makes the `NetSystem` (the global `net`) and calls `Update()` once a frame, **after Steam's and before the input and the world**, so what arrived is a signal before the systems and scripts run.
Nothing of the game runs on GNS's threads: the library's callbacks are queued and become signals inside `Update`, all on the calling thread, in this order: connections requested and made, then messages, then connections
ended (a message sent just before a close is delivered before the close is announced). Opening a socket costs nothing until `listen` or `connect` is called. The system is disposed with the session.

- `listen(port)` listens on a UDP port for IPv4 and IPv6 and returns the port (0 if it is in use). With 0 a free port from 49152 up is chosen. One port at a time; `stop_listening()`; `listening_port`.
- `connect(address)` returns a `NetConnection` that is `connecting`, or nil when the address cannot be used. The outcome is a `connected` or a `disconnected` signal. A name in the address is looked up with the system's resolver,
  which can block for a moment; an IP address does not.
- `NetSystem.create()` makes a system of its own (the tests talk to themselves over the loopback interface), `NetSystem.create_inactive()` one that does nothing, `NetSystem.is_installed()` says whether the library loads.

## Connections

`NetConnection`: `address`, `is_incoming`, `state` (`connecting`, `connected`, `closed`), `ping` (ms, -1 until known), `quality` (0 to 1), `bytes_out` and `bytes_in` (a second), `send(text, reliable = true, lane = 0)` (false when it
could not be queued), `configure_lanes(count)`, `close(reason, linger = true)` and `refuse()`.

- **Messages are text** in scripts: UTF-8, such as JSON. C# also has `SendBytes(span, reliable, lane)` and the event `BytesReceived`, which carry any bytes.
- **Lanes.** `configure_lanes(3)` before the first message gives the connection three lanes of equal priority; each is in order on its own. Both sides configure the same number. The lane a message arrived on is the last argument of
  `message`.
- **Closing.** `close` ends the connection and, with `linger`, sends what is still waiting first. The other side gets `disconnected` with the text and the code (1000 and up are the application's: a refused request is 1001).
  Here no signal comes (you asked for it). A connection that ended is `closed` and destroyed a frame later.
- A connection that is not answered gives up after GNS's connect timeout (10 seconds) with a `disconnected`.

## With Steamworks

GNS's interface is Steam's: `ISteamNetworkingSockets` in `libsteam_api` is the same class with the same functions, and the Steam Datagram Relay (connections by Steam id, through Valve's network, with no open port) is the
same API with another way to address the peer. A Steam transport would therefore replace `Native/net/apogee_net.cpp`'s calls (`CreateListenSocketP2P`, `ConnectP2P`, the identity instead of the IP address) and leave `Apogee.Network`,
the scripts and these signals as they are. It is not built: the Steam wrappers of `steam.md` leave networking out on purpose (`steam.json`).

## How it is built

GNS is C++ with a flat C API that takes C++ references and structs packed differently on each platform, so it is not bound as it is. `Native/net/apogee_net.h` is our own small C API over it (a host, connections as integers,
`an_poll` returning events), compiled against GNS's headers (`ThirdParty/gns/include`) and linked to its shared library. `Apogee.Native.Net` is the generated binding of that header. Every GNS call is in
`apogee_net.cpp`. `./build.sh native` builds the shim when `libGameNetworkingSockets` is in `Bin/native/<rid>` or `ThirdParty/gns/lib/<rid>`, and skips it with a message otherwise.

## Not done

Peer-to-peer connections and signaling, Steam's relay (above), the datagram-only `ISteamNetworkingMessages`, NAT punch-through, a server browser or lobby, and replication (entities, snapshots, prediction): the game builds those on
messages. Messages in scripts are text only.

## Tests

`Tests/Apogee.Network.Tests` runs two network systems in one process over the loopback interface: a client connects and messages go both ways (reliable and unreliable, with non-ASCII text), 300 reliable messages arrive in order,
bytes are not changed, a 200 kB message is split and put back together, a close is announced after the messages that came before it, a handler can refuse a connection (the other side sees code 1001), a connection to a port nobody listens
on stays pending without blocking, lanes carry their number, addresses are parsed and names resolved, and an inactive system does nothing. `Tests/Luau/net.test.luau` runs the same through the script surface. They skip
where the library is not installed; `Tests/Apogee.Native.Tests` checks the shim's entry points and struct sizes.
