using System;

namespace Tedd.Commodore.Emulator.Enums.Memory;

public enum Kernal : UInt16
{
    // https://www.c64-wiki.com/wiki/Kernal
    ACPTR = 0xFFA5, // Input byte from serial port
    CHKIN = 0xFFC6, // Open channel for input
    CHKOUT = 0xFFC9, // Open a channel for output
    CHRIN = 0xFFCF, // Get a character from the input channel
    CHROUT = 0xFFD2, // Output a character
    CIOUT = 0xFFA8, // Transmit a byte over the serial bus
    CINT = 0xFF81, // Initialize the screen editor and VIC-II Chip
    CLALL = 0xFFE7, // Close all open files
    CLOSE = 0xFFC3, // Close a logical file
    CLRCHN = 0xFFCC, // Clear all I/O channels
    GETIN = 0xFFE4, // Get a character
    IOBASE = 0xFFF3, // Define I/O memory page
    IOINIT = 0xFF84, // Initialize I/O devices
    LISTEN = 0xFFB1, // Command a device on the serial bus to listen
    LOAD = 0xFFD5, // Load RAM from device
    MEMBOT = 0xFF9C, // Set bottom of memory
    MEMTOP = 0xFF99, // Set the top of RAM
    OPEN = 0xFFC0, // Open a logical file
    PLOT = 0xFFF0, // Set or retrieve cursor location
    RAMTAS = 0xFF87, // Perform RAM test
    RDTIM = 0xFFDE, // Read system clock
    READST = 0xFFB7, // Read status word
    RESTOR = 0xFF8A, // Set the top of RAM
    SAVE = 0xFFD8, // Save memory to a device
    SCNKEY = 0xFF9F, // Scan the keyboard
    SCREEN = 0xFFED, // Return screen format
    SECOND = 0xFF93, // Send secondary address for LISTEN
    SETLFS = 0xFFBA, // Set up a logical file
    SETMSG = 0xFF90, // Set system message output
    SETNAM = 0xFFBD, // Set up file name
    SETTIM = 0xFFDB, // Set the system clock
    SETTMO = 0xFFAE, // Set IEEE bus card timeout flag
    STOP = 0xFFE1, // Check if STOP key is pressed
    TALK = 0xFFB4, // Command a device on the serial bus to talk
    TKSA = 0xFF96, // Send a secondary address to a device commanded to talk
    UDTIM = 0xFFEA, // Update the system clock
    UNLSN = 0xFFAE, // Send an UNLISTEN command
    UNTLK = 0xFFAB, // Send an UNTALK command
    VECTOR = 0xFF8D, // Manage RAM vectors
}
