# Test data

## 6502_functional_test.bin

Klaus Dormann's 6502 functional test suite, binary build of `6502_functional_test.a65` with the default
configuration (`ROM_vectors = 1`, `load_data_direct = 1`, `I_flag = 3`, `zero_page = $0A`,
`data_segment = $0200`, `code_segment = $0400`, `report = 0`, `disable_decimal = 0`).

* Author: Klaus Dormann
* Source: https://github.com/Klaus2m5/6502_65C02_functional_tests
* License: GPL-3.0 (the binary is redistributed here unchanged, for testing only)

The file is a complete 65536-byte memory image. It is loaded at `$0000` and execution starts at
`PC = $0400` (with the CPU in its power-on state; the test initialises everything else itself, including
the interrupt vectors at `$FFFA-$FFFF` which the image contains).

The test reports results only through the program counter:

* **Success**: the program reaches the final `jmp *` at **`$3469`** (label `success` in the listing) and
  stays there forever.
* **Failure**: the program loops on itself (`jmp *` or a branch to itself) at any other address. The
  trapped address identifies the failing test; look it up in `6502_functional_test.lst` from the
  repository above to see which instruction/flag combination was being checked.

`Cpu/FunctionalTests.cs` runs the image until the PC repeats itself and asserts that this happens at
`$3469`. The run takes roughly 100 million cycles.
