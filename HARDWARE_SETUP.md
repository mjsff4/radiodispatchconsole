# Hardware setup: XTL5000 consolette (W7 head + L146 tone remote) with a PC console

This guide collects what the console software assumes about your hardware, with the source of each fact.
"Manual" means *ASTRO Digital XTL 5000 Consolette, Control Heads W7 and W9, Instruction Manual 6881098C21-P*.
Check every pin with a meter before connecting anything. Wrong connections on Accessory 2 can damage the
consolette (manual, Accessory Connector 2 caution).

---------------------------------------------------------------------------------------------------------
## 1. Signal paths

```
 PC sound OUT (TX) --[600:600 isolation transformer]--> Accessory 2 LINE2+ pin 3 / LINE2- pin 11   (4-wire TX)
 PC line  IN  (RX) <--[600:600 isolation transformer]-- Accessory 2 LINE1+ pin 1 / LINE1- pin 16   (RX; TX+RX in 2-wire)
 PC COM port <--RS-232--> RIB (RLN4008/PLN4008 style, 9 V) <--SB9600--> consolette BUS+ / BUS- / BUSY / ground
```

* **Two-wire (factory default)**: TX and RX share LINE1 (pins 1/16). It works, but your own transmit audio comes
  back on the RX input. A PC console is cleaner in **four-wire**: TX into LINE2 (pins 3/11), RX from LINE1.
  (Manual 2.10.2, Table 2-11.)
* Never connect a sound card directly to a line that may carry DC. Use isolation transformers (manual quotes the
  same caution for TRC desksets; ComTekk TRC documentation says the same).
* Impedance is 600 ohm (factory) or 900 ohm (S101-4/5).

### Using an onboard 7.1 output instead of a second sound card (e.g. ASRock P43DE / VIA VT1708S)

On onboard audio, all of the rear jacks are **one** Windows device with 8 channels. The console can put the radio
TX audio on **only one jack pair** and write silence to every other channel. Windows sounds and receive audio stay
on the front (green) jack.

1. Windows **Sound > Playback > Speakers > Configure**: choose **7.1 Surround**. Turn off "full range" and bass management.
2. **Speakers > Properties**:
   - Enhancements tab: **disable all enhancements**. Also disable any "Speaker Fill", "virtual surround" or
     "upmix" option in the VIA control panel, otherwise stereo Windows sounds get copied onto the side jack.
   - Advanced tab: 16-bit 48000 Hz.
3. Console: **Audio > TX routing...**.
   - TX output = the onboard Speakers device.
   - Jack = **Side (grey)**. Rear (black) or Center/Bass (orange) also work; those two fall back to 5.1 automatically.
4. Wire the **grey side jack** through the isolation transformer to the consolette TX pair. Keep your speakers on the
   **green front jack**. Set the operator speaker (Audio > Recorder "PLAY ON") and the Audio > Receive audio panel output to the same
   onboard device; their stereo audio stays on the front jack.
5. Inputs: **blue line-in** = radio receive (Audio > Receive audio panel input), **pink mic** = microphone.
   - Check that both can record at once: Sound > Recording > each device > Listen. Some onboard codecs share one
     recorder between the jacks.
   - If they can't, use a USB microphone or USB sound card for one of them.
6. **Verify before connecting the radio.** Play Audio > Levels / calibration > HLGT STEADY with a meter or headphones on each jack.
   The tone must appear only on the grey jack. Then play a Windows sound and confirm it does not appear on the grey jack.

The front-panel headphone header normally carries the same front channels as the green jack.

---------------------------------------------------------------------------------------------------------
## 2. TRC board (PLN1687) switches - power off first (manual Tables 2-3 to 2-7)

| Switch | Setting for this console | Meaning |
|---|---|---|
| S100-1 | OFF | Always off for ASTRO |
| S100-2 | your choice | ON = local mic audio NOT sent down the wireline |
| **S100-3** | **ON** | **Tone Remote Control** (OFF = SMP control from a Gold Elite / MCC 5500) |
| S100-4 | OFF | AGC enabled (recommended; ON can leave the low-level guard too weak to key) |
| S100-5..8 | 6 ON, 5/7/8 OFF | **No Mode Select** for a codeplug with more than 8 channels (see CPS_INTEGRATION.md); all OFF = Standard table (8 modes) |
| S101-4 / S101-5 | ON / OFF | 600 ohm |
| S101-7 / S101-8 | OFF / ON | **four-wire** (ON / OFF = two-wire, factory) |
| P103 (PLN1687B only) | B-C | Wireline TX audio is the audio source with PTT (needed if you key over SB9600) |

## 3. Codeplug (ASTRO 25 Mobile CPS) - manual 3.4 and 2.13

* Tone Remote Control option **enabled**.
* **"HUB Defeats PL" selected** (Conventional > Conventional Configuration > General). Without it the monitor tone cannot open PL.
* Internal Mic and External Mic **AGC disabled** (Radio Wide > Tx Audio Control Per Mode).

## 4. Tone-remote signalling (manual 3.4.1, Figure 3-4, Table 3-1)

* Line PTT = **high-level guard tone 2175 Hz for 120 ms**, then the **mode's function tone for 40 ms**, then a
  **continuous low-level guard tone 30 dB lower** under the voice. The consolette dekeys when the low-level guard stops.
* The console sends exactly that. The voice is notched at 2175 Hz and limited to the guard-tone peak.
* **Standard tone table:**

| Tone (Hz) | 2175 | 2050 | 1950 | 1850 | 1350 | 1250 | 1550 | 1450 | 1750 | 1650 | 1150 | 1050 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Function | Guard / PTT | Monitor | Mode 1 | Mode 2 | Mode 3 | Mode 4 | Mode 5 | Mode 6 | Mode 7 | Mode 8 | Secure coded | Secure clear |

  Settings > Console settings > radio tab > Channels / modes has a button that applies this table to modes 1-8. Only 8 modes can be selected by
  tone. For more channels use SB9600 MODE UP / MODE DN (Settings > Console settings > radio tab > ChannelSelect).

## 5. Levels (manual 2.11.2)

* **High-level guard tone (HLGT) at the consolette = 0 dBm = 775 mVrms across 600 ohm.** The TRC AGC normalises to it
  and accepts roughly -20 dB to +13 dB without readjustment. The minimum input is about -35 dBm.
* Factory transmit check: 120 mVrms, 1 kHz into LINE2 (four-wire) gives 3 kHz deviation with AGC enabled.
* Factory receive: R545 set for 0 dBm on LINE1 with a 1 kHz, 3 kHz-deviation signal.

**Procedure in the console**
1. Choose Settings > Operating mode > Live, then open Audio > Levels / calibration.
2. Put a true-RMS meter across the line (or a 600 ohm load) and click **HLGT STEADY**. Steady tones have no
   function tone, so the consolette does not key.
3. Adjust the **Windows output volume** of the TX device until the meter reads **775 mVrms**. Leave Windows volume alone after that.
4. Use **TEST KEY 3 s** into a dummy load. You should see guard, function tone, then 1 kHz with the low-level guard.
5. Set the RX input so normal traffic peaks around -12 dBFS on the RX meter, well below clipping.

---------------------------------------------------------------------------------------------------------
## 6. SB9600 through the RIB

**RIB.** Your MaxtonData RPC-MRIB kit is an RLN4008-equivalent radio interface box. It has a DB9 RS-232 port to
the PC and a DB25 port to the radio, and it needs a 9 V battery or adapter (neither is included). You make the
RIB-to-consolette cable yourself.

**Wiring.** W3AXL's XTL-SB9600-Playground wiring (RIB DB25 to XTL **radio** J2) is:
`BUS+ (RIB 15/24) -> J2-2,  BUS- (RIB 11/13) -> J2-3,  /BUSY (RIB 6) -> J2-9`.
On the consolette the same bus is on **Accessory 2**:

| Accessory 2 pin | Signal |
|---|---|
| 6 | BUSY |
| 7 | BUS+ |
| 19 | BUS- |
| 20 | RESET (W3AXL: probably not needed) |
| 12 | DIG_GND |

Accessory 3 also carries BUSY, BUS+, BUS-, RESET and BUS_SHLD (pin 8). Tie the RIB ground to DIG_GND, and check
your RIB's DB25 ground pin first. Accessory 2 also carries the TRC lines, so you will need a Y cable or breakout.

**Console settings** (Settings > Console settings > radio tab > SB9600 / RIB):

| Setting | Value |
|---|---|
| PortName | your COM port |
| Baud | 9600 |
| BusyOut | DTR |
| BusyIn | CTS |
| ExpectEcho | ON |

These are the RadioConsole2 / Paul Banks defaults: the console asserts BUSY on DTR, reads BUSY on CTS, and
verifies each sent frame by its echo.

**Bring-up order:**
1. Leave *Allow transmit* **off** (listen-only). Choose Settings > Operating mode > Receive only and open Settings > Engineering > SB9600 monitor / LEARN. Press keys on the
   W7 and key the radio. You should see `RX< Head ... pressed`, `RADKEY radio transmitting`, `Squelch OPEN`,
   `SBEP display ...` and so on.
2. If you see only "Unframed bytes", check the baud rate, the RIB power, and BUS+/BUS- polarity.
3. Turn on *Allow transmit*. Click **BUSY test** in Settings > Engineering > SB9600 monitor / LEARN. It should PASS. If it doesn't, try BusyInInvert
   or BusyOutInvert (RadioConsole2 has an invertBusy option for some interfaces).
4. Tick **LEARN** and press each W7 key. Name the captured codes, or check them against the defaults.
5. Optional: *ResetOnConnect* sends `00 00 01 08`. The radio restarts and re-sends its display text, which is what
   RadioConsole2 does at start-up.

**W7 head codes.** The defaults are the Astro W-series (W9) map that RadioConsole2 uses for XTL5000 consolettes:

| Control | Code |
|---|---|
| PTT | 0x01 |
| VIP1 / VIP2 | 0x06 / 0x07 |
| RADIO SEL | 0x10 |
| RSSI | 0x11 |
| SPKR | 0x12 |
| keypad 0-9 | 0x30-0x39 |
| * / # | 0x3A / 0x3B |
| MODE DN / UP | 0x50 / 0x51 |
| VOL DN / UP | 0x52 / 0x53 |
| SEL | 0x60 |
| HOME | 0x61 |
| DIM | 0x62 |
| TOP1-TOP6 | 0x63-0x68 |

**Confirmed on the development W7 (bench log, 20 Oct 2026):** PTT 0x01, VIP1/VIP2 0x06/0x07, RADIO SEL 0x10, RSSI 0x11,
SPKR 0x12, keypad 0x30-0x39, * 0x3A, # 0x3B, MODE DN/UP 0x50/0x51, VOL DN/UP 0x52/0x53, HOME 0x61,
TOP3 (Scan) 0x65, TOP4 (Call) 0x66, **Sel = TOP5 0x67** and **DIM = 0x60**. These last two differ from the W9 map
(SEL 0x60, DIM 0x62); the console sets them when W7BenchKeymap is on (see CPS_INTEGRATION.md). Emer/Phon (TOP1/TOP2, 0x63/0x64) are not used. Keypad 8 = monitor: the radio reports it as DISPLY field 0x05 (1 on / 0 off), which drives the console MON light.

On the development codeplug, **keypad 1 = zone up, keypad 3 = zone down, keypad 8 = monitor, keypad # = scan nuisance delete**; the radio keeps the channel position, or goes to
channel 1 if the new zone is shorter. The console adds these as ZONE UP / ZONE DN when W7BenchKeymap is on. The radio's
mode reports count from 1 in codeplug order (first channel = 1, second = 2, and so on), about 600 ms after the display
text. SBEP indicator 0x02 is the SCAN light and 0x0F the receive BUSY light.
The radio sends only the display characters that change (MURS 1 -> MURS 2 arrives as "2" at column 5); the console
keeps its own copy of the display and merges them. After the knob stops, the mode report arrives once, not per click.

**Console indicator lights (TX RX MON SCAN DIR LPWR SECURE).** The XTL reports a light only when it changes. After the
console connects, a light it has not heard about yet shows as a grey **SCAN?** (unknown) instead of a misleading "off".
**Tools > Sync radio status** (also automatic 2.5 s after SB9600 opens, and when you click any "?" light) presses SCAN
twice, so the radio reports the scan light and redraws its display; scan ends as it started. DIR is filled in from the
channel (simplex = DIR) until the radio reports it. Settings > Engineering > Restart radio restarts the radio over
SB9600 instead (about 3 s, no transmit) and makes it re-send everything. Mapped so far: SCAN = indicator 0x02; DIR = indicator 0x06 (lit on simplex channels, so it follows the channel); MON = DISPLY field 0x05
and the "MON ON / MON OFF" display text; RX = squelch report and indicator 0x0F. To map DIR, LPWR and SECURE:
1. Toggle the function on the W7 and read the code in Settings > Engineering > SB9600 monitor (`SBEP indicators: 0x.. = on`).
2. Enter it under Settings > Console settings > radio tab > SB9600 / RIB > Indicators.

**Development W7 key use (20 Oct 2026):** Scan, Call and Sel are used; Emer and Phon are not. On the keypad, only
1 (zone up), 3 (zone down), 8 (monitor) and # (nuisance delete) do anything. The console's W7 panel greys out the
unused keys (W7 faceplate > UnusedKeys). In Live it also greys out DIRECT and LOW PWR quick keys, since this radio has
no key for them: direct follows the channel.

**What the console decodes:**

| Message | Meaning |
|---|---|
| RADKEY 0x19 | radio transmitting / unkeyed |
| SETBUT 0x0A | registers 1 (monitor) and 3 (transmit) |
| SQLDET 0x1E | 0 = idle, 3 = receiving, 1 = scan activity |
| ACTMDU 0x1F | active mode number (used to sync the channel display) |
| PLDECT 0x23 | PL detect |
| RADRDY | radio ready |
| PRUPST | power-up self test |
| BUTCTL 0x57 | head buttons (0 = up, 1 = down, 2 = toggle) |
| SBEP opcode 0x01 | display text, shown as RADIO DISPLAY in the LCD |
| SBEP opcode 0x21 | indicator LEDs |

Anything else is shown raw. You can turn any frame into an event, for example an emergency alarm, with
**Rule from selected row** in Settings > Engineering > SB9600 monitor / LEARN.

---------------------------------------------------------------------------------------------------------
## 7. First live test checklist

- [ ] Verify.cmd passes on the PC.
- [ ] Test mode (Settings > Operating mode): layout, quick keys, tone sets and alerts arranged the way you want.
- [ ] Receive only: bus frames are decoded, the status sync line appears in the journal, RX audio plays and calls
      appear in Audio > Recorder labelled with their channel.
- [ ] TX output set to the radio interface, not your speakers (Audio > TX routing). The console warns if
      the TX output is the same device as the operator speaker.
- [ ] Dummy load on the consolette. Audio > Levels / calibration done: HLGT = 775 mVrms.
- [ ] TEST KEY works, then PTT with voice, then an alert, then mode-select tones.
- [ ] Only transmit on frequencies you are licensed and authorised to use.

## 8. Footswitch PTT

Settings > Console settings > Footswitch. The FOOT light under the radio display is lit while the pedal is pressed.
"FOOT?" means the pedal cannot be read, and the journal says why. Unplugging the pedal always releases PTT.

| Pedal type | Setting | Notes |
|---|---|---|
| Types a key (most USB foot pedals) | Source = KeyboardKey, Key = the key the pedal sends | Works whatever window has focus. Program the pedal (with its own utility) to a key nothing else uses, such as F13. The console catches that key system-wide and keeps it from other programs |
| Shows up as a game controller (joy.cpl) | Source = GameController, ControllerNumber, Button | Works whatever window has focus. Press the pedal and watch FOOT to find the button number |
| Plain switch on a COM port | Source = ComPortLine, PortName, Line | Works whatever window has focus. Use a COM port other than the RIB's |

Wiring a plain switch to a DB9 COM port (the console holds DTR and RTS high to supply it):

| Line setting | Switch between | DB9 pins |
|---|---|---|
| CTS | RTS and CTS | 7 and 8 |
| DSR | DTR and DSR | 4 and 6 |
| DCD | DTR and DCD | 4 and 1 |

Use a normally-open pedal. For a normally-closed pedal, tick Invert.

## 9. RIB on the motherboard COM header

The RIB connects to a real serial port: the motherboard's 9-pin COM header, with a header-to-DB9 bracket.

- Buy or check the bracket for the standard (Intel / "DTK") header layout: header pin 1 DCD, 2 RXD, 3 TXD, 4 DTR,
  5 GND, 6 DSR, 7 RTS, 8 CTS, 9 RI, wired to the DB9 pin of the same signal. The older "AT / Everex" brackets use a
  different order and will not work. Check pin 2/3 continuity to the DB9 with a meter if unsure.
- Turn the port on in the BIOS (Super I/O > Serial port, 3F8 / IRQ4). Windows then lists it as COM1.
- Set Settings > Console settings > radio tab > SB9600 / RIB > PortName to that COM port, and run the BUSY self-test
  from Settings > Engineering > SB9600 monitor.
- A motherboard port never disappears, so the 3-second reconnect only matters for USB serial adapters (such as a
  bench laptop). If the RIB cable is pulled and replugged, use Tools > Sync radio status to refresh the lights.

## 10. Recovery

| Symptom | Cause | Fix |
|---|---|---|
| Lights show a grey "?" | The XTL reports a light only when it changes | Tools > Sync radio status (automatic on connect) |
| BUS light dark, journal says the RIB port failed | RIB unpowered, cable out, wrong COM port | Check the 9 V supply and cables; the console retries every 3 s. Settings > Engineering > SB9600 monitor > BUSY test |
| Commands "NOT confirmed by the radio" | Bus BUSY / echo problem or AllowTransmit off | Red lines in the SB9600 monitor; AllowTransmit in the radio's SB9600 / RIB settings |
| No receive audio, REC dark | Receive devices not chosen or changed | Audio > Receive audio: pick the line input and speakers, START RX MONITOR (then it starts by itself) |
| Own voice from the consolette speaker while transmitting | Control Point Monitor (consolette manual 3.4.2.1), always on with the TRC board | Turn the W7 volume down; the console's receive audio comes from the line and is not affected |
| Radio keys, then stops while PTT is held (badge NOT ON AIR) | The consolette is not detecting the low-level guard tone that holds it keyed | Audio > Levels / calibration (HLGT 775 mVrms at the consolette), TRC S100-4 OFF (AGC on), Windows audio enhancements OFF on the TX device. Note in the journal how long after keying it dropped |
| Transmit stuck | - | Escape (STOP ALL). TRC PTT also ends if the console closes, and the 60 s timeout always applies |
| Console closed unexpectedly | Internal error | Restart it; error-log.txt is in Console > Open settings folder |
| Settings look wrong after an update | - | Console > Import settings, or restore a backup (Console > Backup settings) |
| Settings file damaged | Disk or power loss during a save | The console starts from console.config.xml.bak and says so in the journal; the damaged file is kept beside it |

