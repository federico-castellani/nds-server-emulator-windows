// Regression test for the WFC peer-to-peer disconnect on Windows.
//
// The "console" sends a UDP datagram through slirp to a closed port on this PC. Windows
// answers with ICMP port-unreachable, which makes the next recvfrom() on slirp's socket
// fail with WSAECONNRESET. A correct driver must not turn that into an ICMP error for
// the console, because the DS network stack drops its peer-to-peer link when it gets one.
// The test then checks that a datagram from a third party still reaches the console on
// the same port, which is what NAT punch-through relies on.
#include <winsock2.h>
#include <ws2tcpip.h>

#include <chrono>
#include <cstdio>
#include <cstring>
#include <memory>
#include <thread>
#include <type_traits>
#include <vector>

#include <net/Net_Slirp.h>
#include "../src/CoreHooks.h"
#include "../src/SlirpDriver.h"

using melonDS::u8;

namespace
{

const u8 kGuestMac[6] = {0x00, 0x09, 0xBF, 0x11, 0x22, 0x33};
const u8 kBroadcast[6] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};
constexpr uint32_t kGuestIp = 0x0A400010;   // 10.64.0.16, slirp's first DHCP lease
constexpr uint32_t kGatewayIp = 0x0A400001;
constexpr uint16_t kGuestPort = 49200;

struct Captured
{
    int icmpUnreachable = 0;
    int udpToGuest = 0;
};

uint16_t Checksum(const u8* data, size_t len)
{
    uint32_t sum = 0;
    for (size_t i = 0; i + 1 < len; i += 2) sum += (data[i] << 8) | data[i + 1];
    if (len & 1) sum += data[len - 1] << 8;
    while (sum >> 16) sum = (sum & 0xFFFF) + (sum >> 16);
    return static_cast<uint16_t>(~sum);
}

void Put16(u8* p, uint16_t v) { p[0] = v >> 8; p[1] = v & 0xFF; }
void Put32(u8* p, uint32_t v) { Put16(p, v >> 16); Put16(p + 2, v & 0xFFFF); }

std::vector<u8> ArpRequest()
{
    std::vector<u8> f(42, 0);
    memcpy(&f[0], kBroadcast, 6);
    memcpy(&f[6], kGuestMac, 6);
    Put16(&f[12], 0x0806);
    Put16(&f[14], 1); Put16(&f[16], 0x0800); f[18] = 6; f[19] = 4; Put16(&f[20], 1);
    memcpy(&f[22], kGuestMac, 6); Put32(&f[28], kGuestIp);
    Put32(&f[38], kGatewayIp);
    return f;
}

std::vector<u8> UdpFrame(uint32_t dstIp, uint16_t dstPort, const char* payload)
{
    const size_t plen = strlen(payload);
    std::vector<u8> f(14 + 20 + 8 + plen, 0);
    memcpy(&f[0], kBroadcast, 6);   // slirp accepts any destination MAC
    memcpy(&f[6], kGuestMac, 6);
    Put16(&f[12], 0x0800);
    u8* ip = &f[14];
    ip[0] = 0x45; Put16(ip + 2, static_cast<uint16_t>(20 + 8 + plen)); ip[8] = 64; ip[9] = 17;
    Put32(ip + 12, kGuestIp); Put32(ip + 16, dstIp);
    Put16(ip + 10, Checksum(ip, 20));
    u8* udp = ip + 20;
    Put16(udp, kGuestPort); Put16(udp + 2, dstPort); Put16(udp + 4, static_cast<uint16_t>(8 + plen));
    memcpy(udp + 8, payload, plen);   // UDP checksum 0 = none
    return f;
}

void Inspect(const u8* frame, int len, Captured& out)
{
    if (len < 34 || frame[12] != 0x08 || frame[13] != 0x00) return;
    const u8* ip = frame + 14;
    if (ip[9] == 1 && len >= 34 + 2 && ip[20] == 3) out.icmpUnreachable++;
    if (ip[9] == 17 && len >= 42 && ((ip[22] << 8) | ip[23]) == kGuestPort) out.udpToGuest++;
}

uint16_t ClosedLocalPort()
{
    SOCKET s = socket(AF_INET, SOCK_DGRAM, 0);
    sockaddr_in a {}; a.sin_family = AF_INET; a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    bind(s, reinterpret_cast<sockaddr*>(&a), sizeof(a));
    int alen = sizeof(a);
    getsockname(s, reinterpret_cast<sockaddr*>(&a), &alen);
    closesocket(s);   // port is now free: datagrams to it bounce with ICMP
    return ntohs(a.sin_port);
}

template <class Driver>
std::unique_ptr<Driver> MakeDriver(const melonDS::Platform::SendPacketCallback& cb)
{
    if constexpr (std::is_same_v<Driver, ndscore::SlirpDriver>)
        return std::make_unique<Driver>(cb, ndscore::SlirpAddressing {});   // melonDS' 10.64.0.x layout
    else
        return std::make_unique<Driver>(cb);
}

template <class Driver>
Captured Run(const char* name)
{
    Captured captured;
    auto driver = MakeDriver<Driver>([&](const u8* data, int len) { Inspect(data, len, captured); });
    auto pump = [&](int ms) {
        auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);
        while (std::chrono::steady_clock::now() < until)
        {
            driver->RecvCheck();
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
    };

    auto arp = ArpRequest();
    driver->SendPacket(arp.data(), static_cast<int>(arp.size()));
    pump(50);

    // 1. Probe a closed port, like the first punch-through packets towards a peer.
    // slirp rewrites 10.64.0.1 (its host alias) to 127.0.0.1 on the host side.
    const uint16_t closed = ClosedLocalPort();
    for (int i = 0; i < 3; i++)
    {
        auto probe = UdpFrame(kGatewayIp, closed, "probe");
        driver->SendPacket(probe.data(), static_cast<int>(probe.size()));
        pump(100);
    }

    // 2. A peer that knows our public endpoint now sends us a datagram. Find out
    // which host port slirp uses for the console's socket, then send to it.
    // slirp binds its socket to an ephemeral port; locate it by sending from the
    // console to a local echo socket and reading the source port.
    SOCKET echo = socket(AF_INET, SOCK_DGRAM, 0);
    sockaddr_in ea {}; ea.sin_family = AF_INET; ea.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    bind(echo, reinterpret_cast<sockaddr*>(&ea), sizeof(ea));
    int ealen = sizeof(ea);
    getsockname(echo, reinterpret_cast<sockaddr*>(&ea), &ealen);
    auto hello = UdpFrame(kGatewayIp, ntohs(ea.sin_port), "hello");
    driver->SendPacket(hello.data(), static_cast<int>(hello.size()));
    pump(100);

    sockaddr_in from {}; int flen = sizeof(from); char buf[64];
    u_long nonblocking = 1; ioctlsocket(echo, FIONBIO, &nonblocking);
    int got = recvfrom(echo, buf, sizeof(buf), 0, reinterpret_cast<sockaddr*>(&from), &flen);
    const int before = captured.udpToGuest;
    if (got > 0)
    {
        SOCKET peer = socket(AF_INET, SOCK_DGRAM, 0);   // a different endpoint: the "other player"
        sendto(peer, "peer", 4, 0, reinterpret_cast<sockaddr*>(&from), flen);
        closesocket(peer);
        pump(200);
    }
    closesocket(echo);

    const bool reachable = captured.udpToGuest > before;
    printf("%-26s ICMP unreachable to console: %d   peer datagram delivered: %s\n",
           name, captured.icmpUnreachable, got > 0 ? (reachable ? "yes" : "NO") : "n/a (no slirp socket)");
    return captured;
}

// --- Consoles on two PCs of the same home network ------------------------------------
// Two drivers stand in for two PCs (127.0.1.20 and 127.0.1.30). Console A resolves and
// messages console B at B's local address, as DWC's TELL_ADDR makes it do, and B answers.

struct Received
{
    std::vector<std::vector<u8>> frames;
};

std::vector<u8> UdpFrameFrom(const u8* mac, uint32_t src, uint16_t sport, uint32_t dst, uint16_t dport, const char* payload)
{
    auto f = UdpFrame(dst, dport, payload);
    memcpy(&f[6], mac, 6);
    Put32(&f[14 + 12], src);
    Put16(&f[14 + 10], 0);
    Put16(&f[14 + 10], Checksum(&f[14], 20));
    Put16(&f[14 + 20], sport);
    return f;
}

std::vector<u8> ArpFor(const u8* mac, uint32_t sender, uint32_t target)
{
    auto f = ArpRequest();
    memcpy(&f[6], mac, 6);
    memcpy(&f[22], mac, 6);
    Put32(&f[28], sender);
    Put32(&f[38], target);
    return f;
}

// Finds a UDP frame for `dport` carrying `payload` and checks its IP and UDP checksums.
bool HasValidUdp(const Received& r, uint32_t src, uint16_t sport, uint32_t dst, uint16_t dport, const char* payload)
{
    for (const auto& f : r.frames)
    {
        if (f.size() < 42 || f[12] != 0x08 || f[13] != 0x00 || f[14 + 9] != 17) continue;
        const u8* ip = &f[14];
        const u8* udp = ip + 20;
        auto be32 = [](const u8* p) { return (uint32_t(p[0]) << 24) | (p[1] << 16) | (p[2] << 8) | p[3]; };
        auto be16 = [](const u8* p) { return uint16_t((p[0] << 8) | p[1]); };
        if (be32(ip + 12) != src || be32(ip + 16) != dst || be16(udp) != sport || be16(udp + 2) != dport) continue;
        const size_t plen = be16(udp + 4) - 8;
        if (plen != strlen(payload) || memcmp(udp + 8, payload, plen) != 0) return false;
        if (Checksum(ip, 20) != 0) return false;
        std::vector<u8> pseudo(ip + 12, ip + 20);
        pseudo.push_back(0); pseudo.push_back(17);
        pseudo.push_back(udp[4]); pseudo.push_back(udp[5]);
        pseudo.insert(pseudo.end(), udp, udp + 8 + plen);
        return Checksum(pseudo.data(), pseudo.size()) == 0;
    }
    return false;
}

bool RunLanRelay()
{
    const uint32_t lanA = 0x7F000114, lanB = 0x7F00011E;   // 127.0.1.20, 127.0.1.30
    const auto addrA = ndscore::SlirpAddressing::ForLan(lanA);
    const auto addrB = ndscore::SlirpAddressing::ForLan(lanB);
    const u8 macA[6] = {0x00, 0x09, 0xBF, 0xAA, 0xAA, 0xAA};
    const u8 macB[6] = {0x00, 0x09, 0xBF, 0xBB, 0xBB, 0xBB};

    Received rxA, rxB;
    ndscore::SlirpDriver a([&](const u8* d, int n) { rxA.frames.emplace_back(d, d + n); }, addrA);
    ndscore::SlirpDriver b([&](const u8* d, int n) { rxB.frames.emplace_back(d, d + n); }, addrB);
    if (!a.RelayActive() || !b.RelayActive())
    {
        printf("LAN relay:                 could not bind the relay sockets\n");
        return false;
    }
    auto pump = [&](int ms) {
        auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);
        while (std::chrono::steady_clock::now() < until)
        {
            a.RecvCheck();
            b.RecvCheck();
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
    };

    // Each console first talks to its own gateway (as after DHCP), so each driver knows its MAC.
    auto gwA = ArpFor(macA, addrA.guestIp, addrA.hostIp);
    auto gwB = ArpFor(macB, addrB.guestIp, addrB.hostIp);
    a.SendPacket(gwA.data(), static_cast<int>(gwA.size()));
    b.SendPacket(gwB.data(), static_cast<int>(gwB.size()));
    pump(30);

    // A looks up B's MAC: same /24, so the DS asks directly instead of via the gateway.
    rxA.frames.clear();
    auto who = ArpFor(macA, addrA.guestIp, addrB.guestIp);
    a.SendPacket(who.data(), static_cast<int>(who.size()));
    bool arpOk = false;
    for (const auto& f : rxA.frames)
        if (f.size() >= 42 && f[12] == 0x08 && f[13] == 0x06 && f[21] == 2 &&
            ((uint32_t(f[28]) << 24) | (f[29] << 16) | (f[30] << 8) | f[31]) == addrB.guestIp)
            arpOk = true;

    auto toB = UdpFrameFrom(macA, addrA.guestIp, 50112, addrB.guestIp, 40000, "hello-from-A");
    a.SendPacket(toB.data(), static_cast<int>(toB.size()));
    pump(100);
    const bool aToB = HasValidUdp(rxB, addrA.guestIp, 50112, addrB.guestIp, 40000, "hello-from-A");

    auto toA = UdpFrameFrom(macB, addrB.guestIp, 40000, addrA.guestIp, 50112, "reply-from-B");
    b.SendPacket(toA.data(), static_cast<int>(toA.size()));
    pump(100);
    const bool bToA = HasValidUdp(rxA, addrB.guestIp, 40000, addrA.guestIp, 50112, "reply-from-B");

    printf("LAN relay:                 consoles 10.0.1.20 <-> 10.0.1.30   ARP: %s   A->B: %s   B->A: %s\n",
           arpOk ? "yes" : "NO", aToB ? "yes" : "NO", bToA ? "yes" : "NO");
    return arpOk && aToB && bToA;
}

}

// Platform.cpp forwards to these; the test has no emulator behind them.
namespace ndscore_hooks
{
void OnSaveWritten() {}
void OnFirmwareWritten() {}
void OnStopSignal() {}
int NetSend(u8*, int) { return 0; }
int NetRecv(u8*) { return 0; }
}

int main()
{
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);

    Run<melonDS::Net_Slirp>("melonDS Net_Slirp:");
    Captured fixed = Run<ndscore::SlirpDriver>("ndscore SlirpDriver:");
    const bool lan = RunLanRelay();

    WSACleanup();
    const bool pass = fixed.icmpUnreachable == 0 && fixed.udpToGuest > 0 && lan;
    printf("%s\n", pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}
