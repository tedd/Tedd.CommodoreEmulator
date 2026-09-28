using System;
namespace Tedd.Commodore.Emulator.Enums.Memory;

public enum Screen : UInt16
{
    SPRITE1 = 0x07F8, // value*64 = start address
    SPRITE2 = 0x07F9, // value*64 = start address
    SPRITE3 = 0x07FA, // value*64 = start address
    SPRITE4 = 0x07FB, // value*64 = start address
    SPRITE5 = 0x07FC, // value*64 = start address
    SPRITE6 = 0x07FD, // value*64 = start address
    SPRITE7 = 0x07FE, // value*64 = start address
    SPRITE8 = 0x07FF, // value*64 = start address

    SCREEN_MEMORY_HIGH_BYTE = 0x0288, // High byte of pointer to screen memory for screen input/output.
    DEFAULT_SCREEN_MEMORY = 0x0400, // $0400-$07FF Default screen memory
    DEFAULT_CHARACTER_MEMORY = 0xD000, // $D000-$D7FF Default character memory
}
