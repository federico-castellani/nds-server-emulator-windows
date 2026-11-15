#include <SDL.h>
#include <winsock2.h>
#include <windows.h>
#include <iphlpapi.h>

#include <NDS.h>
#include <NDSCart.h>
#include <Savestate.h>
#include <SPI_Firmware.h>
#include <net/Net.h>
#include <net/Net_PCap.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <map>
#include <thread>

#include "ndscore.h"
#include "CoreHooks.h"
#include "SlirpDriver.h"

using namespace melonDS;

namespace
{

constexpr double kFrameRate = 59.8260982880808;
constexpr int kFramePixels = NDSCORE_SCREEN_WIDTH * NDSCORE_SCREEN_HEIGHT;

struct Core
{
    bool initialized = false;

    // Console; guarded by emuMutex while the emulation thread runs a frame.
    std::mutex emuMutex;
    std::unique_ptr<NDS> nds;

    std::thread emuThread;
    std::atomic<bool> running{false};
    std::atomic<bool> stopRequested{false};
    std::atomic<bool> paused{false};
    std::mutex pauseMutex;
    std::condition_variable pauseCv;

    std::atomic<float> speed{1.0f};
    std::atomic<float> volume{0.5f};
    std::atomic<float> fps{0.0f};

    std::atomic<uint32_t> inputMask{0};
    std::atomic<int32_t> touchDown{0};
    std::atomic<int32_t> touchX{0};
    std::atomic<int32_t> touchY{0};

    std::mutex frameMutex;
    uint32_t frame[kFramePixels * 2] = {};
    uint64_t frameId = 0;

    std::atomic<uint32_t> saveCounter{0};
    std::atomic<uint32_t> firmwareCounter{0};

    // Audio: the SDL callback drains the SPU; the emulation thread waits on audioCv
    // when the SPU buffer is full, which keeps emulation in step with the sound card.
    SDL_AudioDeviceID audioDevice = 0;
    int audioBufferSamples = 512;
    int audioFrequency = 48000;
    std::mutex audioMutex;
    std::condition_variable audioCv;

    std::thread padThread;
    std::atomic<bool> padThreadRun{false};
    std::atomic<uint32_t> padButtons{0};
    std::mutex padNameMutex;
    std::string padName;

    std::unique_ptr<Net> net;
    std::atomic<int32_t> netMode{NDSCORE_NET_NONE};
    int32_t netPreference = -1;
    std::mutex netDescriptionMutex;
    std::string netDescription;

    std::mutex errorMutex;
    std::string lastError;
};

Core g_core;

void SetError(const std::string& message)
{
    std::lock_guard lock(g_core.errorMutex);
    g_core.lastError = message;
}

// --- Audio -----------------------------------------------------------------

void AudioCallback(void*, Uint8* stream, int len)
{
    const int frames = len / 4;
    int read = 0;
    {
        std::lock_guard lock(g_core.audioMutex);
        if (g_core.nds && g_core.running && !g_core.paused)
            read = g_core.nds->SPU.ReadOutput(reinterpret_cast<s16*>(stream), frames);
    }
    g_core.audioCv.notify_one();

    const float speed = g_core.speed.load();
    const float volume = g_core.volume.load();
    if (read <= 0 || volume <= 0.001f || speed != 1.0f)
    {
        std::memset(stream, 0, len);
        return;
    }

    auto* samples = reinterpret_cast<s16*>(stream);
    if (volume < 0.999f)
    {
        for (int i = 0; i < read * 2; i++)
            samples[i] = static_cast<s16>(samples[i] * volume);
    }

    // Stretch the last sample instead of inserting silence to avoid pops on underrun.
    auto* pairs = reinterpret_cast<uint32_t*>(stream);
    for (int i = read; i < frames; i++)
        pairs[i] = pairs[read - 1];
}

void OpenAudio()
{
    SDL_AudioSpec wanted{}, have{};
    wanted.freq = 48000;
    wanted.format = AUDIO_S16LSB;
    wanted.channels = 2;
    wanted.samples = 512;
    wanted.callback = AudioCallback;
    g_core.audioDevice = SDL_OpenAudioDevice(nullptr, 0, &wanted, &have, SDL_AUDIO_ALLOW_FREQUENCY_CHANGE);
    if (g_core.audioDevice)
    {
        g_core.audioBufferSamples = have.samples;
        g_core.audioFrequency = have.freq;
        SDL_PauseAudioDevice(g_core.audioDevice, 0);
    }
}

// --- Gamepad ---------------------------------------------------------------

void PadThreadMain()
{
    SDL_GameController* pad = nullptr;
    while (g_core.padThreadRun)
    {
        SDL_GameControllerUpdate();

        if (pad && !SDL_GameControllerGetAttached(pad))
        {
            SDL_GameControllerClose(pad);
            pad = nullptr;
            std::lock_guard lock(g_core.padNameMutex);
            g_core.padName.clear();
        }
        if (!pad)
        {
            for (int i = 0; i < SDL_NumJoysticks(); i++)
            {
                if (!SDL_IsGameController(i)) continue;
                pad = SDL_GameControllerOpen(i);
                if (pad)
                {
                    const char* name = SDL_GameControllerName(pad);
                    std::lock_guard lock(g_core.padNameMutex);
                    g_core.padName = name ? name : "Controller";
                    break;
                }
            }
        }

        uint32_t buttons = 0;
        if (pad)
        {
            for (int b = 0; b <= SDL_CONTROLLER_BUTTON_DPAD_RIGHT; b++)
            {
                if (SDL_GameControllerGetButton(pad, static_cast<SDL_GameControllerButton>(b)))
                    buttons |= 1u << b;
            }
            const int deadzone = 16000;
            const Sint16 lx = SDL_GameControllerGetAxis(pad, SDL_CONTROLLER_AXIS_LEFTX);
            const Sint16 ly = SDL_GameControllerGetAxis(pad, SDL_CONTROLLER_AXIS_LEFTY);
            if (ly < -deadzone) buttons |= NDSCORE_PAD_LSTICK_UP;
            if (ly > deadzone) buttons |= NDSCORE_PAD_LSTICK_DOWN;
            if (lx < -deadzone) buttons |= NDSCORE_PAD_LSTICK_LEFT;
            if (lx > deadzone) buttons |= NDSCORE_PAD_LSTICK_RIGHT;
            if (SDL_GameControllerGetAxis(pad, SDL_CONTROLLER_AXIS_TRIGGERLEFT) > deadzone) buttons |= NDSCORE_PAD_LTRIGGER;
            if (SDL_GameControllerGetAxis(pad, SDL_CONTROLLER_AXIS_TRIGGERRIGHT) > deadzone) buttons |= NDSCORE_PAD_RTRIGGER;
        }
        g_core.padButtons = buttons;

        std::this_thread::sleep_for(std::chrono::milliseconds(4));
    }
    if (pad) SDL_GameControllerClose(pad);
}

// --- Network ---------------------------------------------------------------

struct HostAdapter
{
    bool found = false;
    bool wired = false;
    bool up = false;
    bool isVirtual = false;
    std::string description;
};

// Looks up what Windows knows about a pcap device ("\Device\NPF_{GUID}").
HostAdapter DescribeAdapter(const std::string& pcapName)
{
    HostAdapter result;
    const auto brace = pcapName.find('{');
    if (brace == std::string::npos) return result;
    const std::string guid = pcapName.substr(brace);

    ULONG size = 16 * 1024;
    std::unique_ptr<char[]> buffer;
    ULONG status;
    do
    {
        buffer = std::make_unique<char[]>(size);
        status = GetAdaptersAddresses(AF_INET, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
                                      nullptr, reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.get()), &size);
    } while (status == ERROR_BUFFER_OVERFLOW);
    if (status != NO_ERROR) return result;

    for (auto* a = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.get()); a; a = a->Next)
    {
        if (_stricmp(a->AdapterName, guid.c_str()) != 0) continue;

        char description[256] = {};
        WideCharToMultiByte(CP_UTF8, 0, a->Description, -1, description, sizeof(description) - 1, nullptr, nullptr);
        std::string lower = description;
        std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });

        static const char* kVirtualHints[] = {
            "virtual", "vpn", "hyper-v", "vmware", "virtualbox", "tap-", "wireguard", "tunnel",
            "loopback", "bluetooth", "miniport", "npcap", "zerotier", "tailscale", "hamachi",
        };
        result.found = true;
        result.description = description;
        result.wired = a->IfType == IF_TYPE_ETHERNET_CSMACD;
        result.up = a->OperStatus == IfOperStatusUp;
        result.isVirtual = a->PhysicalAddressLength != 6 ||
            std::any_of(std::begin(kVirtualHints), std::end(kVirtualHints),
                        [&](const char* hint) { return lower.find(hint) != std::string::npos; });
        return result;
    }
    return result;
}

void SetNetDescription(std::string text)
{
    std::lock_guard lock(g_core.netDescriptionMutex);
    g_core.netDescription = std::move(text);
}

// Picks how the emulated Wi-Fi reaches the Internet.
//
// Npcap ("direct") bridges the console onto the LAN with its own MAC address, so it
// gets an address from the router and behaves exactly like a real DS, which is the
// most reliable setup for peer-to-peer. Injecting frames with a foreign MAC only works
// on wired Ethernet, though: Wi-Fi access points drop them. Everything else uses Slirp.
void ConfigureNetwork(int32_t preference)
{
    if (!g_core.net)
    {
        g_core.net = std::make_unique<Net>();
        g_core.net->RegisterInstance(0);
    }
    if (preference == g_core.netPreference && g_core.netMode != NDSCORE_NET_NONE) return;
    g_core.netPreference = preference;

    auto enqueue = [](const u8* data, int len) {
        if (g_core.net) g_core.net->RXEnqueue(data, len);
    };

    std::string fallbackReason;
    if (preference != NDSCORE_NETPREF_SLIRP)
    {
        if (auto pcap = LibPCap::New())
        {
            const AdapterData* best = nullptr;
            HostAdapter bestInfo;
            int bestScore = -1;
            for (const auto& adapter : pcap->GetAdapters())
            {
                if ((adapter.Flags & 1) || adapter.IP_v4[0] == 0 || adapter.IP_v4[0] == 127 || adapter.IP_v4[0] == 169)
                    continue;
                HostAdapter info = DescribeAdapter(adapter.DeviceName);
                if (!info.found || !info.up || info.isVirtual) continue;
                if (preference == NDSCORE_NETPREF_AUTO && !info.wired) continue;
                int score = info.wired ? 2 : 1;
                if (score > bestScore)
                {
                    best = &adapter;
                    bestInfo = info;
                    bestScore = score;
                }
            }

            if (best)
            {
                if (auto driver = pcap->Open(*best, enqueue))
                {
                    g_core.net->SetDriver(std::move(driver));
                    g_core.netMode = NDSCORE_NET_PCAP;
                    char ip[32];
                    snprintf(ip, sizeof(ip), "%u.%u.%u.%u", best->IP_v4[0], best->IP_v4[1], best->IP_v4[2], best->IP_v4[3]);
                    SetNetDescription("Npcap su " + bestInfo.description + " (" + ip + ")" +
                                      (bestInfo.wired ? "" : " - Wi-Fi: potrebbe non funzionare"));
                    return;
                }
                fallbackReason = "impossibile aprire " + bestInfo.description;
            }
            else
            {
                fallbackReason = preference == NDSCORE_NETPREF_AUTO
                    ? "nessuna scheda Ethernet cablata attiva"
                    : "nessuna scheda di rete fisica attiva";
            }
        }
        else
        {
            fallbackReason = "Npcap non installato";
        }
    }

    // Release the previous driver first so its LAN relay port is free to bind again.
    g_core.net->SetDriver(nullptr);
    auto slirp = std::make_unique<ndscore::SlirpDriver>(
        enqueue, ndscore::SlirpAddressing::ForLan(ndscore::SlirpAddressing::DetectLanAddress()));
    if (!slirp->IsValid())
    {
        g_core.netMode = NDSCORE_NET_NONE;
        SetNetDescription("Rete non disponibile");
        return;
    }
    const auto& addr = slirp->Addressing();
    auto IpText = [](uint32_t ip) {
        char text[16];
        snprintf(text, sizeof(text), "%u.%u.%u.%u", ip >> 24, (ip >> 16) & 0xFF, (ip >> 8) & 0xFF, ip & 0xFF);
        return std::string(text);
    };
    std::string lan = !addr.lanIp ? " - rete locale non rilevata, niente partite con PC della stessa rete"
        : slirp->RelayActive() ? " - console " + IpText(addr.guestIp) + ", collegamento con la rete locale attivo (UDP " +
                                     std::to_string(addr.relayPort) + ")"
                               : " - porta " + std::to_string(addr.relayPort) + " occupata (un'altra copia dell'app?), niente partite con PC della stessa rete";
    g_core.net->SetDriver(std::move(slirp));
    g_core.netMode = NDSCORE_NET_SLIRP;
    SetNetDescription((fallbackReason.empty() || preference == NDSCORE_NETPREF_SLIRP
        ? "Rete integrata (NAT)"
        : "Rete integrata (NAT) - " + fallbackReason) + lan);
}

// --- Emulation thread ------------------------------------------------------

void CopyFramebuffers()
{
    void* top = nullptr;
    void* bottom = nullptr;
    if (!g_core.nds->GPU.GetFramebuffers(&top, &bottom) || !top || !bottom) return;

    std::lock_guard lock(g_core.frameMutex);
    const uint32_t* src[2] = {static_cast<const uint32_t*>(top), static_cast<const uint32_t*>(bottom)};
    for (int s = 0; s < 2; s++)
    {
        uint32_t* dst = g_core.frame + s * kFramePixels;
        for (int i = 0; i < kFramePixels; i++)
            dst[i] = src[s][i] | 0xFF000000u;
    }
    g_core.frameId++;
}

void WaitForAudio()
{
    if (!g_core.audioDevice) return;
    std::unique_lock lock(g_core.audioMutex);
    g_core.audioCv.wait_for(lock, std::chrono::milliseconds(50), [] {
        return !g_core.nds || g_core.nds->SPU.GetOutputSize() < g_core.audioBufferSamples || g_core.stopRequested;
    });
}

void EmuThreadMain()
{
    using clock = std::chrono::steady_clock;
    auto nextFrame = clock::now();
    auto fpsWindowStart = clock::now();
    int framesInWindow = 0;

    while (!g_core.stopRequested)
    {
        if (g_core.paused)
        {
            std::unique_lock lock(g_core.pauseMutex);
            g_core.pauseCv.wait(lock, [] { return !g_core.paused || g_core.stopRequested; });
            nextFrame = clock::now();
            fpsWindowStart = nextFrame;
            framesInWindow = 0;
            continue;
        }

        {
            std::lock_guard lock(g_core.emuMutex);
            NDS& nds = *g_core.nds;

            nds.SetKeyMask(~g_core.inputMask.load() & 0xFFF);
            if (g_core.touchDown)
                nds.TouchScreen(static_cast<u16>(g_core.touchX.load()), static_cast<u16>(g_core.touchY.load()));
            else
                nds.ReleaseScreen();

            nds.RunFrame();
            CopyFramebuffers();
        }

        framesInWindow++;
        const auto now = clock::now();
        const double windowSeconds = std::chrono::duration<double>(now - fpsWindowStart).count();
        if (windowSeconds >= 1.0)
        {
            g_core.fps = static_cast<float>(framesInWindow / windowSeconds);
            fpsWindowStart = now;
            framesInWindow = 0;
        }

        const float speed = g_core.speed.load();
        if (speed == 1.0f)
            WaitForAudio();

        if (speed > 0.0f)
        {
            const auto step = std::chrono::duration_cast<clock::duration>(std::chrono::duration<double>(1.0 / (kFrameRate * speed)));
            nextFrame += step;
            const auto current = clock::now();
            if (nextFrame > current)
                std::this_thread::sleep_until(nextFrame);
            else if (current - nextFrame > step * 3)
                nextFrame = current; // fell too far behind, don't try to catch up
        }
        else
        {
            nextFrame = clock::now();
        }
    }
}

void JoinEmuThread()
{
    if (!g_core.emuThread.joinable()) return;
    g_core.stopRequested = true;
    g_core.pauseCv.notify_all();
    g_core.audioCv.notify_all();
    g_core.emuThread.join();
    g_core.running = false;
}

void ApplyFirmwareSettings(Firmware& firmware, const ndscore_boot_args& args)
{
    auto& ap = firmware.GetAccessPoints()[0];
    if (args.primary_dns[0] | args.primary_dns[1] | args.primary_dns[2] | args.primary_dns[3])
    {
        std::copy(std::begin(args.primary_dns), std::end(args.primary_dns), ap.PrimaryDns.begin());
        std::copy(std::begin(args.secondary_dns), std::end(args.secondary_dns), ap.SecondaryDns.begin());
        ap.ConnectionConfigured = 0x01;
    }

    if (args.nickname && *args.nickname)
    {
        wchar_t wide[16] = {};
        int len = MultiByteToWideChar(CP_UTF8, 0, args.nickname, -1, wide, 16);
        len = std::clamp(len - 1, 0, 10);
        for (auto& user : firmware.GetUserData())
        {
            std::memset(user.Nickname, 0, sizeof(user.Nickname));
            for (int i = 0; i < len; i++) user.Nickname[i] = static_cast<char16_t>(wide[i]);
            user.NameLength = static_cast<u16>(len);
        }
    }

    firmware.UpdateChecksums();
}

}

// --- Hooks from Platform.cpp -------------------------------------------------

// --- Network diagnostics -------------------------------------------------------
// Counts the console's IPv4 traffic per remote endpoint, so a failed battle or trade
// shows where packets stop: sent to the other player but nothing back points at NAT or
// a firewall; ICMP errors point at the path in between.

namespace
{

struct EndpointStats
{
    uint64_t udpOut = 0, udpIn = 0, tcpOut = 0, tcpIn = 0;
    std::chrono::steady_clock::time_point first, last;
};

struct NetStats
{
    std::mutex mutex;
    uint32_t consoleIp = 0;
    std::map<std::pair<uint32_t, uint16_t>, EndpointStats> endpoints;
    uint64_t icmpToConsole = 0;
    std::string icmpLog;
    // Chronological log of WFC protocol steps (matchmaking messages, NAT negotiation,
    // first contact with each peer), which is where a failed battle or trade breaks.
    std::string timeline;
    int timelineLines = 0;
    uint64_t qr2Heartbeats = 0, qr2Keepalives = 0;
    int dumps = 0;
    std::chrono::steady_clock::time_point start = std::chrono::steady_clock::now();
};

NetStats g_netStats;

uint32_t Be32(const u8* p) { return (uint32_t(p[0]) << 24) | (p[1] << 16) | (p[2] << 8) | p[3]; }
uint16_t Be16(const u8* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }

std::string IpText(uint32_t ip)
{
    char text[16];
    snprintf(text, sizeof(text), "%u.%u.%u.%u", ip >> 24, (ip >> 16) & 0xFF, (ip >> 8) & 0xFF, ip & 0xFF);
    return text;
}

constexpr int kTimelineMax = 600;

void AddEvent(const char* fmt, ...)
{
    if (g_netStats.timelineLines >= kTimelineMax) return;
    char text[240];
    va_list args;
    va_start(args, fmt);
    vsnprintf(text, sizeof(text), fmt, args);
    va_end(args);
    const double t = std::chrono::duration<double>(std::chrono::steady_clock::now() - g_netStats.start).count();
    char line[280];
    snprintf(line, sizeof(line), "  %7.2fs  %s\n", t, text);
    g_netStats.timeline += line;
    if (++g_netStats.timelineLines == kTimelineMax) g_netStats.timeline += "  ... (troncato)\n";
}

constexpr int kDumpMax = 40;

// Appends a hex + ASCII dump of a payload under the last timeline event.
void AddDump(const u8* p, int len, int maxBytes = 128)
{
    if (g_netStats.timelineLines >= kTimelineMax || g_netStats.dumps >= kDumpMax || len <= 0) return;
    g_netStats.dumps++;
    const int n = std::min(len, maxBytes);
    for (int off = 0; off < n; off += 16)
    {
        char line[120];
        int pos = snprintf(line, sizeof(line), "              %04X ", off);
        for (int i = 0; i < 16; i++)
            pos += snprintf(line + pos, sizeof(line) - pos, off + i < n ? " %02X" : "   ", off + i < n ? p[off + i] : 0);
        pos += snprintf(line + pos, sizeof(line) - pos, "  ");
        for (int i = 0; i < 16 && off + i < n; i++)
            line[pos++] = (p[off + i] >= 0x20 && p[off + i] < 0x7F) ? static_cast<char>(p[off + i]) : '.';
        line[pos++] = '\n';
        line[pos] = 0;
        g_netStats.timeline += line;
        g_netStats.timelineLines++;
    }
    if (len > n)
    {
        g_netStats.timeline += "              ... (" + std::to_string(len) + " byte in totale)\n";
        g_netStats.timelineLines++;
    }
}

std::string EndpointText(uint32_t ip, uint16_t port)
{
    return IpText(ip) + ":" + std::to_string(port);
}

bool IsWellKnownPort(uint16_t port)
{
    return port == 53 || port == 67 || port == 68 || port == 80 || port == 443 || port == 27900 ||
           port == 27901 || port == 28910 || port == 29900 || port == 29901;
}

const char* Qr2Name(uint8_t type, bool outgoing)
{
    if (outgoing)
    {
        switch (type)
        {
        case 0x01: return "risposta al challenge";
        case 0x03: return "heartbeat";
        case 0x07: return "conferma del messaggio di collegamento";
        case 0x08: return "keepalive";
        case 0x09: return "richiesta disponibilita";
        default: return nullptr;
        }
    }
    switch (type)
    {
    case 0x01: return "challenge";
    case 0x06: return "MESSAGGIO DI COLLEGAMENTO (un altro giocatore vuole connettersi)";
    case 0x08: return "keepalive";
    case 0x09: return "risposta disponibilita";
    case 0x0A: return "registrazione completata";
    default: return nullptr;
    }
}

const char* NatNegName(uint8_t type)
{
    static const char* names[] = {
        "INIT", "INIT_ACK", "ERT_TEST", "ERT_ACK", "STATE_UPDATE", "CONNECT", "CONNECT_ACK", "CONNECT_PING",
        "BACKUP_TEST", "BACKUP_ACK", "ADDRESS_CHECK", "ADDRESS_REPLY", "NATIFY_REQUEST", "REPORT",
        "REPORT_ACK", "PREINIT", "PREINIT_ACK",
    };
    return type < sizeof(names) / sizeof(names[0]) ? names[type] : "sconosciuto";
}

void RecordUdpPayload(uint32_t remote, uint16_t remotePort, uint16_t localPort, const u8* p, int plen, bool outgoing,
                      bool firstInDirection)
{
    const char* dir = outgoing ? "->" : "<-";
    static const u8 kNatNegMagic[6] = {0xFD, 0xFC, 0x1E, 0x66, 0x6A, 0xB2};

    if (plen >= 12 && std::memcmp(p, kNatNegMagic, 6) == 0)
    {
        const uint8_t type = p[7];
        const uint32_t cookie = Be32(p + 8);
        char extra[96] = "";
        if (type == 0x05 && plen >= 18)        // CONNECT: where the server says the peer is
            snprintf(extra, sizeof(extra), " peer=%s", EndpointText(Be32(p + 12), Be16(p + 16)).c_str());
        else if (type == 0x00 && plen >= 21)   // INIT: the console's own private address
            snprintf(extra, sizeof(extra), " tipo-porta=%u indirizzo-locale=%s", p[12],
                     EndpointText(Be32(p + 15), Be16(p + 19)).c_str());
        else if (type == 0x0D && plen >= 15)   // REPORT: port type, client index, outcome
            snprintf(extra, sizeof(extra), " esito=%u", p[14]);
        AddEvent("NATNEG %s %s %s (porta console %u, cookie %08X)%s", NatNegName(type), dir,
                 EndpointText(remote, remotePort).c_str(), localPort, cookie, extra);
        return;
    }

    if (remotePort == 27900 && plen >= 1)
    {
        // Server-to-console packets start with FE FD, followed by the type.
        const bool framed = !outgoing && plen >= 3 && p[0] == 0xFE && p[1] == 0xFD;
        const uint8_t type = framed ? p[2] : p[0];
        if (type == 0x03 && outgoing)
        {
            if (g_netStats.qr2Heartbeats++ == 0)
                AddEvent("MASTER heartbeat %s %s (porta console %u)", dir, EndpointText(remote, remotePort).c_str(), localPort);
            return;
        }
        if (type == 0x08)
        {
            g_netStats.qr2Keepalives++;
            return;
        }
        const char* name = Qr2Name(type, outgoing);
        if (name) AddEvent("MASTER %s %s %s (porta console %u, %d byte)", name, dir, EndpointText(remote, remotePort).c_str(), localPort, plen);
        else AddEvent("MASTER tipo 0x%02X %s %s (porta console %u, %d byte)", type, dir, EndpointText(remote, remotePort).c_str(), localPort, plen);
        // The connection request and its acknowledgement are what a failed battle
        // or trade hinges on; keep their bytes.
        if (type == 0x06 || type == 0x07 || !name) AddDump(p, plen);
        return;
    }

    if (firstInDirection && !IsWellKnownPort(remotePort))
        AddEvent("primo pacchetto UDP %s %s (porta console %u, %d byte)", dir,
                 EndpointText(remote, remotePort).c_str(), localPort, plen);
}

void RecordGpPayload(const u8* p, int plen, bool outgoing)
{
    // GameSpy presence uses \key\value text; \bm\<n> marks a message between friends,
    // which is how one console asks another to connect. Only the type is logged.
    for (int i = 0; i + 4 < plen; i++)
    {
        if (std::memcmp(p + i, "\\bm\\", 4) != 0) continue;
        int type = 0, j = i + 4;
        while (j < plen && p[j] >= '0' && p[j] <= '9') type = type * 10 + (p[j++] - '0');
        AddEvent("AMICI messaggio tipo %d %s", type, outgoing ? "inviato" : "ricevuto");
        // Type 1 carries the game's own matchmaking commands; status updates (100) are skipped.
        if (type != 100)
        {
            int end = j;
            while (end < plen && end - i < 400 && !(p[end] == '\\' && end + 6 < plen && std::memcmp(p + end, "\\final\\", 7) == 0)) end++;
            AddDump(p + i, end - i, 256);
        }
    }
}

void RecordFrame(const u8* frame, int len, bool outgoing)
{
    if (len < 34 || Be16(frame + 12) != 0x0800) return;
    const u8* ip = frame + 14;
    const int ihl = (ip[0] & 0x0F) * 4;
    if (len < 14 + ihl + 4) return;
    const uint8_t proto = ip[9];
    const uint32_t src = Be32(ip + 12), dst = Be32(ip + 16);
    const u8* l4 = ip + ihl;

    std::lock_guard lock(g_netStats.mutex);
    if (outgoing && src != 0) g_netStats.consoleIp = src;
    // In Npcap mode the console also sees other machines' broadcast traffic; skip it.
    if (!outgoing && g_netStats.consoleIp && dst != g_netStats.consoleIp) return;

    if (proto == 17 || proto == 6)
    {
        const uint32_t remote = outgoing ? dst : src;
        const uint16_t port = outgoing ? Be16(l4 + 2) : Be16(l4);
        auto& e = g_netStats.endpoints[{remote, port}];
        const auto now = std::chrono::steady_clock::now();
        if (e.first == std::chrono::steady_clock::time_point{}) e.first = now;
        e.last = now;

        const int ipTotal = Be16(ip + 2);
        const int l4Len = std::min(ipTotal, len - 14) - ihl;
        if (proto == 17 && l4Len >= 8)
        {
            const bool first = outgoing ? e.udpOut == 0 : e.udpIn == 0;
            const uint16_t localPort = outgoing ? Be16(l4) : Be16(l4 + 2);
            RecordUdpPayload(remote, port, localPort, l4 + 8, std::min<int>(Be16(l4 + 4), l4Len) - 8, outgoing, first);
        }
        else if (proto == 6 && port == 29900 && l4Len >= 20)
        {
            const int dataOffset = (l4[12] >> 4) * 4;
            if (l4Len > dataOffset) RecordGpPayload(l4 + dataOffset, l4Len - dataOffset, outgoing);
        }

        if (proto == 17) (outgoing ? e.udpOut : e.udpIn)++;
        else (outgoing ? e.tcpOut : e.tcpIn)++;
    }
    else if (proto == 1 && !outgoing)
    {
        g_netStats.icmpToConsole++;
        if (g_netStats.icmpLog.size() < 4000 && len >= 14 + ihl + 8 + 20)
        {
            // The ICMP payload quotes the console's original IP header + 8 bytes of UDP.
            const u8* quoted = l4 + 8;
            const int qihl = (quoted[0] & 0x0F) * 4;
            const bool hasPort = len >= 14 + ihl + 8 + qihl + 4;
            const double t = std::chrono::duration<double>(std::chrono::steady_clock::now() - g_netStats.start).count();
            char line[200];
            snprintf(line, sizeof(line), "  %7.1fs ICMP tipo %u codice %u da %s per il pacchetto verso %s:%u\n",
                     t, l4[0], l4[1], IpText(src).c_str(), IpText(Be32(quoted + 16)).c_str(),
                     hasPort ? Be16(quoted + qihl + 2) : 0);
            g_netStats.icmpLog += line;
        }
    }
}

const char* PortLabel(uint16_t port)
{
    switch (port)
    {
    case 53: return "DNS";
    case 80: return "HTTP";
    case 443: return "HTTPS";
    case 27900: return "GameSpy master";
    case 27901: return "NAT negotiation";
    case 28910: return "GameSpy server browser";
    case 29900: return "GameSpy profile";
    case 29901: return "GameSpy search";
    default: return "";
    }
}

std::string BuildNetReport()
{
    std::string out;
    {
        std::lock_guard lock(g_core.netDescriptionMutex);
        out += "Connessione: " + g_core.netDescription + "\n";
    }
    std::lock_guard lock(g_netStats.mutex);
    out += "Indirizzo della console: " + (g_netStats.consoleIp ? IpText(g_netStats.consoleIp) : std::string("-")) + "\n";
    const double total = std::chrono::duration<double>(std::chrono::steady_clock::now() - g_netStats.start).count();
    char line[256];
    snprintf(line, sizeof(line), "Durata sessione: %.0fs, errori ICMP ricevuti dalla console: %llu\n\n",
             total, static_cast<unsigned long long>(g_netStats.icmpToConsole));
    out += line;
    out += "Remoto            Porta   UDP inv/ric    TCP inv/ric    attivo (s)       Servizio\n";
    for (const auto& [key, e] : g_netStats.endpoints)
    {
        const double from = std::chrono::duration<double>(e.first - g_netStats.start).count();
        const double to = std::chrono::duration<double>(e.last - g_netStats.start).count();
        snprintf(line, sizeof(line), "%-16s %6u   %5llu/%-6llu   %5llu/%-6llu   %6.1f-%-8.1f %s\n",
                 IpText(key.first).c_str(), key.second,
                 static_cast<unsigned long long>(e.udpOut), static_cast<unsigned long long>(e.udpIn),
                 static_cast<unsigned long long>(e.tcpOut), static_cast<unsigned long long>(e.tcpIn),
                 from, to, PortLabel(key.second));
        out += line;
    }
    if (!g_netStats.icmpLog.empty()) out += "\nErrori ICMP:\n" + g_netStats.icmpLog;
    snprintf(line, sizeof(line), "\nCronologia WFC (heartbeat al master: %llu, keepalive: %llu):\n",
             static_cast<unsigned long long>(g_netStats.qr2Heartbeats), static_cast<unsigned long long>(g_netStats.qr2Keepalives));
    out += line;
    out += g_netStats.timeline.empty() ? "  (nessun evento)\n" : g_netStats.timeline;
    return out;
}

void ResetNetStats()
{
    std::lock_guard lock(g_netStats.mutex);
    g_netStats.consoleIp = 0;
    g_netStats.endpoints.clear();
    g_netStats.icmpToConsole = 0;
    g_netStats.icmpLog.clear();
    g_netStats.timeline.clear();
    g_netStats.timelineLines = 0;
    g_netStats.qr2Heartbeats = g_netStats.qr2Keepalives = 0;
    g_netStats.start = std::chrono::steady_clock::now();
}

}

namespace ndscore_hooks
{
void OnSaveWritten() { g_core.saveCounter++; }
void OnFirmwareWritten() { g_core.firmwareCounter++; }
void OnStopSignal() { g_core.stopRequested = true; }

int NetSend(u8* data, int len)
{
    if (!g_core.net) return 0;
    RecordFrame(data, len, true);
    return g_core.net->SendPacket(data, len, 0);
}

int NetRecv(u8* data)
{
    if (!g_core.net) return 0;
    int len = g_core.net->RecvPacket(data, 0);
    if (len > 0) RecordFrame(data, len, false);
    return len;
}
}

// --- Public API ----------------------------------------------------------------

NDS_API int32_t ndscore_api_version(void) { return NDSCORE_API_VERSION; }

NDS_API int32_t ndscore_init(void)
{
    if (g_core.initialized) return 1;

    SDL_SetMainReady();
    SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
    if (SDL_Init(SDL_INIT_AUDIO | SDL_INIT_GAMECONTROLLER) != 0)
    {
        SetError(std::string("SDL_Init failed: ") + SDL_GetError());
        return 0;
    }

    OpenAudio();
    ConfigureNetwork(NDSCORE_NETPREF_AUTO);

    g_core.padThreadRun = true;
    g_core.padThread = std::thread(PadThreadMain);
    g_core.initialized = true;
    return 1;
}

NDS_API void ndscore_shutdown(void)
{
    if (!g_core.initialized) return;
    ndscore_unload();

    g_core.padThreadRun = false;
    if (g_core.padThread.joinable()) g_core.padThread.join();

    if (g_core.audioDevice)
    {
        SDL_CloseAudioDevice(g_core.audioDevice);
        g_core.audioDevice = 0;
    }
    g_core.net.reset();
    SDL_Quit();
    g_core.initialized = false;
}

NDS_API int32_t ndscore_boot(const ndscore_boot_args* args)
{
    if (!g_core.initialized && !ndscore_init()) return 0;
    if (!args || !args->rom || args->rom_length <= 0)
    {
        SetError("No ROM data.");
        return 0;
    }

    ndscore_unload();

    NDSArgs ndsArgs;
    ndsArgs.OutputSampleRate = g_core.audioFrequency;

    // Reuse the console's own firmware when the frontend kept one, so the WFC user ID
    // and profile survive between sessions. Generated firmware is 128 KiB.
    if (args->firmware && args->firmware_length >= 0x20000)
    {
        Firmware saved(args->firmware, static_cast<u32>(args->firmware_length));
        if (saved.Buffer() && saved.GetEffectiveUserData().ChecksumValid())
            ndsArgs.Firmware = std::move(saved);
    }
    if (args->mac[0] | args->mac[1] | args->mac[2] | args->mac[3] | args->mac[4] | args->mac[5])
        std::copy(std::begin(args->mac), std::end(args->mac), ndsArgs.Firmware.GetHeader().MacAddr.begin());
    ApplyFirmwareSettings(ndsArgs.Firmware, *args);

    NDSCart::NDSCartArgs cartArgs;
    if (args->save && args->save_length > 0)
    {
        cartArgs.SRAMLength = static_cast<u32>(args->save_length);
        cartArgs.SRAM = std::make_unique<u8[]>(cartArgs.SRAMLength);
        std::memcpy(cartArgs.SRAM.get(), args->save, cartArgs.SRAMLength);
    }

    auto cart = NDSCart::ParseROM(args->rom, static_cast<u32>(args->rom_length), nullptr, std::move(cartArgs));
    if (!cart)
    {
        SetError("The ROM is not a valid Nintendo DS cartridge image.");
        return 0;
    }

    auto nds = std::make_unique<NDS>(std::move(ndsArgs));
    nds->SetNDSCart(std::move(cart));
    nds->Reset();
    nds->SetupDirectBoot(args->save_name ? args->save_name : "game");
    nds->Start();

    {
        std::lock_guard emuLock(g_core.emuMutex);
        std::lock_guard audioLock(g_core.audioMutex);
        g_core.nds = std::move(nds);
    }
    {
        std::lock_guard lock(g_core.frameMutex);
        std::memset(g_core.frame, 0, sizeof(g_core.frame));
        g_core.frameId = 0;
    }

    g_core.saveCounter = 0;
    g_core.firmwareCounter = 0;
    g_core.fps = 0.0f;
    ResetNetStats();
    g_core.paused = false;
    g_core.stopRequested = false;
    g_core.running = true;
    g_core.emuThread = std::thread(EmuThreadMain);
    return 1;
}

NDS_API void ndscore_stop(void)
{
    JoinEmuThread();
    std::lock_guard lock(g_core.emuMutex);
    if (g_core.nds) g_core.nds->Stop();
}

NDS_API void ndscore_unload(void)
{
    ndscore_stop();
    std::lock_guard emuLock(g_core.emuMutex);
    std::lock_guard audioLock(g_core.audioMutex);
    g_core.nds.reset();
}

NDS_API int32_t ndscore_is_running(void) { return g_core.running ? 1 : 0; }

NDS_API void ndscore_set_paused(int32_t paused)
{
    {
        std::lock_guard lock(g_core.pauseMutex);
        g_core.paused = paused != 0;
    }
    g_core.pauseCv.notify_all();
}

NDS_API void ndscore_set_speed(float multiplier) { g_core.speed = std::max(0.0f, multiplier); }
NDS_API void ndscore_set_volume(float volume) { g_core.volume = std::clamp(volume, 0.0f, 1.0f); }

NDS_API void ndscore_set_input(uint32_t pressed_mask) { g_core.inputMask = pressed_mask & 0xFFF; }

NDS_API void ndscore_set_touch(int32_t down, int32_t x, int32_t y)
{
    g_core.touchX = std::clamp(x, 0, NDSCORE_SCREEN_WIDTH - 1);
    g_core.touchY = std::clamp(y, 0, NDSCORE_SCREEN_HEIGHT - 1);
    g_core.touchDown = down ? 1 : 0;
}

NDS_API uint32_t ndscore_get_gamepad_buttons(void) { return g_core.padButtons; }

NDS_API int32_t ndscore_get_gamepad_name(char* buffer, int32_t capacity)
{
    std::lock_guard lock(g_core.padNameMutex);
    if (g_core.padName.empty()) return 0;
    if (buffer && capacity > 0)
    {
        const size_t n = std::min<size_t>(g_core.padName.size(), static_cast<size_t>(capacity - 1));
        std::memcpy(buffer, g_core.padName.data(), n);
        buffer[n] = '\0';
    }
    return 1;
}

NDS_API uint64_t ndscore_copy_frame(uint32_t* dst, uint64_t last_frame)
{
    std::lock_guard lock(g_core.frameMutex);
    if (!dst || g_core.frameId == 0 || g_core.frameId == last_frame) return 0;
    std::memcpy(dst, g_core.frame, sizeof(g_core.frame));
    return g_core.frameId;
}

NDS_API float ndscore_get_fps(void) { return g_core.fps; }

NDS_API uint32_t ndscore_get_save_counter(void) { return g_core.saveCounter; }

NDS_API uint32_t ndscore_get_firmware_counter(void) { return g_core.firmwareCounter; }

NDS_API int32_t ndscore_get_firmware(uint8_t* dst, int32_t capacity)
{
    std::lock_guard lock(g_core.emuMutex);
    if (!g_core.nds) return 0;
    const Firmware& firmware = g_core.nds->SPI.GetFirmware();
    const int32_t length = static_cast<int32_t>(firmware.Length());
    if (dst)
    {
        if (capacity < length) return -length;
        std::memcpy(dst, firmware.Buffer(), length);
    }
    return length;
}

NDS_API int32_t ndscore_get_save(uint8_t* dst, int32_t capacity)
{
    std::lock_guard lock(g_core.emuMutex);
    if (!g_core.nds || !g_core.nds->CartInserted()) return 0;
    const u8* save = g_core.nds->GetNDSSave();
    const u32 length = g_core.nds->GetNDSSaveLength();
    if (!save || length == 0) return 0;
    if (dst)
    {
        if (capacity < static_cast<int32_t>(length)) return -static_cast<int32_t>(length);
        std::memcpy(dst, save, length);
    }
    return static_cast<int32_t>(length);
}

NDS_API int32_t ndscore_save_state(uint8_t* dst, int32_t capacity)
{
    std::lock_guard lock(g_core.emuMutex);
    if (!g_core.nds) return 0;
    Savestate state;
    if (state.Error || !g_core.nds->DoSavestate(&state) || state.Error)
    {
        SetError("Could not create the save state.");
        return 0;
    }
    const int32_t length = static_cast<int32_t>(state.Length());
    if (dst)
    {
        if (capacity < length) return -length;
        std::memcpy(dst, state.Buffer(), length);
    }
    return length;
}

NDS_API int32_t ndscore_load_state(const uint8_t* src, int32_t length)
{
    if (!src || length <= 0) return 0;
    std::lock_guard lock(g_core.emuMutex);
    if (!g_core.nds) return 0;

    Savestate backup;
    if (backup.Error || !g_core.nds->DoSavestate(&backup) || backup.Error)
    {
        SetError("Could not back up the current state.");
        return 0;
    }

    std::unique_ptr<u8[]> copy = std::make_unique<u8[]>(length);
    std::memcpy(copy.get(), src, length);
    Savestate state(copy.get(), static_cast<u32>(length), false);
    if (!g_core.nds->DoSavestate(&state) || state.Error)
    {
        Savestate restore(backup.Buffer(), backup.Length(), false);
        g_core.nds->DoSavestate(&restore);
        SetError("The save state is invalid or from another version.");
        return 0;
    }
    return 1;
}

NDS_API int32_t ndscore_get_net_mode(void) { return g_core.netMode; }

NDS_API int32_t ndscore_set_network(int32_t preference)
{
    if (preference < NDSCORE_NETPREF_AUTO || preference > NDSCORE_NETPREF_PCAP) preference = NDSCORE_NETPREF_AUTO;
    if (g_core.running)
    {
        SetError("The network can only be changed while no game is running.");
        return -1;
    }
    ConfigureNetwork(preference);
    return g_core.netMode;
}

NDS_API int32_t ndscore_get_net_report(char* buffer, int32_t capacity)
{
    const std::string report = BuildNetReport();
    if (!buffer || capacity <= 0) return static_cast<int32_t>(report.size()) + 1;
    const size_t n = std::min<size_t>(report.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(buffer, report.data(), n);
    buffer[n] = 0;
    return static_cast<int32_t>(n);
}

NDS_API int32_t ndscore_get_net_description(char* buffer, int32_t capacity)
{
    std::lock_guard lock(g_core.netDescriptionMutex);
    if (!buffer || capacity <= 0) return static_cast<int32_t>(g_core.netDescription.size()) + 1;
    const size_t n = std::min<size_t>(g_core.netDescription.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(buffer, g_core.netDescription.data(), n);
    buffer[n] = '\0';
    return static_cast<int32_t>(n);
}

NDS_API const char* ndscore_last_error(void)
{
    static thread_local std::string copy;
    std::lock_guard lock(g_core.errorMutex);
    copy = g_core.lastError;
    return copy.c_str();
}
