/*
 * Our C API over GameNetworkingSockets (ThirdParty/gns), Valve's open-source reliable and unreliable UDP transport with encryption. It is written for what the engine needs: a host owns a set of
 * connections and a listen socket, connections are small integers (GNS's own handles), and everything GNS says (a connection asked for, made, closed; a message arrived) comes out of an_poll as an
 * event, so managed code never runs inside a GNS callback or on one of its threads.
 *
 * GNS's flat C API cannot be bound as it is: it takes C++ references and structs that are packed differently by platform and carry C++ member functions. The compiler lays those out here instead.
 *
 * Threads: call everything from one thread. Errors: functions that can fail return false, 0 or a negative number; none throws. Addresses are IP addresses with a port ("127.0.0.1:27020",
 * "[::1]:27020"); names are the caller's business to resolve.
 */
#ifndef APOGEE_NET_H
#define APOGEE_NET_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifndef APOGEE_NET_API
#if defined(_WIN32)
#define APOGEE_NET_API __declspec(dllexport)
#else
#define APOGEE_NET_API __attribute__((visibility("default")))
#endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AnHost AnHost;

typedef enum AnEventType
{
    /** A peer wants to connect to a listen socket: answer with an_accept or an_close. `address` is its address. */
    AN_EVENT_INCOMING = 1,
    /** A connection is made: one we asked for was accepted, or one we accepted is up. */
    AN_EVENT_CONNECTED = 2,
    /** A message arrived on `connection`, on `lane`: `size` bytes at `data`. */
    AN_EVENT_MESSAGE = 3,
    /** A connection ended (the peer closed it, or it failed): `reason` is GNS's end reason (1000 and up: the peer's own), `debug` the text. The connection is gone after this. */
    AN_EVENT_DISCONNECTED = 4,
} AnEventType;

typedef struct AnEvent
{
    int32_t type;
    uint32_t connection;
    int32_t reason;
    int32_t lane;
    /** For a message: its bytes. Valid until the next an_poll of this host. */
    const void* data;
    int32_t size;
    /** UTF-8, valid until the next an_poll of this host; null when there is none. */
    const char* address;
    const char* debug;
} AnEvent;

typedef enum AnConnectionState
{
    AN_STATE_NONE = 0,
    AN_STATE_CONNECTING = 1,
    AN_STATE_FINDING_ROUTE = 2,
    AN_STATE_CONNECTED = 3,
    AN_STATE_CLOSED_BY_PEER = 4,
    AN_STATE_PROBLEM_DETECTED_LOCALLY = 5,
} AnConnectionState;

typedef struct AnConnectionInfo
{
    int32_t state;
    /** Round trip time in milliseconds, -1 when not known yet. */
    int32_t ping_ms;
    /** 0 to 1: the share of packets that arrived, as the peer measures it. */
    float quality;
    float out_bytes_per_second;
    float in_bytes_per_second;
    /** Bytes of reliable messages waiting to be sent. */
    int32_t pending_reliable_bytes;
} AnConnectionInfo;

/** Starts the library (counted: every an_init needs an an_shutdown). On failure returns false and writes the reason to `error` (`error_size` bytes, may be null). */
APOGEE_NET_API bool an_init(char* error, int32_t error_size);

APOGEE_NET_API void an_shutdown(void);

/** Makes a host: its own listen sockets and connections. */
APOGEE_NET_API AnHost* an_host_create(void);

/** Closes everything the host has and frees it. */
APOGEE_NET_API void an_host_destroy(AnHost* host);

/** Listens on a UDP port (not 0: GNS needs a port to be named), for IPv4 and IPv6. Returns the listen socket (0 on failure, such as a port in use) and writes the port to `out_port`. */
APOGEE_NET_API uint32_t an_listen(AnHost* host, uint16_t port, uint16_t* out_port);

APOGEE_NET_API void an_close_listen(AnHost* host, uint32_t listen_socket);

/** Starts a connection to an address; the result is an AN_EVENT_CONNECTED or AN_EVENT_DISCONNECTED from an_poll. Returns the connection, 0 if the address does not parse. */
APOGEE_NET_API uint32_t an_connect(AnHost* host, const char* address);

/** Accepts an incoming connection (see AN_EVENT_INCOMING). */
APOGEE_NET_API bool an_accept(AnHost* host, uint32_t connection);

/** Closes a connection. `reason` is 1000 to 1999 for the application's own reasons (0: a plain close); with `linger`, reliable messages still waiting are sent first. */
APOGEE_NET_API void an_close(AnHost* host, uint32_t connection, int32_t reason, const char* debug, bool linger);

/** Gives a connection `lane_count` lanes of equal priority: each is delivered in order on its own, so a slow reliable message on one does not hold up the others. Before the first send. */
APOGEE_NET_API bool an_configure_lanes(AnHost* host, uint32_t connection, int32_t lane_count);

/** Sends a message (reliable: arrives, in order, once; unreliable: may be lost, never twice) on a lane. Returns true when it was queued. */
APOGEE_NET_API bool an_send(AnHost* host, uint32_t connection, const void* data, int32_t size, bool reliable, int32_t lane);

/** Runs GNS's callbacks and fills `events` with what happened since the last call (up to `max`); returns how many. Message data and strings stay valid until the next call. */
APOGEE_NET_API int32_t an_poll(AnHost* host, AnEvent* events, int32_t max);

APOGEE_NET_API bool an_connection_info(AnHost* host, uint32_t connection, AnConnectionInfo* info);

/** The peer's address as text in `buffer`; false if the connection is not known. */
APOGEE_NET_API bool an_connection_address(AnHost* host, uint32_t connection, char* buffer, int32_t buffer_size);

#ifdef __cplusplus
}
#endif

#endif
