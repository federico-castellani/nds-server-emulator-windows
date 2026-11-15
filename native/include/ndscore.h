// ndscore: a small C ABI around the melonDS core, consumed by the WinUI frontend via P/Invoke.
// All functions are safe to call from any thread unless stated otherwise.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
#define NDS_EXTERN extern "C"
#else
#define NDS_EXTERN
#endif

#ifdef NDSCORE_BUILD
#define NDS_API NDS_EXTERN __declspec(dllexport)
#else
#define NDS_API NDS_EXTERN __declspec(dllimport)
#endif

#define NDSCORE_API_VERSION 3

#define NDSCORE_SCREEN_WIDTH 256
#define NDSCORE_SCREEN_HEIGHT 192

// DS buttons as bits of the input mask passed to ndscore_set_input (1 = pressed).
enum ndscore_key {
    NDSCORE_KEY_A = 1 << 0,
    NDSCORE_KEY_B = 1 << 1,
    NDSCORE_KEY_SELECT = 1 << 2,
    NDSCORE_KEY_START = 1 << 3,
    NDSCORE_KEY_RIGHT = 1 << 4,
    NDSCORE_KEY_LEFT = 1 << 5,
    NDSCORE_KEY_UP = 1 << 6,
    NDSCORE_KEY_DOWN = 1 << 7,
    NDSCORE_KEY_R = 1 << 8,
    NDSCORE_KEY_L = 1 << 9,
    NDSCORE_KEY_X = 1 << 10,
    NDSCORE_KEY_Y = 1 << 11,
};

enum ndscore_net_mode {
    NDSCORE_NET_NONE = 0,
    NDSCORE_NET_SLIRP = 1,
    NDSCORE_NET_PCAP = 2,
};

// How ndscore_set_network should connect the emulated Wi-Fi.
enum ndscore_net_preference {
    NDSCORE_NETPREF_AUTO = 0,   // Npcap on a wired Ethernet adapter if possible, else Slirp
    NDSCORE_NETPREF_SLIRP = 1,  // always the built-in NAT
    NDSCORE_NETPREF_PCAP = 2,   // Npcap on the best adapter, Wi-Fi included
};

typedef struct ndscore_boot_args {
    const uint8_t* rom;
    int32_t rom_length;
    const uint8_t* save;       // may be null
    int32_t save_length;
    const char* save_name;     // used by direct boot, usually the game id
    const char* nickname;      // UTF-8 firmware nickname (max 10 chars), may be null
    uint8_t primary_dns[4];    // WFC DNS server, 0.0.0.0 keeps the firmware default
    uint8_t secondary_dns[4];
    // Firmware saved from a previous session (see ndscore_get_firmware), or null for a
    // fresh one. It holds the Nintendo WFC user ID the server assigned to this console:
    // losing it makes games see a new console and wipe their friend roster.
    const uint8_t* firmware;
    int32_t firmware_length;
    uint8_t mac[6];            // console MAC address, all zero keeps the firmware's
} ndscore_boot_args;

NDS_API int32_t ndscore_api_version(void);

// Initializes audio, gamepads and networking. Call once at startup.
NDS_API int32_t ndscore_init(void);
NDS_API void ndscore_shutdown(void);

// Creates a console, inserts the cartridge and starts the emulation thread.
// Returns 1 on success, 0 on failure (see ndscore_last_error).
NDS_API int32_t ndscore_boot(const ndscore_boot_args* args);
// Stops the emulation thread. The console stays alive so its save can still be read.
NDS_API void ndscore_stop(void);
// Stops (if needed) and frees the console.
NDS_API void ndscore_unload(void);
NDS_API int32_t ndscore_is_running(void);

NDS_API void ndscore_set_paused(int32_t paused);
// 1.0 = normal speed, >1 = fast forward (audio muted), 0 = unlimited.
NDS_API void ndscore_set_speed(float multiplier);
NDS_API void ndscore_set_volume(float volume); // 0..1

NDS_API void ndscore_set_input(uint32_t pressed_mask);
NDS_API void ndscore_set_touch(int32_t down, int32_t x, int32_t y);

// Raw state of the first connected game controller (polled at ~250 Hz).
// Bits 0..14 follow SDL_GameControllerButton (A, B, X, Y, Back, Guide, Start,
// LStick, RStick, LShoulder, RShoulder, DPadUp, DPadDown, DPadLeft, DPadRight);
// the remaining bits are synthesized from analog inputs.
enum ndscore_pad {
    NDSCORE_PAD_LSTICK_UP = 1 << 16,
    NDSCORE_PAD_LSTICK_DOWN = 1 << 17,
    NDSCORE_PAD_LSTICK_LEFT = 1 << 18,
    NDSCORE_PAD_LSTICK_RIGHT = 1 << 19,
    NDSCORE_PAD_LTRIGGER = 1 << 20,
    NDSCORE_PAD_RTRIGGER = 1 << 21,
};
NDS_API uint32_t ndscore_get_gamepad_buttons(void);
// Writes the controller name (UTF-8) into buffer; returns 0 when no controller is connected.
NDS_API int32_t ndscore_get_gamepad_name(char* buffer, int32_t capacity);

// Copies both screens (top then bottom, 256x384 BGRA, opaque) into dst when a frame
// newer than last_frame exists. Returns the id of the copied frame, or 0 if nothing new.
NDS_API uint64_t ndscore_copy_frame(uint32_t* dst, uint64_t last_frame);
NDS_API float ndscore_get_fps(void);

// Incremented every time the console writes its firmware (Wi-Fi settings, WFC user ID,
// user profile). The frontend should then persist ndscore_get_firmware.
NDS_API uint32_t ndscore_get_firmware_counter(void);
// Copies the current firmware image into dst. Returns its length; pass dst = null to query it.
NDS_API int32_t ndscore_get_firmware(uint8_t* dst, int32_t capacity);

// Incremented every time the game writes to its save memory.
NDS_API uint32_t ndscore_get_save_counter(void);
// Copies the cartridge save into dst. Returns its length; pass dst = null to query it.
NDS_API int32_t ndscore_get_save(uint8_t* dst, int32_t capacity);

// Save states. ndscore_save_state returns the needed length; pass dst = null to query.
NDS_API int32_t ndscore_save_state(uint8_t* dst, int32_t capacity);
NDS_API int32_t ndscore_load_state(const uint8_t* src, int32_t length);

NDS_API int32_t ndscore_get_net_mode(void);
// Applies a network preference; only while no game is running. Returns the resulting
// ndscore_net_mode, or -1 if a game is running.
NDS_API int32_t ndscore_set_network(int32_t preference);
// UTF-8 description of the active connection, e.g. "Npcap su Realtek ... (192.168.1.20)".
NDS_API int32_t ndscore_get_net_description(char* buffer, int32_t capacity);
// Plain-text summary of the console's traffic since the last boot, per remote endpoint
// (UDP/TCP packets each way, ICMP errors). Pass buffer = null to get the needed size.
NDS_API int32_t ndscore_get_net_report(char* buffer, int32_t capacity);
NDS_API const char* ndscore_last_error(void);
