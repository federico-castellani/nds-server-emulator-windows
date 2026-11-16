// Slirp gives the console a private network and relays its traffic through ordinary host
// sockets. Two Windows/Nintendo WFC problems are fixed on top of melonDS' Net_Slirp:
//
// 1. ICMP errors. On Windows, when a UDP datagram is answered with an ICMP "port
//    unreachable", the next recvfrom() on that socket fails with WSAECONNRESET. libslirp
//    turns that error into an ICMP port-unreachable sent to the console, and the DS
//    network stack then drops the connection. That is exactly what happens during the
//    NAT punch-through that starts a WFC battle or trade: the first probes towards the
//    other player hit a port their router has not opened yet. Disabling
//    SIO_UDP_CONNRESET on every socket slirp creates restores the Linux/macOS behaviour.
//
// 2. Players on the same home network. When both consoles reach the WFC server from the
//    same public IP, the game skips NAT negotiation and tells the other console to
//    connect to its *local* address (DWC's TELL_ADDR). With melonDS every console is
//    10.64.0.16, so the other console ends up talking to itself. Here each console gets
//    an address derived from its PC's LAN address (192.168.1.23 -> 10.168.1.23), and
//    UDP sent to another console's address is relayed over the LAN to that PC, which
//    hands it to its console. To the games this looks like two DSes under one router.
#include "SlirpDriver.h"

#include <iphlpapi.h>
#include <mstcpip.h>
#include <ws2tcpip.h>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <memory>

#include <libslirp.h>

// Not every MinGW header set defines these (values from the Windows SDK's mstcpip.h).
#ifndef SIO_UDP_CONNRESET
#define SIO_UDP_CONNRESET _WSAIOW(IOC_VENDOR, 12)
#endif
#ifndef SIO_UDP_NETRESET
#define SIO_UDP_NETRESET _WSAIOW(IOC_VENDOR, 15)
#endif

using melonDS::u8;

namespace ndscore
{
namespace
{

// LAN relay datagram: magic, version, source console ip:port, destination ip:port,
// then the UDP payload. Addresses and ports are big-endian.
constexpr u8 kRelayMagic[4] = {'N', 'D', 'S', 'R'};
constexpr u8 kRelayVersion = 1;
constexpr int kRelayHeader = 17;
constexpr int kMaxPayload = 1500;

uint32_t Be32(const u8* p) { return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | p[3]; }
uint16_t Be16(const u8* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }
void Put32(u8* p, uint32_t v) { p[0] = v >> 24; p[1] = (v >> 16) & 0xFF; p[2] = (v >> 8) & 0xFF; p[3] = v & 0xFF; }
void Put16(u8* p, uint16_t v) { p[0] = v >> 8; p[1] = v & 0xFF; }

uint32_t SumWords(const u8* data, int len, uint32_t sum = 0)
{
    for (int i = 0; i + 1 < len; i += 2) sum += Be16(data + i);
    if (len & 1) sum += data[len - 1] << 8;
    return sum;
}

uint16_t Fold(uint32_t sum)
{
    while (sum >> 16) sum = (sum & 0xFFFF) + (sum >> 16);
    return static_cast<uint16_t>(~sum);
}

/// Locally administered MAC standing in for another console in ARP and Ethernet headers.
void FakeMac(uint32_t ip, u8* mac)
{
    mac[0] = 0x02; mac[1] = 0x4E; mac[2] = 0x44;
    mac[3] = (ip >> 16) & 0xFF; mac[4] = (ip >> 8) & 0xFF; mac[5] = ip & 0xFF;
}

void GuestError(const char* msg, void*)
{
    melonDS::Platform::Log(melonDS::Platform::LogLevel::Error, "slirp: %s\n", msg);
}

int64_t ClockNs(void*)
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

// libslirp only uses timers for IPv6 router advertisements, which the DS never needs.
void* TimerNew(SlirpTimerCb, void*, void*) { return nullptr; }
void TimerFree(void*, void*) {}
void TimerMod(void*, int64_t, void*) {}

void DisableUdpReset(SOCKET s)
{
    BOOL off = FALSE;
    DWORD returned = 0;
    WSAIoctl(s, SIO_UDP_CONNRESET, &off, sizeof(off), nullptr, 0, &returned, nullptr, nullptr);
    WSAIoctl(s, SIO_UDP_NETRESET, &off, sizeof(off), nullptr, 0, &returned, nullptr, nullptr);
}

void RegisterPollFd(int fd, void*)
{
    // Harmless no-ops on TCP sockets; on UDP sockets they stop ICMP errors from
    // surfacing as recvfrom() failures (see the comment at the top of the file).
    if (fd >= 0) DisableUdpReset(static_cast<SOCKET>(fd));
}

void UnregisterPollFd(int, void*) {}

slirp_ssize_t SendToGuest(const void* buf, size_t len, void* opaque)
{
    if (len > 2048) return 0;
    static_cast<SlirpDriver*>(opaque)->DeliverToGuest(static_cast<const u8*>(buf), static_cast<int>(len));
    return static_cast<slirp_ssize_t>(len);
}

void Notify(void*) {}

}

// --- Addressing ------------------------------------------------------------------------

SlirpAddressing SlirpAddressing::ForLan(uint32_t lanIp, uint16_t relayPort)
{
    SlirpAddressing a;
    if (lanIp == 0) return a;

    const uint32_t subnet = 0x0A000000 | (lanIp & 0x00FFFF00);
    const uint32_t last = lanIp & 0xFF;
    // Gateway and DNS take high host numbers the console itself is not using.
    uint32_t picks[2] = {};
    int n = 0;
    for (uint32_t candidate : {254u, 253u, 252u})
    {
        if (candidate != last && n < 2) picks[n++] = candidate;
    }

    a.guestIp = 0x0A000000 | (lanIp & 0x00FFFFFF);
    a.netmask = 0xFFFFFF00;
    a.hostIp = subnet | picks[0];
    a.dnsIp = subnet | picks[1];
    a.lanIp = lanIp;
    a.relayPort = relayPort;
    return a;
}

// The home network address: the one other players' devices can reach. Only Ethernet and
// Wi-Fi adapters with a default gateway qualify, which leaves out VPN tunnels (WireGuard,
// Wintun and most others are "virtual" adapters) and host-only switches such as Hyper-V's
// Default Switch. A full-tunnel VPN is the route to the Internet, so the best route alone
// is not enough: it only breaks ties between qualifying adapters, then the lowest metric.
uint32_t SlirpAddressing::DetectLanAddress()
{
    sockaddr_in probe {};
    probe.sin_family = AF_INET;
    probe.sin_addr.s_addr = htonl(0x08080808);
    DWORD bestIf = 0;
    GetBestInterfaceEx(reinterpret_cast<sockaddr*>(&probe), &bestIf);

    ULONG size = 16 * 1024;
    std::unique_ptr<char[]> buffer;
    ULONG status;
    do
    {
        buffer = std::make_unique<char[]>(size);
        status = GetAdaptersAddresses(AF_INET,
                                      GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER | GAA_FLAG_INCLUDE_GATEWAYS,
                                      nullptr, reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.get()), &size);
    } while (status == ERROR_BUFFER_OVERFLOW);
    if (status != NO_ERROR) return 0;

    uint32_t chosen = 0;
    bool chosenIsBest = false;
    ULONG chosenMetric = ~0UL;
    for (auto* a = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.get()); a; a = a->Next)
    {
        if (a->OperStatus != IfOperStatusUp || !a->FirstGatewayAddress) continue;
        if (a->IfType != IF_TYPE_ETHERNET_CSMACD && a->IfType != IF_TYPE_IEEE80211) continue;

        uint32_t ip = 0;
        for (auto* u = a->FirstUnicastAddress; u && !ip; u = u->Next)
        {
            if (u->Address.lpSockaddr->sa_family != AF_INET) continue;
            const uint32_t candidate = ntohl(reinterpret_cast<sockaddr_in*>(u->Address.lpSockaddr)->sin_addr.s_addr);
            if ((candidate >> 16) != 0xA9FE) ip = candidate;   // skip 169.254.x.x link-local
        }
        if (!ip) continue;

        const bool isBest = a->IfIndex == bestIf;
        if (!chosen || (isBest && !chosenIsBest) || (isBest == chosenIsBest && a->Ipv4Metric < chosenMetric))
        {
            chosen = ip;
            chosenIsBest = isBest;
            chosenMetric = a->Ipv4Metric;
        }
    }
    return chosen;
}

// --- Driver ----------------------------------------------------------------------------

SlirpDriver::SlirpDriver(const melonDS::Platform::SendPacketCallback& callback, const SlirpAddressing& addressing) noexcept
    : Callback(callback), Addr(addressing)
{
    static const SlirpCb callbacks = [] {
        SlirpCb cb {};
        cb.send_packet = SendToGuest;
        cb.guest_error = GuestError;
        cb.clock_get_ns = ClockNs;
        cb.timer_new = TimerNew;
        cb.timer_free = TimerFree;
        cb.timer_mod = TimerMod;
        cb.register_poll_fd = RegisterPollFd;
        cb.unregister_poll_fd = UnregisterPollFd;
        cb.notify = Notify;
        return cb;
    }();

    SlirpConfig cfg {};
    cfg.version = 1;
    cfg.in_enabled = true;
    cfg.vnetwork.s_addr = htonl(Addr.guestIp & Addr.netmask);
    cfg.vnetmask.s_addr = htonl(Addr.netmask);
    cfg.vhost.s_addr = htonl(Addr.hostIp);
    cfg.vhostname = "ndsstation";
    cfg.vdhcp_start.s_addr = htonl(Addr.guestIp);
    cfg.vnameserver.s_addr = htonl(Addr.dnsIp);
    Ctx = slirp_new(&cfg, &callbacks, this);

    if (Addr.lanIp != 0)
    {
        // Bound to the LAN address only: other consoles on the home network reach this
        // PC here, nothing from the Internet does. A second copy of the app on the same
        // PC cannot bind it and simply runs without the relay.
        SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (s != INVALID_SOCKET)
        {
            BOOL exclusive = TRUE;
            setsockopt(s, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));
            sockaddr_in local {};
            local.sin_family = AF_INET;
            local.sin_addr.s_addr = htonl(Addr.lanIp);
            local.sin_port = htons(Addr.relayPort);
            u_long nonblocking = 1;
            if (bind(s, reinterpret_cast<sockaddr*>(&local), sizeof(local)) == 0 && ioctlsocket(s, FIONBIO, &nonblocking) == 0)
            {
                DisableUdpReset(s);
                Relay = s;
            }
            else
            {
                closesocket(s);
            }
        }
    }
}

SlirpDriver::~SlirpDriver() noexcept
{
    if (Relay != INVALID_SOCKET) closesocket(Relay);
    if (Ctx) slirp_cleanup(Ctx);
}

bool SlirpDriver::IsPeerConsole(uint32_t ip) const noexcept
{
    if (Relay == INVALID_SOCKET || (ip >> 24) != 10) return false;
    if (ip == Addr.guestIp || ip == Addr.hostIp || ip == Addr.dnsIp) return false;
    const uint32_t host = ip & 0xFF;
    return host != 0 && host != 0xFF;
}

int SlirpDriver::SendPacket(u8* data, int len) noexcept
{
    if (!Ctx || len < 14 || len > 2048) return 0;

    const uint16_t ethertype = Be16(data + 12);
    if ((ethertype == 0x0800 || ethertype == 0x0806) && !(data[6] & 1))
    {
        std::memcpy(GuestMac, data + 6, 6);
        GuestMacKnown = true;
    }

    if (HandleArp(data, len) || RelayToPeer(data, len)) return len;

    slirp_input(Ctx, data, len);
    return len;
}

bool SlirpDriver::HandleArp(const u8* data, int len) noexcept
{
    // Consoles on the same /24 talk to each other directly, so the DS asks for their MAC
    // address first. slirp only answers for itself; answer for the other consoles.
    if (len < 42 || Be16(data + 12) != 0x0806 || Be16(data + 20) != 1) return false;
    const uint32_t target = Be32(data + 38);
    if (!IsPeerConsole(target)) return false;

    u8 reply[42];
    u8 mac[6];
    FakeMac(target, mac);
    std::memcpy(reply, data + 6, 6);         // to the asking console
    std::memcpy(reply + 6, mac, 6);
    Put16(reply + 12, 0x0806);
    Put16(reply + 14, 1);                    // Ethernet
    Put16(reply + 16, 0x0800);               // IPv4
    reply[18] = 6;
    reply[19] = 4;
    Put16(reply + 20, 2);                    // reply
    std::memcpy(reply + 22, mac, 6);
    Put32(reply + 28, target);
    std::memcpy(reply + 32, data + 22, 6);
    std::memcpy(reply + 38, data + 28, 4);
    DeliverToGuest(reply, sizeof(reply));
    return true;
}

bool SlirpDriver::RelayToPeer(const u8* data, int len) noexcept
{
    if (len < 14 + 20 + 8 || Be16(data + 12) != 0x0800) return false;
    const u8* ip = data + 14;
    const int ihl = (ip[0] & 0x0F) * 4;
    if (ip[9] != 17 || ihl < 20 || len < 14 + ihl + 8) return false;
    const uint32_t dst = Be32(ip + 16);
    if (!IsPeerConsole(dst)) return false;
    if (Be16(ip + 6) & 0x3FFF) return true;  // fragments: the DS never sends them for P2P; drop

    const u8* udp = ip + ihl;
    const int ipPayload = std::min<int>(Be16(ip + 2), len - 14) - ihl;
    const int plen = std::min<int>(Be16(udp + 4), ipPayload) - 8;
    if (plen < 0 || plen > kMaxPayload) return true;

    u8 packet[kRelayHeader + kMaxPayload];
    std::memcpy(packet, kRelayMagic, 4);
    packet[4] = kRelayVersion;
    std::memcpy(packet + 5, ip + 12, 4);     // source console address
    std::memcpy(packet + 9, udp, 2);         // source port
    std::memcpy(packet + 11, ip + 16, 4);    // destination console address
    std::memcpy(packet + 15, udp + 2, 2);    // destination port
    std::memcpy(packet + kRelayHeader, udp + 8, plen);

    // The other console's PC: same first octet as ours, the rest from its address.
    sockaddr_in peer {};
    peer.sin_family = AF_INET;
    peer.sin_addr.s_addr = htonl((Addr.lanIp & 0xFF000000) | (dst & 0x00FFFFFF));
    peer.sin_port = htons(Addr.relayPort);
    sendto(Relay, reinterpret_cast<const char*>(packet), kRelayHeader + plen, 0, reinterpret_cast<sockaddr*>(&peer), sizeof(peer));
    return true;
}

void SlirpDriver::ReceiveFromPeers() noexcept
{
    if (Relay == INVALID_SOCKET) return;

    u8 packet[kRelayHeader + kMaxPayload + 1];
    for (int i = 0; i < 64; i++)
    {
        sockaddr_in from {};
        int fromLen = sizeof(from);
        const int n = recvfrom(Relay, reinterpret_cast<char*>(packet), sizeof(packet), 0, reinterpret_cast<sockaddr*>(&from), &fromLen);
        if (n < 0) return;   // WSAEWOULDBLOCK: nothing more queued
        if (n < kRelayHeader || n > kRelayHeader + kMaxPayload || std::memcmp(packet, kRelayMagic, 4) != 0 ||
            packet[4] != kRelayVersion || !GuestMacKnown)
            continue;

        const uint32_t src = Be32(packet + 5), dst = Be32(packet + 11);
        // Only for our console, and only from the PC that owns the claimed source address.
        if (dst != Addr.guestIp || (ntohl(from.sin_addr.s_addr) & 0x00FFFFFF) != (src & 0x00FFFFFF)) continue;

        const int plen = n - kRelayHeader;
        u8 frame[14 + 20 + 8 + kMaxPayload];
        u8* eth = frame;
        u8* ip = frame + 14;
        u8* udp = ip + 20;

        std::memcpy(eth, GuestMac, 6);
        FakeMac(src, eth + 6);
        Put16(eth + 12, 0x0800);

        ip[0] = 0x45;
        ip[1] = 0;
        Put16(ip + 2, static_cast<uint16_t>(20 + 8 + plen));
        Put16(ip + 4, IpId++);
        Put16(ip + 6, 0);
        ip[8] = 64;
        ip[9] = 17;
        Put16(ip + 10, 0);
        Put32(ip + 12, src);
        Put32(ip + 16, dst);
        Put16(ip + 10, Fold(SumWords(ip, 20)));

        std::memcpy(udp, packet + 9, 2);
        std::memcpy(udp + 2, packet + 15, 2);
        Put16(udp + 4, static_cast<uint16_t>(8 + plen));
        Put16(udp + 6, 0);
        std::memcpy(udp + 8, packet + kRelayHeader, plen);

        // UDP checksum over the pseudo-header; some DS network stacks insist on it.
        uint32_t sum = SumWords(ip + 12, 8);
        sum += 17 + 8 + plen;
        uint16_t check = Fold(SumWords(udp, 8 + plen, sum));
        Put16(udp + 6, check == 0 ? 0xFFFF : check);

        DeliverToGuest(frame, 14 + 20 + 8 + plen);
    }
}

int SlirpDriver::AddPoll(int fd, int events, void* opaque) noexcept
{
    auto& self = *static_cast<SlirpDriver*>(opaque);
    if (self.PollListSize >= PollListMax) return -1;

    SHORT wanted = 0;
    if (events & SLIRP_POLL_IN) wanted |= POLLRDNORM;
    if (events & SLIRP_POLL_OUT) wanted |= POLLWRNORM;
    // WSAPoll rejects POLLPRI; errors and hang-ups are always reported.

    int idx = self.PollListSize++;
    self.PollList[idx].fd = static_cast<SOCKET>(fd);
    self.PollList[idx].events = wanted;
    self.PollList[idx].revents = 0;
    return idx;
}

int SlirpDriver::GetREvents(int idx, void* opaque) noexcept
{
    auto& self = *static_cast<SlirpDriver*>(opaque);
    if (idx < 0 || idx >= self.PollListSize) return 0;

    SHORT got = self.PollList[idx].revents;
    int events = 0;
    if (got & (POLLRDNORM | POLLRDBAND)) events |= SLIRP_POLL_IN;
    if (got & POLLWRNORM) events |= SLIRP_POLL_OUT;
    if (got & POLLERR) events |= SLIRP_POLL_ERR;
    if (got & POLLHUP) events |= SLIRP_POLL_HUP;
    return events;
}

void SlirpDriver::RecvCheck() noexcept
{
    if (!Ctx) return;

    ReceiveFromPeers();

    // Called from the emulated Wi-Fi chip; never block the emulation thread.
    uint32_t timeout = 0;
    PollListSize = 0;
    slirp_pollfds_fill(Ctx, &timeout, &SlirpDriver::AddPoll, this);
    int result = PollListSize > 0 ? WSAPoll(PollList, PollListSize, 0) : 0;
    slirp_pollfds_poll(Ctx, result < 0, &SlirpDriver::GetREvents, this);
}

}
