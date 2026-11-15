"""Builds a tiny homebrew NDS ROM for testing the app without real games.

The ARM9 program powers the main 2D engine, maps VRAM bank A as the framebuffer and
fills the top screen with a colour; the ARM7 just idles. The banner carries a
checkerboard icon and a title so the library can show them.

Usage: python make_test_rom.py out.nds [rgb555-colour-hex]
"""
import struct
import sys

ARM9_ADDR = 0x02000000
ARM7_ADDR = 0x02380000
ARM9_OFFSET = 0x8000  # past the secure area (0x4000-0x7FFF), which the core decrypts
ARM7_OFFSET = 0x9000
BANNER_OFFSET = 0xA000


def crc16(data: bytes) -> int:
    crc = 0xFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def arm9_program(colour: int) -> bytes:
    pair = colour | (colour << 16)
    code = [
        0xE59F0038,  # ldr r0, =POWCNT1
        0xE59F1038,  # ldr r1, =0x8003 (LCD on, 2D engine A, A on top)
        0xE5801000,  # str r1, [r0]
        0xE59F0034,  # ldr r0, =DISPCNT_A
        0xE59F1034,  # ldr r1, =0x00020000 (VRAM display mode, bank A)
        0xE5801000,  # str r1, [r0]
        0xE59F0030,  # ldr r0, =VRAMCNT_A
        0xE3A01080,  # mov r1, #0x80 (enable, LCDC)
        0xE5C01000,  # strb r1, [r0]
        0xE59F0028,  # ldr r0, =VRAM_A
        0xE59F1028,  # ldr r1, =colour pair
        0xE3A02A06,  # mov r2, #0x6000 (256*192*2 / 4 words)
        0xE4801004,  # loop: str r1, [r0], #4
        0xE2522001,  # subs r2, r2, #1
        0x1AFFFFFC,  # bne loop
        0xEAFFFFFE,  # b .
    ]
    pool = [0x04000304, 0x00008003, 0x04000000, 0x00020000, 0x04000240, 0x06800000, pair]
    # Literal pool offsets above assume the pool starts right after the code (word 16).
    return b"".join(struct.pack("<I", w) for w in code + pool)


def banner() -> bytes:
    data = bytearray(0x840)
    struct.pack_into("<H", data, 0, 1)  # version
    # Icon: 4x4 tiles of 8x8, 4bpp. Checkerboard of palette entries 1 and 2.
    for tile in range(16):
        tx, ty = tile % 4, tile // 4
        index = 1 if (tx + ty) % 2 == 0 else 2
        for i in range(32):
            data[0x20 + tile * 32 + i] = index | (index << 4)
    palette = [0x0000, 0x7C1F, 0x03FF] + [0] * 13  # transparent, magenta, yellow
    for i, c in enumerate(palette):
        struct.pack_into("<H", data, 0x220 + i * 2, c)
    title = "Test ROM\nNDS Station\nHomebrew".encode("utf-16-le")
    for lang in range(6):
        data[0x240 + lang * 0x100: 0x240 + lang * 0x100 + len(title)] = title
    struct.pack_into("<H", data, 2, crc16(bytes(data[0x20:0x840])))
    return bytes(data)


def build(colour: int) -> bytes:
    arm9 = arm9_program(colour)
    arm7 = struct.pack("<I", 0xEAFFFFFE)  # b .
    rom = bytearray(BANNER_OFFSET + 0x840)
    rom[0:12] = b"NDSSTATION\0\0"
    rom[0x0C:0x10] = b"TEST"
    rom[0x10:0x12] = b"01"
    rom[0x14] = 0x00  # chip size
    struct.pack_into("<IIII", rom, 0x20, ARM9_OFFSET, ARM9_ADDR, ARM9_ADDR, len(arm9))
    struct.pack_into("<IIII", rom, 0x30, ARM7_OFFSET, ARM7_ADDR, ARM7_ADDR, len(arm7))
    struct.pack_into("<I", rom, 0x68, BANNER_OFFSET)
    struct.pack_into("<I", rom, 0x80, len(rom))
    struct.pack_into("<I", rom, 0x84, 0x4000)
    rom[ARM9_OFFSET:ARM9_OFFSET + len(arm9)] = arm9
    rom[ARM7_OFFSET:ARM7_OFFSET + len(arm7)] = arm7
    rom[BANNER_OFFSET:BANNER_OFFSET + 0x840] = banner()
    struct.pack_into("<H", rom, 0x15E, crc16(bytes(rom[0:0x15E])))
    return bytes(rom)


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "test.nds"
    colour = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0x7C1F
    with open(out, "wb") as f:
        f.write(build(colour))
    print(f"wrote {out}")
