// Our C API over GameNetworkingSockets: see apogee_net.h.
#include "apogee_net.h"

#include <steam/isteamnetworkingutils.h>
#include <steam/steamnetworkingsockets.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <deque>
#include <map>
#include <mutex>
#include <string>
#include <vector>

namespace
{
struct Pending
{
    int type;
    HSteamNetConnection connection;
    int reason;
    std::string address;
    std::string debug;
};

constexpr int MaxMessagesPerCall = 64;

struct Host
{
    HSteamNetPollGroup pollGroup = k_HSteamNetPollGroup_Invalid;
    std::deque<Pending> pending; // what the callback saw, in order
    std::vector<SteamNetworkingMessage_t*> held; // messages handed out by the last poll
    std::vector<std::string> strings; // the text handed out by the last poll
    std::vector<HSteamListenSocket> listens;
    std::vector<HSteamNetConnection> connections;
};

std::recursive_mutex g_lock;
int g_references = 0;
std::map<HSteamNetConnection, Host*> g_connections;
std::map<HSteamListenSocket, Host*> g_listens;

ISteamNetworkingSockets* Sockets()
{
    return SteamNetworkingSockets();
}

void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t* callback)
{
    std::lock_guard<std::recursive_mutex> lock(g_lock);
    const SteamNetConnectionInfo_t& info = callback->m_info;
    HSteamNetConnection connection = callback->m_hConn;

    Host* host = nullptr;
    if (auto it = g_connections.find(connection); it != g_connections.end())
    {
        host = it->second;
    }
    else if (info.m_hListenSocket != k_HSteamListenSocket_Invalid)
    {
        if (auto listen = g_listens.find(info.m_hListenSocket); listen != g_listens.end())
        {
            host = listen->second;
        }
    }

    if (host == nullptr)
    {
        // Nobody's (a host that was destroyed, a connection already closed): let it go.
        if (info.m_eState == k_ESteamNetworkingConnectionState_ClosedByPeer || info.m_eState == k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
        {
            Sockets()->CloseConnection(connection, 0, nullptr, false);
        }

        return;
    }

    char address[SteamNetworkingIPAddr::k_cchMaxString];
    info.m_addrRemote.ToString(address, sizeof(address), true);

    switch (info.m_eState)
    {
    case k_ESteamNetworkingConnectionState_Connecting:
        if (callback->m_eOldState == k_ESteamNetworkingConnectionState_None && info.m_hListenSocket != k_HSteamListenSocket_Invalid)
        {
            g_connections[connection] = host;
            host->connections.push_back(connection);
            host->pending.push_back({AN_EVENT_INCOMING, connection, 0, address, ""});
        }

        break;
    case k_ESteamNetworkingConnectionState_Connected:
        host->pending.push_back({AN_EVENT_CONNECTED, connection, 0, address, ""});
        break;
    case k_ESteamNetworkingConnectionState_ClosedByPeer:
    case k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
        // The connection is closed in an_poll, after the messages it had received are handed out.
        host->pending.push_back({AN_EVENT_DISCONNECTED, connection, info.m_eEndReason, address, info.m_szEndDebug});
        break;
    default:
        break;
    }
}

const char* Keep(Host* host, const std::string& text)
{
    if (text.empty())
    {
        return nullptr;
    }

    host->strings.push_back(text);
    return host->strings.back().c_str();
}

void Forget(Host* host, HSteamNetConnection connection)
{
    g_connections.erase(connection);
    host->connections.erase(std::remove(host->connections.begin(), host->connections.end(), connection), host->connections.end());
}
} // namespace

extern "C"
{
bool an_init(char* error, int32_t error_size)
{
    std::lock_guard<std::recursive_mutex> lock(g_lock);
    if (g_references == 0)
    {
        SteamDatagramErrMsg message;
        if (!GameNetworkingSockets_Init(nullptr, message))
        {
            if (error != nullptr && error_size > 0)
            {
                std::snprintf(error, static_cast<size_t>(error_size), "%s", message);
            }

            return false;
        }

        SteamNetworkingUtils()->SetGlobalCallback_SteamNetConnectionStatusChanged(OnStatusChanged);
    }

    g_references++;
    return true;
}

void an_shutdown(void)
{
    std::lock_guard<std::recursive_mutex> lock(g_lock);
    if (g_references > 0 && --g_references == 0)
    {
        GameNetworkingSockets_Kill();
        g_connections.clear();
        g_listens.clear();
    }
}

AnHost* an_host_create(void)
{
    std::lock_guard<std::recursive_mutex> lock(g_lock);
    if (g_references == 0)
    {
        return nullptr;
    }

    Host* host = new Host();
    host->pollGroup = Sockets()->CreatePollGroup();
    return reinterpret_cast<AnHost*>(host);
}

void an_host_destroy(AnHost* handle)
{
    Host* host = reinterpret_cast<Host*>(handle);
    if (host == nullptr)
    {
        return;
    }

    std::lock_guard<std::recursive_mutex> lock(g_lock);
    for (SteamNetworkingMessage_t* message : host->held)
    {
        message->Release();
    }

    for (HSteamNetConnection connection : host->connections)
    {
        Sockets()->CloseConnection(connection, 0, nullptr, false);
        g_connections.erase(connection);
    }

    for (HSteamListenSocket listen : host->listens)
    {
        Sockets()->CloseListenSocket(listen);
        g_listens.erase(listen);
    }

    Sockets()->DestroyPollGroup(host->pollGroup);
    delete host;
}

uint32_t an_listen(AnHost* handle, uint16_t port, uint16_t* out_port)
{
    Host* host = reinterpret_cast<Host*>(handle);
    std::lock_guard<std::recursive_mutex> lock(g_lock);
    SteamNetworkingIPAddr address;
    address.Clear();
    address.m_port = port;
    HSteamListenSocket listen = Sockets()->CreateListenSocketIP(address, 0, nullptr);
    if (listen == k_HSteamListenSocket_Invalid)
    {
        return 0;
    }

    g_listens[listen] = host;
    host->listens.push_back(listen);
    if (out_port != nullptr)
    {
        SteamNetworkingIPAddr bound;
        *out_port = Sockets()->GetListenSocketAddress(listen, &bound) ? bound.m_port : port;
    }

    return listen;
}

void an_close_listen(AnHost* handle, uint32_t listen_socket)
{
    Host* host = reinterpret_cast<Host*>(handle);
    std::lock_guard<std::recursive_mutex> lock(g_lock);
    Sockets()->CloseListenSocket(listen_socket);
    g_listens.erase(listen_socket);
    host->listens.erase(std::remove(host->listens.begin(), host->listens.end(), static_cast<HSteamListenSocket>(listen_socket)), host->listens.end());
}

uint32_t an_connect(AnHost* handle, const char* text)
{
    Host* host = reinterpret_cast<Host*>(handle);
    SteamNetworkingIPAddr address;
    if (text == nullptr || !address.ParseString(text))
    {
        return 0;
    }

    std::lock_guard<std::recursive_mutex> lock(g_lock);
    HSteamNetConnection connection = Sockets()->ConnectByIPAddress(address, 0, nullptr);
    if (connection == k_HSteamNetConnection_Invalid)
    {
        return 0;
    }

    g_connections[connection] = host;
    host->connections.push_back(connection);
    Sockets()->SetConnectionPollGroup(connection, host->pollGroup);
    return connection;
}

bool an_accept(AnHost* handle, uint32_t connection)
{
    Host* host = reinterpret_cast<Host*>(handle);
    std::lock_guard<std::recursive_mutex> lock(g_lock);
    if (Sockets()->AcceptConnection(connection) != k_EResultOK)
    {
        return false;
    }

    Sockets()->SetConnectionPollGroup(connection, host->pollGroup);
    return true;
}

void an_close(AnHost* handle, uint32_t connection, int32_t reason, const char* debug, bool linger)
{
    Host* host = reinterpret_cast<Host*>(handle);
    std::lock_guard<std::recursive_mutex> lock(g_lock);
    Sockets()->CloseConnection(connection, reason, debug, linger);
    Forget(host, connection);
}

bool an_configure_lanes(AnHost*, uint32_t connection, int32_t lane_count)
{
    if (lane_count < 1 || lane_count > 255)
    {
        return false;
    }

    std::vector<int> priorities(static_cast<size_t>(lane_count), 0);
    std::vector<uint16> weights(static_cast<size_t>(lane_count), 1);
    return Sockets()->ConfigureConnectionLanes(connection, lane_count, priorities.data(), weights.data()) == k_EResultOK;
}

bool an_send(AnHost*, uint32_t connection, const void* data, int32_t size, bool reliable, int32_t lane)
{
    if (size < 0 || (data == nullptr && size > 0))
    {
        return false;
    }

    int flags = (reliable ? k_nSteamNetworkingSend_Reliable : k_nSteamNetworkingSend_Unreliable) | k_nSteamNetworkingSend_NoNagle;
    if (lane == 0)
    {
        return Sockets()->SendMessageToConnection(connection, data, static_cast<uint32>(size), flags, nullptr) == k_EResultOK;
    }

    SteamNetworkingMessage_t* message = SteamNetworkingUtils()->AllocateMessage(size);
    if (message == nullptr)
    {
        return false;
    }

    if (size > 0)
    {
        std::memcpy(message->m_pData, data, static_cast<size_t>(size));
    }

    message->m_conn = connection;
    message->m_nFlags = flags;
    message->m_idxLane = static_cast<uint16>(lane);
    int64 result = 0;
    Sockets()->SendMessages(1, &message, &result, true);
    return result > 0;
}

int32_t an_poll(AnHost* handle, AnEvent* events, int32_t max)
{
    Host* host = reinterpret_cast<Host*>(handle);
    if (host == nullptr || events == nullptr || max <= 0)
    {
        return 0;
    }

    // What the last call handed out is over.
    for (SteamNetworkingMessage_t* message : host->held)
    {
        message->Release();
    }

    host->held.clear();
    host->strings.clear();
    host->strings.reserve(static_cast<size_t>(max) * 2); // the pointers into it must not move while the events are used

    Sockets()->RunCallbacks();

    std::lock_guard<std::recursive_mutex> lock(g_lock);
    int32_t count = 0;

    // Connections coming and going up first, then the messages, then the endings: a message sent just before a close is still delivered.
    std::vector<Pending> endings;
    std::deque<Pending> rest;
    while (!host->pending.empty() && count < max)
    {
        Pending item = std::move(host->pending.front());
        host->pending.pop_front();
        if (item.type == AN_EVENT_DISCONNECTED)
        {
            endings.push_back(std::move(item));
            continue;
        }

        AnEvent& event = events[count++];
        event = {};
        event.type = item.type;
        event.connection = item.connection;
        event.address = Keep(host, item.address);
    }

    while (count < max)
    {
        SteamNetworkingMessage_t* messages[MaxMessagesPerCall];
        int wanted = max - count < MaxMessagesPerCall ? max - count : MaxMessagesPerCall;
        int got = Sockets()->ReceiveMessagesOnPollGroup(host->pollGroup, messages, wanted);
        if (got <= 0)
        {
            break;
        }

        for (int i = 0; i < got; i++)
        {
            host->held.push_back(messages[i]);
            AnEvent& event = events[count++];
            event = {};
            event.type = AN_EVENT_MESSAGE;
            event.connection = messages[i]->m_conn;
            event.lane = messages[i]->m_idxLane;
            event.data = messages[i]->m_pData;
            event.size = static_cast<int32_t>(messages[i]->m_cbSize);
        }
    }

    for (Pending& item : endings)
    {
        if (count >= max)
        {
            host->pending.push_front(std::move(item)); // next call
            continue;
        }

        AnEvent& event = events[count++];
        event = {};
        event.type = AN_EVENT_DISCONNECTED;
        event.connection = item.connection;
        event.reason = item.reason;
        event.address = Keep(host, item.address);
        event.debug = Keep(host, item.debug);
        Sockets()->CloseConnection(item.connection, 0, nullptr, false);
        Forget(host, item.connection);
    }

    return count;
}

bool an_connection_info(AnHost*, uint32_t connection, AnConnectionInfo* info)
{
    if (info == nullptr)
    {
        return false;
    }

    SteamNetConnectionInfo_t details;
    if (!Sockets()->GetConnectionInfo(connection, &details))
    {
        return false;
    }

    *info = {};
    info->state = details.m_eState == k_ESteamNetworkingConnectionState_FindingRoute ? AN_STATE_FINDING_ROUTE : static_cast<int32_t>(details.m_eState);
    info->ping_ms = -1;
    SteamNetConnectionRealTimeStatus_t status;
    if (Sockets()->GetConnectionRealTimeStatus(connection, &status, 0, nullptr) == k_EResultOK)
    {
        info->ping_ms = status.m_nPing;
        info->quality = status.m_flConnectionQualityRemote;
        info->out_bytes_per_second = status.m_flOutBytesPerSec;
        info->in_bytes_per_second = status.m_flInBytesPerSec;
        info->pending_reliable_bytes = status.m_cbPendingReliable;
    }

    return true;
}

bool an_connection_address(AnHost*, uint32_t connection, char* buffer, int32_t buffer_size)
{
    SteamNetConnectionInfo_t details;
    if (buffer == nullptr || buffer_size <= 0 || !Sockets()->GetConnectionInfo(connection, &details))
    {
        return false;
    }

    char text[SteamNetworkingIPAddr::k_cchMaxString];
    details.m_addrRemote.ToString(text, sizeof(text), true);
    std::snprintf(buffer, static_cast<size_t>(buffer_size), "%s", text);
    return true;
}
}
