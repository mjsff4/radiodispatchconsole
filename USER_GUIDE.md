# Dispatch Console v1.0.2 - User guide

PC dispatch console for a Motorola XTL5000 consolette (W7 head, PLN1687 tone remote board), in the style of
an MCC console. It sends real tone-remote audio (guard tone, function tone, low-level guard, voice, alerts and paging
tones) down the consolette's wireline. It also controls and reads the consolette over SB9600 through the RIB: channel,
zone, monitor, scan, display text and lights. It is original open-source software, not Motorola or ComTekk software.

Every download is numbered: the zip, the folder inside it, the window title and CHANGELOG.md carry the same version.
Your settings live in **C:\ProgramData\Dispatch Console**, so a new version starts with them automatically.
The folder name, department name, motto and logo come from **branding\brand.ini** (see the README).

## Install or update

1. Extract the zip into the folder that holds your other console versions (right-click > Properties > Unblock first).
2. Run **Verify.cmd**. It builds the self-test with the Windows .NET Framework 4 compiler and runs every check.
   It must end with two PASS lines. If it stops, send the FAIL line back.
3. Run **Build_And_Run.cmd**. It builds `build\DispatchConsole.exe` and starts it. After that,
   **START_CONSOLE.cmd** starts it.
   **Create_Desktop_Shortcut.cmd** puts a "Dispatch Console" shortcut on the desktop (run it again after each
   update so the shortcut points at the new version).
4. First start of a new version: if C:\ProgramData\Dispatch Console has no settings yet, the console imports the
   newest settings it finds in this or the neighbouring version folders. The journal says where they came from.
   Console > **Import settings...** takes any other console.config.xml (for example from Console > Backup settings).

## Operating modes (Settings > Operating mode)

| Mode | What it does |
|---|---|
| **Live** (normal) | SB9600 control and readback, TRC transmit audio, receive audio monitored and recorded. Ready to talk: hold PTT. |
| Receive only | SB9600 and receive audio; nothing is transmitted. For setup and listening. |
| Test mode | Simulated radio and SB9600 bus; nothing reaches the hardware. A TEST MODE badge shows while it is on. |

Console settings > **StartIn** sets the mode the console starts in (Live by default). **RequireArm** adds an ARM TX
button for bench work.

## The main screen

- **Header**: department name, the mode badge (LIVE, RECEIVE ONLY, TEST MODE, ON AIR) and the clock.
- **Radio display**: your channel (zone / position, frequencies, flags), the W7's own display text
  (`RADIO DISPLAY: CITY FD = City Fire Department`) and LAST UNIT ID. While scan receives on another channel, the
  display shows `SCAN - RECEIVING ON ...` and `YOUR CHANNEL: ...`; your zone / channel keys do not move.
  Click the display to open the W7 control head.
- **Lights**: TX, RX, MON, SCAN, DIR, EMERG, BUS (SB9600 open), TRC (radio audio open), REC (receive audio being
  recorded), FOOT (footswitch, when set up). A grey "?" means the radio has not reported that light yet; click it, or
  use Tools > Sync radio status.
- **Channel row**: zone and channel drop-downs, CH - / CH +, HOME, REPLAY (and < RADIO / RADIO > with several radios).
- **Zone / channel keys** (Full layout) or the CHANNELS tab (Compact layout): one zone at a time; each key shows the
  position, full name, radio label and SCAN / RX ONLY / HOME flags. Only you move these.
- **Alert / tone keys** (or the ALERTS tab): your alerts and paging tone sets, the preset list with SEND, + QUEUE,
  LISTEN and STOP, and PAGING QUEUE.
- **Quick-key bar**: MONITOR, SCAN, NUIS DEL and your own keys, each printing its keyboard key. Function keys light
  while the function is on. Right-click a key to edit, recolour, move or delete it; **+** adds one.
- **PTT bar**: HOLD TO TALK (bright red), the TX status line and STOP ALL.
- **Journal**: everything the console did, including each command's "confirmed by the radio" result.

The window picks the **Full** layout on large screens and **Compact** on small ones (1024x768, 1366x768);
Settings > Show / hide panels can force either.

## Keyboard (defaults)

| Key | Action |
|---|---|
| F12 (hold) | Talk on the selected radio |
| Escape | STOP ALL: ends transmit, alerts, paging, macros and channel changes |
| PageUp / PageDown | Channel up / down |
| F8 | Replay the last received call |
| F1 / F2 / F3 | ATTENTION / BEEPER / HI-LO alerts |
| F4 / F5 / F6 | MONITOR / SCAN / NUIS DEL |
| Ctrl+1 ... Ctrl+4 | First four channels of zone 1 |

Console > **Keyboard shortcuts** lists every key and flags duplicates. Change the global keys in Settings > Console
settings > Hotkeys, and a quick key's key by right-clicking it > Edit. Keys work while any console window has focus;
letters and digits still type normally in a text box.

## Paging tone sets

Settings > Console settings > **Alerts / paging** > **Add tone set** makes a two-tone set (tone A, then tone B, with
Quick Call II timing: 1000 ms + 3000 ms). Enter the department's verified tones, give it a name, and tick
**ShowOnMain** to give it a key in the ALERT / TONE KEYS panel (ColorHex colours the key; Up / Down orders them).
A quick key can also send it: `alert:STATION 1`. **+ QUEUE** and the paging queue send several in order.

## Recording

In Live and Receive only, receive audio starts by itself on the devices chosen once in **Audio > Receive audio**
(closing that window only hides it). Every received and transmitted call is saved as a WAV file with a daily
index.csv in `C:\ProgramData\Dispatch Console\Recordings`, labelled by channel: received calls by the channel
the radio was receiving on, your own by your channel. Recordings older than 90 days are deleted automatically
(Console settings > Recorder > KeepDays). **Audio > Recorder / instant recall** plays them back.

## Footswitch

Settings > Console settings > **Footswitch**. A USB pedal that types a key: Source = KeyboardKey, Key = what the
pedal sends (program it to F13). A pedal that Windows lists as a game controller: Source = GameController. A switch
on a COM port: Source = ComPortLine. All three work while another program (CAD, softphone) has focus. The FOOT light shows the
pedal. Details and wiring: HARDWARE_SETUP.md section 8.

## Settings, backups and themes

- **Console > Backup settings** saves a dated copy; **Import settings** restores one; **Open settings folder** opens
  C:\ProgramData\Dispatch Console (settings, receive-audio choices, recordings, error-log.txt).
- If the settings file is ever unreadable, the console starts from the previous copy (console.config.xml.bak) and
  says so in the journal.
- **Settings > Layouts and themes**: Black & Gold (default), Classic Dispatch (MCC style), Console Gray,
  Night Blue, Night Red, Day Light, High Contrast and Custom. Interface scale 70-200 %.
- **Settings > Engineering** holds the setup tools: SB9600 monitor / LEARN, W7 mappings and display names,
  diagnostics, tone test files and WAV export, restart radio, hardware guide, action reference.

## Safety

- Transmit ends on PTT release, focus loss (keyboard and mouse PTT), Escape / STOP ALL, the PTT timeout (60 s), an
  audio device error or an internal error. Unplugging a game-controller or COM-port pedal ends it too.
- PTT is sent as TRC tones: if the console stops for any reason, the guard tone stops and the radio unkeys.
- The console will not key a receive-only channel, a channel with no TRC tone, during a channel change, or when the
  radio display shows a different channel than yours (except a scan landing, which the journal notes).
- Receive only and Test mode never transmit.

## More documents

- **OPERATOR_CARD.md**: one-page card for the console position.
- **HARDWARE_SETUP.md**: wiring, TRC switches, levels, SB9600 / RIB, footswitch, motherboard COM port, recovery.
- **CPS_INTEGRATION.md**: how the codeplug maps to the console.
- **CHANGELOG.md**: every version's changes.
