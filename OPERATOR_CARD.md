# Dispatch Console - Operator Card (v1.0)

## Talk
- **Hold F12** or **hold HOLD TO TALK** (red). Release to stop. A footswitch works even with another program (CAD, phone) in front.
- **Escape = STOP ALL**: ends transmit, alerts, paging and channel changes at once.
- Transmit stops by itself after 60 s.

## Channels
- Click a **zone** key, then a **channel** key. Or use the drop-downs, PageUp / PageDown, or **HOME** (your home channel).
- **Ctrl+1 ... Ctrl+4**: the first four channels of zone 1 (right-click a quick key to change it).
- Top right of the display says **RADIO CONFIRMED** when the radio is on your channel.
- **SCAN - RECEIVING ON ...** = scan picked up traffic on another channel. Your channel keys stay where they are.

## Radio functions (quick keys)
| Key | Function |
|---|---|
| F4 | MONITOR (lights while on) |
| F5 | SCAN (lights while on) |
| F6 | NUISANCE DELETE |
| F1 / F2 / F3 | ATTENTION / BEEPER / HI-LO alert tones |
| F8 | Replay the last received call |

Paging: click a tone-set key in **ALERT / TONE KEYS**, or pick it in the list and **SEND** (**+ QUEUE** for several).

## Lights under the display
| Light | Meaning |
|---|---|
| TX / RX | Transmitting / receiving |
| MON / SCAN / DIR | Monitor, scan, direct (simplex channel) |
| BUS / TRC | Radio control link / radio audio link open (TRC green = ready to talk) |
| REC | Receive audio is being recorded |
| FOOT | Footswitch pressed |
| Grey "?" | Not reported yet: click it to sync with the radio |

## Badge (top)
**LIVE** = ready. **ON AIR** = transmitting. **RECEIVE ONLY** / **TEST MODE** = nothing is transmitted
(Settings > Operating mode > Live to return).

## If something is wrong
| Problem | Do this |
|---|---|
| Lights show "?" or the display looks stale | Tools > Sync radio status |
| BUS light dark | Check the RIB power and cable. The console reconnects by itself |
| No receive audio | Audio > Receive audio: check the input and speakers, START RX MONITOR |
| Own voice from the consolette speaker | Turn the W7 volume all the way down (the console has its own audio) |
| Need an old call | Audio > Recorder / instant recall |
| Console closed unexpectedly | Start it again. Send `error-log.txt` from Console > Open settings folder |
