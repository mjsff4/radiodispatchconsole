# Codeplug (CPS) integration

The console reads a Motorola CPS CSV export of the consolette's **Zone Channel Assignment** and **Scan List** features
and builds its channel list from it. The repository ships a fictional example, `cps/Example_XTL5000_Channels.csv`,
with full names in `cps/channel_names.csv`. **Replace both with your own export before using the console on the air.**

> The example's local channels and frequencies are made up. Only the national interoperability channels
> (VCALL10 / VTAC11-14), MURS and NOAA weather use their real frequencies. Never program a radio from the example file.

## What the console takes from the codeplug

| Codeplug fact | What the console does with it |
|---|---|
| Zones and channels with 8-character channel text | Channels keep their zone and position. The zone/channel key panel shows one zone at a time, with ZONE buttons above the channel keys. The W7 label (e.g. `CITY FD`) becomes each channel's RadioText, and the console shows the full name from `channel_names.csv`. |
| RX/TX frequency, PL/DPL, personality | Shown in the radio display line and in channel-key tooltips, e.g. `RX 151.6250 PL 186.2 \| TX 151.7875 PL 186.2 \| P1`. Information only; the codeplug stays authoritative. |
| Receive-only personalities (3 and 6 in the example) | Marked **RX ONLY**. The console will **not key PTT, alerts or pages** on them, and logs why. |
| Scan list with priority 1 and 2 members | Channel keys and the display show `SCAN`, `SCAN P1` and `SCAN P2`. |
| Home channel (Z1/2 in the example) | HOME selects it. |
| The same label in two zones (the example has `FIRE MA` at Z1/3 and Z3/1) | When the display shows that label, the console resolves it to the zone it is working in. |

## Important: TRC tone table for a radio with more than 8 channels

The PLN1687 tone remote can select only **8 modes**. On the factory **Standard** table, every PTT also sends a mode-select
function tone (1950 Hz is Mode 1, 1850 Hz is Mode 2, and so on). That would pull the radio off whatever zone/channel you
chose before it transmits.

**Set the TRC board to the "No Mode Select" table** (consolette manual Table 2-7: S100-6 ON; S100-5, S100-7, S100-8
OFF). With that table:
* the tone remote keys the transmitter (guard, 1950 Hz, low-level guard) on **whatever channel the radio is on**
* 2050 Hz monitor and 1150/1050 Hz secure still work
* channels are selected over **SB9600** (below)

The CPS import sets every channel's PTT function tone to 1950 Hz for this. **Bench-check it before relying on it:** with
the tone table changed, key from the console on one channel and confirm the radio stays there and transmits.

## Channel selection over SB9600 (closed loop)

When you pick a channel (a channel key, the drop-downs, a quick key or HOME), the console:

1. Sends **one direct "go to mode" command**: `01 00 mm 1F` then `01 01 mm 60`, from W3AXL's XTL5000 tools. It then
   waits for the radio's next display message. When the display shows the target's label, it confirms in the journal,
   for example `radio is on City Fire Department (CITY FD)`. For a repeated label, the radio's mode report must also
   match.
2. Learns the radio's mode numbering from the first confirmed jump. If the radio lands on a different channel, the
   console works out the offset and retries once.
3. Falls back to the **ZONE and MODE UP / MODE DN keys** if the radio ignores the command or numbers its modes
   differently. Every key press waits for the radio's fresh display before the next one.

**HOME** is one press of the radio's own HOME key, then a display check.
Turn off *DirectChannelGoto* (Settings > Console settings > radio tab > SB9600 / RIB) to always use the keys.

What you need to do once:
1. In Receive only (Settings > Operating mode), open Settings > Engineering > SB9600 monitor / LEARN, tick **LEARN**,
   and press your W7 zone button(s). Save them as **ZONE UP** and **ZONE DN**, or **ZONE 1** ... **ZONE n** if you have
   direct zone keys.
2. Confirm **MODE UP / MODE DN** (defaults 0x51 / 0x50, the W9 map) by pressing the W7 channel up/down keys with LEARN on.
3. Turn on *Allow transmit* for the bus, then try a channel in another zone. If the display text never arrives, enable
   *ResetOnConnect* once so the radio re-sends its display.

### The bench W7 key map

The first-run example load turns on **W7BenchKeymap** (radio tab > SB9600 / RIB > W7 faceplate). It adds the key map
measured on the development consolette: keypad 1 / 3 = ZONE UP / DN, keypad 8 = monitor, keypad # = nuisance delete,
top key 3 = SCAN, Sel = 0x67, DIM = 0x60, DIR light = indicator 0x06. Keypad functions are programmed in the codeplug,
so **if your radio's keypad is set up differently, turn W7BenchKeymap off and LEARN your own keys.**

## Paging

To page from the console over the tone-remote audio, add an alert in Settings > Console settings > Alerts / paging with
**Kind = TwoTone** and your department's verified A/B tones (default timing 1 s A + 3 s B, Quick Call II). Test with your
dispatch center before relying on it.

## MDC1200

A channels-and-scan export holds no signaling settings. To see whether MDC1200 PTT-ID decode/display is configured,
export **ALL** nodes from CPS and look at the personalities' signaling settings. If the radio displays MDC IDs, the
console can read them off the display. An audio MDC decoder is not implemented.

## Full names

`cps/channel_names.csv` (RadioText,FullName) supplies the full names. Names are Title Case for readability; the
8-character radio label stays in capitals, as the W7 shows it. To rename a channel, edit `channel_names.csv` and
re-import, or edit the Name column in Settings > Console settings > radio tab > Channels / modes.

## First run, and using your own codeplug

* **New install:** when there is no settings file yet, the console loads `cps\Example_XTL5000_Channels.csv`
  (copied into `build\cps` by Build_And_Run.cmd) and creates quick keys **Ctrl+1 ... Ctrl+4** for the first four
  channels of zone 1.
* **To start from your own codeplug:** export from CPS (below), save it over `cps\Example_XTL5000_Channels.csv`
  (keep the file name) and put your names in `cps\channel_names.csv` *before* the first run. Or run the import at any
  time, as below.
* **v0.6.1 settings:** converted on load. The COM port, audio devices, head buttons and quick keys are kept.

## How to export and import

In CPS, export the **Zone Channel Assignment** and **Scan List** features to CSV.

In the console: Settings > Console settings > radio tab > Channels / modes > **Import from CPS CSV export...**:
* choose the CSV
* your receive-only personalities (e.g. `3,6`)
* your Home channel as zone/channel (e.g. `1/2`)
* tone table **No Mode Select**
* tick SB9600 channel select

Then click Import. The same dialog is under Console > **Import CPS codeplug (selected radio)**. Re-run the import any
time the codeplug changes. It replaces the console's channel list for that radio.
