using System;

namespace Tedd.Commodore.Emulator.Enums.Memory;

public enum Basic : UInt16
{
    // https://www.c64-wiki.com/wiki/BASIC-ROM

    COLDSTART = 0xA000, // Basic cold start vector (0xE394)
    WARMSTART = 0xA002, // Basic warm start vector (0xE37B)
    CBMBASIC = 0xA004, // Text "cbmbasic"
                       //0xA00C, // Addresses of the BASIC-commands -1 (END, FOR, NEXT, ... 35 addresses of 2 byte each)
                       //0xA052, // Addresses of the BASIC-Functions (SGN, INT, ABS, ... 23 addresses of 2 byte each)
                       //0xA080, // Hierarchy-codes and addresses of the operators -1 (10-times 1+2 Bytes)
                       //0xA09E, // BASIC key words as string in PETSCII; Bit 7 of the last character is set
                       //0xA129, // Keywords which have no action addresses - TAB(, TO, SPC(, ...; Bit 7 of the last character is set
                       //0xA140, // Keywords of the operands + - * etc.; also AND, OR as strings. Bit 7 of the last character is set
                       //0xA14D, // Keywords of the functions (SGN, INT, ABS, etc.) where bit 7 of the last character is set
                       //0xA19E, // Error messages (TOO MANY FILES, FILE OPEN, ... ); 29 messages where bit 7 of the last character is set
                       //0xA328, // Pointer-table to the error messages
                       //0xA364, // Messages of the interpreter (OK, ERROR IN, READY, BREAK)
                       //0xA38A, // Routine to search stack for FOR-NEXT and GOSUB
                       //0xA3B8, // Called at Basic line insertion. Checks, if enough space available. After completion, 0xA3BF is executed
                       //0xA3BF, // Move bytes routine
                       //0xA3FB, // Check for space on stack
                       //0xA408, // Array area overflow check
                       //0xA435, // Output of error message ?OUT OF MEMORY
                       //0xA437, // Output of an error message, error number in X-register; uses vector in (0x0300) to jump to 0xE38B
                       //0xA480, // Input waiting loop; uses vector in (0x0302) to jump to basic warm start at 0xA483
                       //0xA49C, // Delete or Insert program lines and tokenize them
                       //0xA533, // Relink BASIC program
                       //0xA560, // Input of a line via keyboard
                       //0xA579, // Token crunch -> text line to interpreter code; uses vector in (0x0304) to get to 0xA57C
                       //0xA613, // Calculate start adress of a program line
                       //0xA642, // BASIC-command NEW
                       //0xA65E, // BASIC-command CLR
                       //0xA68E, // Set program pointer to BASIC-start (loads 0x7A, 0x7B with 0x2B-1, 0x2C-1)
                       //0xA69C, // BASIC-command LIST
                       //0xA717, // Convert BASIC code to clear text; uses vector (0306) to jump to 0xA71A
                       //0xA742, // BASIC-command FOR: Move 18 bytes to the stack 1) Pointer to the next instruction, 2) actual line number, 3) upper loop value, 4) step with (default value = 1), 5) name of the lop variable and 6) FOR-token.
                       //0xA7AE, // Interpreter loop, set up next statement for execution
                       //0xA7C4, // Check for program end
                       //0xA7E1, // execute BASIC-command; uses vector (0x0308) to point to 0xA7E4
                       //0xA7ED, // Executes BASIC keyword
                       //0xA81D, // BASIC-command RESTORE: set data pointer at 0x41, 0x42 to the beginning of the actual basic text
                       //0xA82C, // BASIC-command STOP (also END and program interuption)
                       //0xA82F, // BASIC-command STOP
                       //0xA831, // BASIC-command END
                       //0xA857, // BASIC-command CONT
                       //0xA871, // BASIC-command RUN
                       //0xA883, // BASIC-command GOSUB: Move 5 bytes to the stack. 1) the pointer within CHRGET, 2) the actual line number, 3) token of GOSUB; after that, GOTO (0xa8a0) will be called
                       //0xA8A0, // BASIC-command GOTO
                       //0xA8D2, // BASIC-command RETURN
                       //0xA8F8, // BASIC-command DATA
                       //0xA906, // Find offset of the next seperator
                       //0xA928, // BASIC-command IF
                       //0xA93B, // BASIC-command REM
                       //0xA94B, // BASIC-command ON
                       //0xA96B, // Get decimal number (0...63999, usually a line number) from basic text into 0x14/$15
                       //0xA9A5, // BASIC-command LET
                       //0xA9C4, // Value assignment of integer
                       //0xA9D6, // Value assignment of float
                       //0xA9D9, // Value assignment of string
                       //0xA9E3, // Assigns system variable TI$
                       //0xAA1D, // Check for digit in string, if so, continue with 0xAA27
                       //0xAA27, // Add PETSCII digit in string to float accumulator
                       //0xAA2C, // Value assignment to string variable (LET for strings)
                       //0xAA80, // BASIC-command PRINT#
                       //0xAA86, // BASIC-command CMD
                       //0xAA9A, // Part of the PRINT-routine: Output string and continue with the handling of PRINT
                       //0xAAA0, // BASIC-command PRINT
                       //0xAAB8, // Outputs variable; Numbers will be converted into string first
                       //0xAACA, // Append 0x00 as end indicator of string
                       //0xAAD7, // Outputs a CR/soft hyphenation (#0x0D), followed by a line feed/newline (#0x0A), if the channel number is 128
                       //0xAAF8, // TAB( (C = 1) and SPC( (C = 0)
                       //0xAB1E, // Output string: Output string, which is indicated by accu/Y reg, until 0 byte or quote is found
                       //0xAB3B, // Output of cursor right (or space if Output not on screen)
                       //0xAB3F, // Output of a space character
                       //0xAB42, // Output of cursor right
                       //0xAB45, // Output of question mark (before error message)
                       //0xAB47, // Output of a PETSCII character, accu must contain PETSCII value
                       //0xAB4D, // Output error messages dor read commands (INPUT / GET / READ)
                       //0xAB7B, // BASIC-command GET
                       //0xABA5, // BASIC-command INPUT#
                       //0xABBF, // BASIC-command INPUT
                       //0xABEA, // Get line into buffer
                       //0xABF9, // Display input prompt
                       //0xAC06, // BASIC-commands READ, GET and INPUT share this routine and will be distinguished by a flag in $11
                       //0xAC35, // Input routine for GET
                       //0xACFC, // Messages ?EXTRA IGNORED and ?REDO FROM START, both followed by 0x0D (CR) and end of string 0x00.
                       //0xAD1D, // BASIC-command NEXT
                       //0xAD61, // Check for valid loop
                       //0xAD8A, // FRMNUM: Get expression (FRMEVL) and check, if numeric
                       //0xAD9E, // FRMEVL: Analyzes any Basic formula expression and shows syntax errors. Set type flag 0x0D (Number: 0x00, string 0xFF). Sety integer flag 0x0E (float: 0x00, integer: 0x80) puts values in FAC.
                       //0xAE83, // EVAL: Get next element of an expression; uses vector (0x030A) to jump to 0xAE86
                       //0xAEA8, // Value for constant PI in 5 bytes float format
                       //0xAEF1, // Evaluates expression within brackets
                       //0xAEF7, // Check for closed bracket ")"
                       //0xAEFA, // Check for open bracket "("
                       //0xAEFD, // Check for comma
                       //0xAF08, // Outputs error message ?SYNTAX ERROR and returns to READY state
                       //0xAF0D, // Calculates NOT
                       //0xAF14, // Check for reserved variables. Set carry flag, if FAC points to ROM. This indicates the use of one of the reserved variables TI0x, TI, ST.
                       //0xAF28, // Get variable: Searches the variable list for one of the variables named in 0x45, $46
                       //0xAF48, // Reads clock counter and generate string, which contains TI$
                       //0xAFA7, // Calculate function: Determine type of function and evaluates it
                       //0xAFB1, // String function: check for open bracket, get expression (FRMEVL), checks for comms, get string.
                       //0xAFD1, // Analyze numeric function
                       //0xAFE6, // BASIC-commands OR and AND, distinguished by flag 0x0B (= 0xFF at OR, 0x00 at AND).
                       //0xB016, // Comparison (<, =, > )
                       //0xB01B, // Numeric comparison
                       //0xB02E, // String comparison
                       //0xB081, // BASIC-command DIM
                       //0xB08B, // Checks, if variable name valid
                       //0xB0E7, // Searches variable in list, set variable pointer, create new variable, if name not found
                       //0xB113, // Check for character
                       //0xB11D, // Create variable
                       //0xB194, // Calculate pointer to first element of array
                       //0xB1A5, // Constant -32768 as float (5 bytes)
                       //0xB1AA, // Convert FAC to integer and save it to accu/Y reg
                       //0xB1B2, // Get positive integer from BASIC text
                       //0xB1BF, // Convert FAC to integer
                       //0xB1D1, // Get array variable from BASIC text
                       //0xB218, // Search for array name in pointer(0x45, 0x46)
                       //0xB245, // Output error message? BAD SUBSCRIPT
                       //0xB248, // Output error message? ILLEGAL QUANTITY
                       //0xB24D, // Output error message? REDIM\'D ARRAY
                       //0xB261, // Create array variable
                       //0xB30E, // Calculate address of a array element, set pointer(0x47)
                       //0xB34C, // Calculate distance of given array element to the one which(0x47) points to and write the result to X reg/Y reg
                       //0xB37D, // BASIC-function FRE
                       //0xB391, // Convert 16-bit integer in accu/Y reg to float
                       //0xB39E, // BASIC-function POS
                       //0xB3A2, // Convert the byte in Y reg to float and return it to FAC
                       //0xB3A6, // Check for direct mode: value 0xFF in flag 0x3A indicates direct mode
                       //0xB3AE, // Output error message? UNDEF\'D FUNCTION
                       //0xB3B3, // BASIC-command DEF FN
                       //0xB3E1, // Check syntax of FN
                       //0xB3F4, // BASIC-function FN
                       //0xB465, // BASIC-function STR$
                       //0xB475, // Make space for inserting into string
                       //0xB487, // Get string, pointer in accu/Y reg
                       //0xB4CA, // Store string pointer in descriptor stack
                       //0xB4F4, // Reserve space for string, length in accu
                       //0xB526, // Garbage Collection
                       //0xB606, // Searches n variables and arrays for string which has to be saved by the next Garbace Collection
                       //0xB63D, // Concatenates two strings
                       //0xB67A, // Move String to reserved area
                       //0xB6A3, // String management FRESTR
                       //0xB6DB, // Remove string pointer from descriptor stack
                       //0xB6EC, // BASIC-function CHR$
                       //0xB700, // BASIC-function LEFT$
                       //0xB72C, // BASIC-function RIGHT$
                       //0xB737, // BASIC-function MID$
                       //0xB761, // String parameter from stack: Get pointer for string descriptor and write it to 0x50, 0x51 and the length to accu (also X-reg)
                       //0xB77C, // BASIC-function LEN
                       //0xB782, // Get string parameter (length in Y-reg), switch to numeric
                       //0xB78B, // BASIC-function ASC
                       //0xB79B, // Holt Byte-Wert nach X: Read and evaluate expression from BASIC text; the 1 byte value is then stored in X-reg and in FAC+4
                       //0xB7AD, // BASIC-funktion VAL
                       //0xB7EB, // GETADR and GETBYT: Get 16-bit integer (to 0x14, 0x15) and an 8 bit value (to X-reg) - e.G. parameter for WAIT and POKE.
                       //0xB7F7, // Converts FAC in 2-byte integer (scope 0 ... 65535) to 0x14, 0x15 and Y-Reg/accu
                       //0xB80D, // BASIC-function PEEK
                       //0xB824, // BASIC-command POKE
                       //0xB82D, // BASIC-command WAIT
                       //0xB849, // FAC = FAC + 0,5; for rounding
                       //0xB850, // FAC = CONSTANT - FAC , accu and Y-register are pointing to CONSTANT (low- and high-byte)
                       //0xB853, // FAC = ARG - FAC
                       //0xB862, // Align exponent of FAC and ARG for addition
                       //0xB867, // FAC = CONSTANT (accu/Y reg) + FAC
                       //0xB86A, // FAC = ARG + FAC
                       //0xB947, // Invert mantissa of FAC
                       //0xB97E, // Output error message OVERFLOW
                       //0xB983, // Multiplies with one byte
                       //0xB9BC, // Constant 1.00 (table of constants in Mfltp-format for LOG)
                       //0xB9C1, // Constant 03 (grade of polynome, then 4th coefficient)
                       //0xB9C2, // Constant 0.434255942 (1st coefficient)
                       //0xB9C7, // Constant 0.576584541 (2nd coefficient)
                       //0xB9CC, // Constant 0.961800759 (3rd coefficient)
                       //0xB9D1, // Constant 2.885390073 (4th coefficient)
                       //0xB9D6, // Constant 0.707106781 = 1/SQR(2)
                       //0xB9DB, // Constant 1.41421356 = SQR(2)
                       //0xB9E0, // Constant -0.5
                       //0xB9E5, // Constant 0.693147181 = LOG(2)
                       //0xB9EA, // BASIC-function LOG
                       //0xBA28, // FAC = constant (accu/Y reg) * FAC
                       //0xBA30, // FAC = ARG * FAC
                       //0xBA59, // Multiplies FAC with one byte and stores result to 0x26 .. $2A
                       //0xBA8C, // ARG = constant (accu/Y reg)
                       //0xBAB7, // Checks FAC and ARG
                       //0xBAE2, // FAC = FAC * 10
                       //0xBAF9, // Constant 10 in Flpt
                       //0xBAFE, // FAC = FAC / 10
                       //0xBB0F, // FAC = constant (accu/Y reg) / FAC
                       //0xBB14, // FAC = ARG / FAC
                       //0xBB8A, // Output error message ?DIVISION BY ZERO
                       //0xBBA2, // Transfer constant (accu/Y reg) to FAC
                       //0xBBC7, // FAC to accu #4 (0x5C to 0x60)
                       //0xBBCA, // FAC to accu #3 (0x57 to 0x5B)
                       //0xBBD0, // FAC to variable (the adress, where 0x49 points to)
                       //0xBBFC, // ARG to FAC
                       //0xBC0C, // FAC (rounded) to ARG
                       //0xBC1B, // Rounds FAC
                       //0xBC2B, // Get sign of FAC: A=0 if FAC=0, A=1 if FAC positive, A=0xFF if FAC negative
                       //0xBC39, // BASIC-function SGN
                       //0xBC58, // BASIC-function ABS
                       //0xBC5B, // Compare constant (accu/Y reg) with FAC: A=0 if equal, A=1 if FAC greater, A=0xFF if FAC smaller
                       //0xBC9B, // FAC to integer: converts FAC to 4-byte integer
                       //0xBCCC, // BASIC-function INT
                       //0xBCF3, // Conversion PETSCII-string to floating-point format
                       //0xBDB3, // Constant 9999999.9 (3 constants for float PETSCII conversion)
                       //0xBDB8, // Constant 99999999
                       //0xBDBD, // Constant 1000000000
                       //0xBDC2, // Output of "IN" and line number (from CURLIN 0x39, 0x3A)
                       //0xBDCD, // Output positive integer number in accu/X reg
                       //0xBDDD, // Convert FAC to PETSCII string which starts with 0x0100 and ends with 0x00. Start address in accu/Y reg.
                       //0xBE68, // TI to string: convert TI to PETSCII string which starts with 0x0100 and ends with $00
                       //0xBF11, // Constant 0.5
                       //0xBF16, // Constant tables for integer PETSCII conversion
                       //0xBF3A, // Constant tables to convert TI to TI$
                       //0xBF71, // BASIC-function SQR
                       //0xBF78, // Power function FAC = ARG to the power of constant (accu/Y reg)
                       //0xBF7B, // Power function FAC = ARG to the power of FAC
                       //0xBFB4, // Makes FAC negative
                       //0xBFBF, // Constant 1.44269504 = 1/LOG(2) (table of 8 constants to evaluate EXP - polynomal table)
                       //0xBFC4, // Constant 07: 7 = Grade of polynome (followed by 8 coefficient constants)
                       //0xBFC5, // Constant 2.149875 E-5
                       //0xBFCA, // Constant 1.435231 E-4
                       //0xBFCF, // Constant 1.342263 E-3
                       //0xBFD4, // Constant 9.641017 E-3
                       //0xBFD9, // Constant 5.550513 E-2
                       //0xBFDE, // Constant 2.402263 E-4
                       //0xBFE3, // Constant 6.931471 E-1
                       //0xBFE8, // Constant 1.00
                       //0xBFED, // BASIC-function EXP
}
