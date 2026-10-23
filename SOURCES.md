# Credits and sources

This console stands on other people's protocol research and documentation. Thank you to everyone below.
All console code in this repository is original. Facts, codes and timings were learned from these sources and
re-implemented; no code, tables or binaries from them were copied into this project. Each source keeps its own
copyright and license.

## Open-source projects and research

| Source | Author | What it taught us |
|---|---|---|
| [RadioConsole2](https://github.com/W3AXL/RadioConsole2) (`daemon/SB9600.cs`) and its [SB9600 configuration docs](https://rc2.readthedocs.io/en/latest/config/sb9600-config.html) | W3AXL | W-series control-head button and indicator codes, BUTCTL toggle value, RADKEY / SQLDET / ACTMDU / PLDECT handling, SBEP framing, display and indicator message layouts, DTR/CTS BUSY handling with echo check, the BUSY self-test and the reset frame. RadioConsole2 is the open-source dispatch console that showed this was possible. |
| [XTL-SB9600-Playground](https://github.com/W3AXL/XTL-SB9600-Playground) | W3AXL | SB9600 CRC parameters and test vectors, RIB-to-XTL wiring, O5 head codes, "go to mode" command, listener examples. |
| [SB9600 / GM1200 control library](https://paulbanks.org/projects/sb9600/) | Paul Banks | The original open SB9600 / SBEP research that the XTL-SB9600-Playground support libraries build on. |

## Manuals and documents

| Document | Used for |
|---|---|
| Motorola XTL 5000 Consolette (W7/W9) Instruction Manual, 6881098C21-P | TRC tone sequence (HLGT 120 ms, FT 40 ms, LLGT -30 dB), standard and No Mode Select tone tables, TRC/AIB DIP switches, Accessory 2/3 pinouts, level procedures, codeplug notes, Control Point Monitor (3.4.2.1), TRC transmit audio path (3-9), Enhanced Digital ID Display (2.13.3). |
| [Motorola SB9600 Serial Bus Protocol Manual v05.13](https://wiki.w9cr.net/images/6/61/SB9600_Serial_Bus_Protocol_Manual_by_Motorola.pdf) (public copy hosted on the W9CR wiki) | Message format, 9600 8N1, BUSY arbitration rules. |
| [ComTekk Tone Remote Console manuals](https://comtekk.com/manuals/Tone-Remote-Console/settings.htm) | Feature and settings ideas for a PC tone-remote console (no code). |
| ASRock P43DE specifications and manual; StarTech PLATE9M16 pin sheet | Motherboard COM1 header to DB9 wiring for the RIB port (`HARDWARE_SETUP.md`). |

## This project

- Bench logs from the development XTL5000 consolette (W7 head, PLN1687 TRC, RIB), October 2026: W7 key and indicator
  codes, SBEP display behaviour and channel-select behaviour, used by the self-test.
- The example codeplug in `cps/` is fictional; it keeps only the shape (zones, personalities, scan list) of a real
  export.
- No proprietary binaries were copied or executed while building this console.

## Trademarks

Motorola, XTL, ASTRO and MCC are trademarks of Motorola Trademark Holdings, LLC. ComTekk, ASRock and StarTech are
trademarks of their owners. They are used only to identify compatible hardware and sources. This project is not
affiliated with or endorsed by any of them.

If you are the author of a source above and would like the credit worded differently, please open an issue.
