# Changelog - Dispatch Console

Versions are numbered `major.minor.patch`. The zip name, the folder inside it, the window title, Help > About and
the first journal line all show the same number.

## v1.0.2-public - 22 Oct 2026
- **First public release.** Department branding moved to `branding/brand.ini` (name, motto, logo, icon, settings
  folder) with a neutral placeholder logo. The shipped codeplug is now a fictional example
  (`cps/Example_XTL5000_Channels.csv`); first-run quick keys use the first four channels of zone 1. The development
  bench's W7 key map is applied only when Bus.W7BenchKeymap is on. Unknown theme names fall back to the default theme.
  Settings live in `C:\ProgramData\Dispatch Console` by default.

## v1.0.2 - 22 Oct 2026
- **The radio display shows at start-up.** Bench log 4 shows a SCAN press sends only the scan light, so the old sync
  left "WAITING FOR SBEP DISPLAY" until a key was pressed. A VOL press makes the XTL show "VOL n", then redraw the full
  channel name about 1 s later. Sync radio status (automatic on connect) now presses VOL UP then VOL DN (the volume
  ends where it was) before SCAN twice, and the journal reports the display it got.
- **The console starts on the radio's real channel.** During a sync, the channel the radio display names becomes the
  console's channel (once; never during receive or a scan landing).
- **TRC light is green** when radio audio is open and ready (it was red, left over from the ARM TX design). Amber =
  open but ARM TX needed (RequireArm), dark = not open.
- **Create_Desktop_Shortcut.cmd** puts a "Dispatch Console" shortcut on the desktop.

## v1.0.1 - 22 Oct 2026
- **Dropped-transmit warning.** In Live with TRC PTT, the console watches the radio's own SB9600 transmit report. If
  the radio stops transmitting while PTT is still held (it keyed on the loud guard tone, then lost the low-level guard
  tone), the badge flashes **NOT ON AIR**, the PTT bar says **RADIO STOPPED TRANSMITTING**, and the journal logs when it
  dropped and what to check: calibration, TRC S100-4 (AGC) and Windows audio enhancements on the TX device. If the radio
  sends no transmit report within 1.5 s of keying, that is logged too.

## v1.0.0 - 22 Oct 2026 (first release)
- **Classic Dispatch (MCC style) theme**: light grey panels and keys, dark text, blue accent, amber selected channel,
  light display. Settings > Layouts and themes, or Appearance > ThemeName.
- **Paging tone sets on the main screen.** Settings > Console settings > Alerts / paging > **Add tone set** creates a
  two-tone set with Quick Call II timing (1000 ms + 3000 ms). **ShowOnMain** gives any alert or tone set a key in the
  ALERT / TONE KEYS panel (no longer just the first 8), **ColorHex** colours it, Up / Down orders the keys. Existing
  settings keep the keys they had; the two test alerts are hidden by default.
- **Robust start-up**: an unreadable settings file no longer stops the console. It loads console.config.xml.bak (or
  first-run defaults) and keeps the damaged file aside, and the journal says so. Import settings refuses the file
  already in use.
- **Operator card** (OPERATOR_CARD.md, also Console > Operator card) and a recovery table in HARDWARE_SETUP.md
  section 10. README_FIRST.md rewritten as the v1.0 guide. VALIDATION.txt records what was checked on the bench.
- Wording: every remaining "SIMULATION", "LIVE - TX ENABLED", "ARM TX" and "Tools > SB9600" reference updated to the
  v0.9 menus and modes. The program's file version is now 1.0.0.0 (it had stayed at 0.8.0.0).
- Self-test: tone-set keys follow ShowOnMain, damaged settings fall back to the .bak copy, every theme builds.

## v0.9.3 - 22 Oct 2026
- **Settings carry over between versions.** Settings, receive-audio choices, recordings and error logs now live in one
  shared folder, C:\ProgramData\Dispatch Console, instead of each version's build folder. Every new version uses
  the same settings.
  - First start with this version: the newest console.config.xml found in this or the neighbouring version folders
    (and their build subfolders) is imported automatically, with its receive-audio files. The journal says from where.
  - Console > **Import settings...** takes a console.config.xml from a backup or any older folder. The current settings
    are kept as console.config.xml.before-import, and the console restarts.
  - Console > **Open settings folder**. Backups (Console > Backup settings) now restore with Import settings.
  - Recordings made before v0.9.3 stay in the old build\Recordings folder; new ones go to the shared folder.

## v0.9.2 - 22 Oct 2026
- **Channel keys stay on the operator's channel.** When scan lands on another channel, only the display at the top
  changes: it shows "SCAN - RECEIVING ON <zone / channel>" and "YOUR CHANNEL: ...". The zone / channel keys and the
  drop-downs do not move. They follow the radio only when the radio itself is changed (W7 knob, HOME, a W7-window key);
  the radio's mode report confirms that, and scan landings send none. Keying during a scan landing is allowed, with a
  journal note that the codeplug's talkback setting decides the transmit channel.
- **Recordings carry the channel.** RX calls are labelled with the channel the radio was receiving on (the scan landing),
  TX calls with the operator's channel, for example "RX  CITY FD (City Fire Department)  4.2 s". WAV file names use the
  channel, and index.csv gains a "channel" column (added last, so older files still line up).
- **Sync radio status without restarting the radio** (Tools > Sync radio status, or click any "?" light). It presses
  SCAN twice so the radio reports its scan light and redraws its display; scan ends as it started. It runs by itself
  2.5 s after SB9600 opens (BusSettings > SyncOnConnect, needs AllowTransmit). Until the radio reports DIR, it is filled
  in from the channel (simplex = DIR). The old restart is under Settings > Engineering > Restart radio.
- **HOLD TO TALK is bright red** in every theme, and brighter while on the air.
- **DTMF left the main screen**; it is in Tools > DTMF.

## v0.9.1 - 21 Oct 2026
- **Keyboard-style USB pedals** (Footswitch > Source = KeyboardKey). The pedal's key (F13 by default) is caught
  system-wide and kept from other programs, so PTT works with a CAD or softphone in focus. The hook runs on its own
  thread, so a busy console window can never make Windows drop it. Scroll Lock can be written as "Scroll".
- HARDWARE_SETUP.md section 9: the RIB on the motherboard COM header (bracket pinout, BIOS, port setting).

## v0.9.0 - 21 Oct 2026
- **Final-product main screen.**
  - Starts Live. Settings > Operating mode switches between Live, Receive only and Test mode (simulated radio).
    Test mode shows a TEST MODE badge. Console settings > StartIn sets the start-up mode.
  - No ARM TX step in normal operation. Console settings > RequireArm brings it back for bench work.
  - Engineering tools moved to Settings > Engineering. The mode drop-down left the menu bar. EXPORT WAV and DTMF
    EXPORT moved from the alert panel to the tone test files window.
- **Recording.** RX monitoring and recording start automatically in Live on the saved receive devices. Closing the
  receive audio window hides it. Disk logging is on (turned on once for existing settings). Recordings older than
  KeepDays (90) are deleted hourly; only the recorder's own day folders and files are touched. REC light = RX recording.
- **Footswitch PTT** from a game-controller pedal or a COM port line. It works with another window in focus, shows on
  the FOOT light, and releases on unplug. A keyboard-style pedal uses the PTT hotkey.
- **RIB COM port reconnects** after a USB serial unplug / replug (3 s check). The journal reports both.
- **Background-thread crashes** are logged to error-log.txt.
- Self-test: start mode, no-ARM live, footswitch press / release / loss, recording clean-up, footswitch port clash,
  menus, no test buttons on the main screen.

## v0.8.7 - 20 Oct 2026
- **Status lights tidied.**
  - A light the radio can never report is hidden instead of showing a permanent "?". On the development XTL that is
    LPWR and SECURE (no indicator code mapped). Map a code in Settings > radio tab > SB9600 / RIB > Indicators to bring one back.
  - "Not reported yet" (MON? SCAN? DIR?) is now grey instead of orange, because it is not a fault. It clears the
    first time the radio reports that light.

## v0.8.6 - 20 Oct 2026
- Self-test only, no change to how the console runs. The macro check could fail depending on timing. The simulated
  radio's delayed mode report (~600 ms, like the real XTL) from the previous test could land mid-macro and move the
  console back to the radio's channel, which is correct behaviour. The test now lets that report arrive first and
  waits for the macro to finish. If it fails, it reports the channel, LOW PWR state and steps left.

## v0.8.5 - 20 Oct 2026
- **Real fix for the Compact channel area.** The CHANNELS / ALERTS tab control was never told to fill its space, so
  it stayed at the Windows default of 200 x 100 px. The self-test report showed it: area 228 px, tabs 100 px, channel
  keys 56 px. It now fills the area, about 190 px of channel keys on a 1024x768 screen, and the full width.
- Self-test: the MONITOR quick-key light check now starts with monitor off. An earlier menu test leaves it on, so
  the check would have switched it off and failed.

## v0.8.4 - 20 Oct 2026
- **Compact layout on a short window:** the journal shrinks, and hides below a readable size, so the channel keys
  always keep at least one full row. It comes back when the window is taller. (The v0.8.3 self-test measured 56 px
  for the channel keys on the 1024x768 laptop.)
- If that self-test still fails, it reports the window size and the height of every section.

## v0.8.3 - 20 Oct 2026
- **Compact layout with several radios:** the radio cards move to their own **RADIOS** tab. v0.8.2 put them above the
  channel area, which left too little room for the channel keys on a 1024x768 screen (caught by the self-test).
- Compact tab names shortened to CHANNELS / ALERTS / RADIOS so they are not cut off.

## v0.8.2 - 20 Oct 2026
- Fixes the v0.8.1 self-test failure "Default global hotkeys must parse and be unique". The test predates the
  blank extra Monitor key; it now allows that one deliberately blank key. No change to how the console runs.

## v0.8.1 - 20 Oct 2026
- **One button per control.** Radio functions moved to the quick-key bar: MONITOR (F4), SCAN (F5), NUIS DEL (F6).
  - Function quick keys light while the radio reports the function on, and grey out in LIVE when the radio has no key for it.
  - Removed: the RADIO FUNCTIONS button group, the second MON key (F9; existing settings are cleared only when a
    MONITOR quick key exists), and < RADIO / RADIO >, GROUP PTT and ALL CALL when there is only one radio
    (their keyboard keys still work).
  - Existing settings get the SCAN and NUIS DEL quick keys once, next to MONITOR (F5 / F6 only if free).
  - The quick-key action list now offers every function (func:NUIS DEL, func:DIRECT, func:LOW PWR, func:BEACON ...).
- **W7 control head in its own window** (Tools > W7 control head, or click the radio display). Show / hide panels can
  put it back on the main screen in the Full layout.
- **Screen sizes.** Layout Auto / Full / Compact (Settings > Show / hide panels, or Appearance).
  - Auto picks Compact below about 1400 x 820 (1024x768 and 1366x768 laptops) and rechecks after a resize or move. It never rebuilds during TX, paging or a channel change.
  - Compact: shorter header, radio display and journal, channels / alerts as tabs, quick keys on one row, card column hidden with one radio.
  - The window opens no bigger than the screen it is on. Minimum size is 800 x 560.
  - Settings, Appearance, bus monitor, text viewer, receive audio and tool windows all fit the screen.

## v0.8.0 - 20 Oct 2026 (replaces every "v07" download)
- **Command verification.** After each command the console waits for the radio's answer and logs
  "confirmed by the radio" or "NOT confirmed". The footer shows the last result.
  - MON: monitor report. SCAN: scan light. Other keys: key beep, display or light. Channel changes: display readback.
  - TRC PTT: the radio's "transmitting" report over SB9600.
- **Channel display stays truthful.** In LIVE the console no longer shows a channel the radio is not on. If a change
  (Ctrl+1-4, channel keys, drop-downs, HOME) cannot start or does not complete, it shows the channel the radio
  reports and says why.
- **Head-button repair.** The standard W7 keys (MODE UP/DN, HOME, PTT, ...) were missing after a first run. They are
  restored on load.
- **W7 map from the bench logs.**
  - Keypad: 1 zone up, 3 zone down, 8 monitor, # nuisance delete.
  - Top keys: Scan = top key 3, Sel = 0x67 (top key 5), DIM = 0x60.
  - Lights: DIR = indicator 0x06 (simplex channels), SCAN = 0x02, BUSY = 0x0F. MON = display field 0x05.
  - Unused keys greyed out.
- **Channel changes use the ZONE / MODE keys on the development radio.** Bench log 4 shows the XTL ignores the direct
  "go to mode" command. After the first ignored attempt, the console turns DirectChannelGoto off for that radio
  and remembers it. Each key step is confirmed from the display.
- **SB9600 first, TRC fallback** for monitor and secure. PTT and voice stay on TRC.
- **Indicators.** "?" until the radio reports a light. Tools > Resync radio status.
- **Channel select.** Direct jump with fresh-display confirmation; mode numbering learned (codeplug position + 1).
- **SBEP parser.** Fixed for a doubled EPREQ. Partial display updates verified against real traffic.
- **Layout and wording.** Fixes across the main screen and menus.

## Bus transmit confirmed (bench log 4, 20 Oct 2026)
The console's MON and SCAN presses reached the radio: TX frames were echoed, and the radio answered MON ON/OFF and
the scan light.

## v0.7 (several unnumbered "v07" downloads, 19-20 Oct 2026)
- Merge of v0.6.1 (menu bar, W7 panel, tool windows, transmit gates) with v0.5 (CPS import, closed-loop channel seek).
- Hotkey system rebuilt; confirmed channel names; first-run codeplug load.
