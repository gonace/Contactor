# Devantech

How the app talks to these boards. What every board shares (`IRelayBoard`, `IBoardConnection`, the board catalogue) is
in `docs/ARCHITECTURE.md`.

This project connects Contactor to five Devantech ETH relay boards. It has the command bytes, the client, the adapters
for the app and a simulator. It has no web or database code.

Nobody has tested the app on a real board yet. Devantech's own site was not reachable when the first four models were
written, so their facts come from copies of Devantech's documents and code, and from a session recorded on a real
ETH008-B. The ETH1610's come from its own manual on Devantech's site. The list in "To check on a real board" shows what
is not known.

## Sources

- *ETH002-B Technical Documentation*, Devantech, 13/11/2023, a copy in `github.com/julienGautier77/relay`.
- The *ETH008 Technical Documentation* web page (robot-electronics.co.uk), as saved in 2015, a copy in
  `github.com/arundaleais/yachtsource_racestation`.
- Devantech's own code: `github.com/devantech/devantech_eth_python`, `ethx_java`, `eth008_python`, `eth008_dotnet`.
- A session on a real ETH008-B (module id 19, hardware 2, firmware 28): `github.com/KrisChaplin/kris-lab`,
  `devantech-eth008b/`.
- The ETH8020 command table, as `github.com/kiloreven/devantech_relays` quotes it, and the libsigrok driver
  `src/hardware/devantech-eth008/`.
- *ETH1610 Technical Documentation*, Devantech, 02/11/26 (11 February 2026), `robot-electronics.co.uk/files/eth1610.pdf`:
  "Overview" (p. 1), "Analogue Inputs" (p. 4), "Access from the Internet" (p. 7), "TCP access and commands" (pp. 17–18)
  and "TCP/IP Password" (p. 18). For the ETH1610, every command and answer below is from these pages.
- *ETH484-B Technical Documentation*, Devantech, 02/11/26 (11 February 2026), `robot-electronics.co.uk/files/eth484b.pdf`:
  "Overview" (p. 1), "Analogue Inputs" (p. 5) and "TCP access and commands" (p. 18). The analog inputs of the ETH484-B
  come from these pages.
- *ETH484 Technical Documentation* (the original ETH484, module id 20 as the ETH484-B), Devantech, 05/23/24 (23 May
  2024), `robot-electronics.co.uk/files/eth484.pdf`: "Analogue Inputs" (p. 4) and the command table (p. 10).
- *ETH8020-B Technical Documentation*, Devantech, 02/11/26 (11 February 2026), `robot-electronics.co.uk/files/eth8020b.pdf`:
  "Overview" (p. 1), "Connections" (p. 2), "Analogue Inputs" (p. 4) and "TCP access and commands" (p. 17). The analog
  inputs of the ETH8020-B come from these pages.

## Models

| Catalogue id | Name | Relays | Inputs | Analog inputs | Module id | Bytes of 0x24 | Bytes of 0x25 | Pulse | Login |
|---|---|---|---|---|---|---|---|---|---|
| `devantech-eth002` | Devantech ETH002-B | 2 | 0 | 0 | 18 | 1 | none | 0.1 s to 25.5 s | optional TCP password |
| `devantech-eth008` | Devantech ETH008-B | 8 | 0 | 0 | 19 | 1 | none | 0.1 s to 25.5 s | optional TCP password |
| `devantech-eth1610` | Devantech ETH1610 | 10 | 0 (see below) | 16, 0–5 V | 52 | 2 | 2, not read | 0.1 s to 25.5 s | optional TCP password |
| `devantech-eth484` | Devantech ETH484-B | 4 | 8 | 4, 0–3.3 V | 20 | 2 | 2 | 0.1 s to 25.5 s | optional TCP password |
| `devantech-eth8020` | Devantech ETH8020-B | 20 | 8 | 8, 0–5 V | 21 | 3 | 4 | 0.1 s to 25.5 s | optional TCP password |

All five use port 17494. Their only login is an optional TCP password. `DevantechModels` has the five models and
their layouts. The original ETH002, ETH008, ETH484 and ETH8020 have the same module ids, so the app also takes them.

The ETH1610, ETH484-B and ETH8020-B also have analog inputs. The app reads them on every poll (see "Analog inputs"
below).

The app reads no inputs of the ETH1610. Its manual says 0x25 answers 2 bytes for 16 digital inputs, a set bit meaning an
active input (p. 17), but not which bit is which input, and no other Devantech document or code says it either: the
ETH484 and ETH8020 documents only cover 8 inputs in the last byte, and Devantech's `ethx_java` gives the ETH1610 no
digital input bytes at all. So the ETH1610 is relays only until a real board shows the order.

## Protocol

The board takes binary commands on a TCP connection. A command is a command byte and its data. The answer has a fixed
length. There is no framing and no checksum.

- All bytes of a command must go in one TCP packet. The client sends each command in one write, with `NoDelay`.
- The board never sends data by itself. The app polls it every 2 s.
- A command that the board does not know gets no answer at all. For example, the ETH008-B does not answer 0x25.
- Relays are numbered from 1.

| What | Command | Answer |
|---|---|---|
| Which board it is | `10` | 3 bytes: module id, hardware version, firmware version |
| Turn on a relay | `20 nn tt` | `00` done, `01` refused |
| Turn off a relay | `21 nn tt` | `00` done, `01` refused |
| Read the relays | `24` | 1 to 3 bytes, low byte first, bit 0 = relay 1 |
| Read the inputs | `25` | 2 or 4 bytes, high byte first, bit 0 of the last byte = input 1 |
| Send the password | `79` and the password's ASCII bytes | `01` accepted, `02` refused |
| Is the board locked? | `7A` | `00` locked, `01` to `1E` seconds left, `FF` no password |
| Read one analog input | `32 cc` | 2 bytes, high byte first: 0 to 1023 |
| Read all analog inputs (ETH1610 only) | `34` | 32 bytes: 2 for each of the 16 inputs, input 1 first |

`nn` is the relay number. `tt` is a pulse time in units of 100 ms. For ON and OFF the app sends `tt` = `00`, so the
relay stays as set. A relay number that the board does not have gets `01`.

A pulse is `20 nn tt` with `tt` from 1 to 255: the board turns the relay on, and off again by itself after `tt` × 100 ms,
also when the app stops in between. So all five models pulse for 0.1 s to 25.5 s, in steps of 0.1 s
(`DevantechModels.Pulse`). For example, `20 03 05` pulses relay 3 for 0.5 s, and the board answers `00`.

Layouts:

- The ETH1610 answers 0x10 with module id 52 (`34`), and 0x24 with 2 bytes: relays 1 to 8 in the first, 9 and 10 in
  the second (manual p. 17). As for the other models, the app takes bit 0 of each byte as its lowest relay. It answers
  0x25 with 2 bytes (p. 17), which the app does not ask for. Its command table has no 0x78 (pp. 17–18).
- The ETH484-B answers 0x24 with 2 bytes. Relays 1 to 4 are bits 0 to 3 of the first byte. The second byte is its I/O
  used as outputs, and the app does not use it. It answers 0x25 with 2 bytes. Inputs 1 to 8 are the second byte.
- The ETH8020-B answers 0x24 with 3 bytes: relays 1 to 8, 9 to 16, and 17 to 20. It answers 0x25 with 4 bytes. The
  first three are 0, and inputs 1 to 8 are the last byte.
- An input bit of 1 means the input is active (pulled low). The app shows it as "Closed".

Exchanges from the documents and the recorded session:

| Command | Answer | Meaning |
|---|---|---|
| `10` | `13 02 1C` | ETH008-B, hardware 2, firmware 28 |
| `24` | `0D` | Relays 1, 3 and 4 on |
| `20 08 00` | `00` | Relay 8 on |
| `21 08 00` | `00` | Relay 8 off |
| `20 09 00` | `01` | No relay 9 on an ETH008-B |
| `7A` | `FF` | No password |
| `79 61 70 70 6C 65` | `01` | The password "apple" is right |
| `25` | none | The ETH008-B has no inputs |
| `20 02 32` | `00` | ETH1610: relay 2 on for 5 s, the command of its manual's example (p. 17). The `00` isn't printed there: it comes from "Board will return 0 for success" |

`DevantechProtocolTests` and `DevantechSimulatedBoardTests` check these exchanges byte for byte.

### Analog inputs

| Model | Inputs | Command the app sends | Value | Full scale | Source |
|---|---|---|---|---|---|
| ETH1610 | 16 | one `34` | 10 bit, 0 to 1023 | 1023 = 5 V | manual p. 4 ("0v-5V at 10-bit"), p. 18 (0x32, 0x34) |
| ETH484-B | 4 | `32 01` to `32 04` | 10 bit, 0 to 1023 | 1023 = 3.3 V | manual p. 1, p. 5, p. 18 (0x32) |
| ETH8020-B | 8 | `32 01` to `32 08` | 10 bit, 0 to 1023 | 1023 = 5 V | manual p. 4 ("0v-5V at 10-bit"), p. 17 (0x32) |

- `32 cc`: `cc` is the input, from 1. The board answers 2 bytes, high byte first. "The 10-bit conversion will be in
  the lower 10 bits of the 16 bit integer with the upper 6 bits being 0" (ETH1610 p. 4, ETH8020-B p. 17; the ETH484-B
  says "the remaining upper bits being 0", p. 18). For example, `02 00` is 512.
- `34` (ETH1610 p. 18): "The ETH1610 will respond with all 16 inputs of ADC results, transmitted high and then low byte
  for each with input 1 transmitted first. A total of 32 bytes are sent." The ETH484-B and ETH8020-B have no such
  command, so the app sends a `32` for each input.
- The ETH484-B also has `33 cc`: 12 bit, full scale at 5 V (p. 18). The app uses `32`, which the ETH484-B answers "scaled
  to 3.3v for ETH484 compatibility" (p. 18). The original ETH484 has the same module id, 20, and has only `32`: 10 bit,
  0 to 3.3 V (ETH484 manual pp. 4 and 10). The app can't tell the two apart, so it uses the command they both have. On
  the ETH484-B, "for 10 bit results the 3.3V to 5V region will merely read full scale" (p. 5): the app shows 1023 as
  "Above the board's range".
- An analog answer that fails never fails the poll, and never stops the app from switching the relays. The analog
  inputs are a measurement nobody has checked on a real board yet. When an analog answer fails:
  - The relays and inputs of that poll are kept, and the board stays connected.
  - Every analog input of that poll has "no reading", which the screens show as "–".
  - The Debug screen and the log (at Information) say why, in plain words.
  - The client sends no analog request to the board for 1 minute (`DevantechClient.AnalogRetryInterval`, on
    `TimeProvider`), or until the poller opens a new connection. So a board that never answers 0x32 or 0x34 costs one
    time-out per minute, not one per poll.
  - All of this happens only once the relays and inputs after it answered: in the same poll, or in the read of a
    command that took the poll's place. When the relays or inputs fail too, the board may have just died: the
    readings from before stay, and the next poll asks for the analog inputs again.
- An analog answer fails when it has one of the upper 6 bits set (a value above 1023), when it is late (5 s), or when
  it is too short. A value above 1023 comes in an answer of the right length, so the connection stays in step and stays
  open. After a late or short answer, the client closes the connection, so the late bytes can't be taken for the next
  answer. The relays and inputs of the same poll go on a new connection, at most once a minute because of the pause
  above.
- Before the first analog request of a poll, the client opens a connection if there is none, and checks and unlocks it.
  A board that doesn't take the connection or answer the check fails the poll there, as it would for the relays. It
  isn't taken for a problem with the analog inputs.
- The Debug screen, the log and the 1-minute pause come only when the relays and inputs of the same poll answered. When
  they fail too (a board that has just died), the whole poll fails, and the next poll asks for the analog inputs again.
- None of the three manuals says if full scale is 1023 or 1024 counts. The app takes 1023 as full scale: 1023 = 5 V
  (or 3.3 V), and 1 count is 5 V / 1023. The difference is 1 count, about 5 mV.
- None of the manuals gives an example answer. The tests use the manuals' format with made-up values.

The poll order: each poll sends the analog requests first, and then 0x24, and 0x25 on a model with inputs. So the
relays a poll reports, which the interlock checks, are as fresh as they can be, however long the analog inputs took.

The poll cost: the analog requests are 1 more request on the ETH1610 (2 per poll), 4 more on the ETH484-B (6 per poll)
and 8 more on the ETH8020-B (10 per poll). Each request is one round trip on the local network, so a poll takes some
milliseconds longer. An analog request without an answer adds at most 5 s to one poll a minute, and a new connection
for the relays and inputs after it. The read of the board after a command reads the relays and inputs only, and keeps
the last poll's analog values, so a command's confirmation never waits on the analog inputs. A command never waits for
a poll: a poll under way gives way to it.

### Password

The TCP password is set on the board's configuration page. It is not the login of the board's web page.

- With a password, each TCP connection starts locked. The board reads its state, but it does not switch a relay.
- The password unlocks only the connection that sent it.
- The board locks again after 30 s without a command. 0x7A gives the seconds that are left.
- The board has no user name. The Settings screen and the setup guide show only "Password" for these models
  (`BoardModel.PasswordOnly`).
- The app takes at most 32 characters: letters, digits and plain symbols, without spaces.
- The app never logs or traces the password. The Debug screen shows its command as `79 …`.

## The app's client

`DevantechClient` keeps one TCP connection and sends one command at a time. Each command waits for its answer.

On each new connection, the client:

1. Sends 0x10. When the module id is not the chosen model's, the client switches nothing. The connection fails with,
   for example, "The board at this address is an ETH484, not an ETH008.". The poller tries again with its backoff.
2. Sends 0x7A. When the board is locked, the client sends the password. When there is no password, the connection
   fails, and the poller tries again with its backoff. When the board refuses the password, the connection fails. A
   password that never connected isn't tried again by itself: the poller waits for a new password or a person's "Check
   again" (Issue #522). One that connected before is tried every 10 minutes for 24 hours, then held the same way.

When the board refuses the connection in one of these ways, the state says why (`DevantechState.Problem`, a
`ConnectionProblem`), so the screens can show it:

- `WrongModel`, with the model expected and the model found, by the catalogue's names (module id 20 is the
  "ETH484-B"; `DevantechModels.ModelFor`). For a module id of a model the app doesn't support, or doesn't know, the
  found model is null.
- `LoginNeeded`: the board is locked and the app has no password.
- `LoginRefused`: the board refused the password.

The problem never holds the password. `BoardPoller` clears it at the board's first answer, at a new address and at a
new password (`SetPassword`). A connection that fails for another reason, such as no answer, clears it.

Then:

- Each command and each connection attempt has 5 s on `TimeProvider`.
- The client reads only the length of the answer. A short answer counts as no answer after 5 s.
- An answer with a value it cannot have is unreadable, for example `05` to 0x20. Then the client closes the
  connection, because the stream can be out of step. The next command opens and checks a new connection. An analog
  value above 1023 is different: see "Analog inputs".
- When the connection was open from before and turns out broken (the board or the network can close a connection
  that is idle), the client sends the command once more on a new connection, which it checks and unlocks first. It
  does this only when no byte of the answer came, and never for the commands that check and unlock a new connection.
  Each command the client sends has the same result when done twice: 0x10, 0x7A, 0x24, 0x25, 0x32 and 0x34 read, 0x20 and 0x21
  switch one relay or start its pulse again, and 0x79 unlocks.
- Bytes that wait before a command were not asked for. The client drops them and shows them on the Debug screen.
- `BoardPoller` does the polls, the reconnects and the count of failures, as for the VM204, the VM208 and the
  Waveshare boards.

A command (ON, OFF or a pulse) switches the relays in the mask one at a time:

- First the client sends 0x7A. When the board locked again, it sends the password again.
- The client sends 0x20 or 0x21 for every relay in the mask, also when the board refuses one.
- A command with no answer, also on a new connection, stops the rest of the mask: the board is gone.
- When the board takes no relay, the command throws "The Devantech board didn't take the command: …".
- Otherwise it returns the relays the board confirmed. A refused relay, or one whose answer couldn't be read, isn't among them.
- When a command gets no answer, the command throws "The Devantech board didn't answer; the command may not have been
  carried out."
- The poller reads the board at once after each of these two failures. It counts a command with no answer as a failed
  poll, and also a command with an answer that cannot be read, or a connection it cannot check or unlock: a new one, or
  the open one when `7A` before a command fails. When the board answered `01` to every relay, the board answered: the
  command is a refusal (`BoardRefusedException`), which does not count.
- After the switches, the client reads the relays and the inputs in the same command. It doesn't read the analog
  inputs: the state keeps what the last poll read of them. The poller takes that as the
  new state. When that read gets an answer it cannot read or no answer, the command returns the relays that the board
  took. The client keeps the last state it read and reads again at once.

`DevantechClient.PulseAsync` and `DevantechBoard.PulseAsync` throw `ArgumentOutOfRangeException` for a length outside
0.1 s to 25.5 s, or not in steps of 0.1 s, before anything is sent.

A change in the analog values alone is no state change: `DevantechStatus.SwitchesComparer` compares the relays and the
inputs only. Such a change comes as `DevantechClient.AnalogChanged` and `DevantechBoard.AnalogChanged`, never as
`StateChanged`, so the safety services are not woken by a value that moves a little on every poll. A change with a
relay or an input comes as `StateChanged`, with the new analog values in it.

`DevantechBoard`, `DevantechConnection` and `DevantechTraffic` are the adapters for the app. `Features/Devantech` in
`Contactor.Web` registers them. `DevantechTraceText` puts each command in plain words. The polls and the answers that
change nothing are routine.

## Settings

The `Devantech` section applies to all five models:

```json
"Devantech": {
  "Host": "",
  "Port": 17494,
  "Password": "",
  "PollSeconds": 2
}
```

When `Host` is empty, the setup guide asks for the address.

## The simulator

`DevantechSimulator` simulates each of the five models on a local port. `Contactor.Simulator` offers port 17494.

- It answers 0x10, 0x20, 0x21, 0x23, 0x24, 0x77, 0x79, 0x7A and 0x7B, 0x25 on the models that answer it (the ETH1610
  with 2 bytes of 0: it has no inputs to set), and 0x78 on all but the ETH1610.
- It answers 0x32 on the ETH1610, ETH484-B and ETH8020-B, and 0x34 on the ETH1610, in the manuals' format. It gives no
  answer to 0x32 for an input the model doesn't have (the manuals don't say what the board does), and none to 0x33,
  which the app doesn't use.
- It gives no answer to a command that it does not know.
- It reads each TCP packet as the commands in it, and a password as the rest of its packet.
- With a password, each connection starts locked and locks again after 30 s without a command. A relay command while
  locked gets `01`. Every command counts as activity.
- A pulse time switches the relay back after the time, on the simulator's `TimeProvider`: 0x20 turns it off again, and
  0x21 on again. The next poll shows it. A later 0x20, 0x21 or 0x23 for the relay ends its pulse; a later pulse starts
  it again.
- Relays, inputs and analog inputs (0 to 1023, `SetAnalogAsync`) can be set "at the board", and connections can be
  dropped or refused.
- For the tests, `NoAnswer` stops all answers, `FailRelays` refuses some relays, and `ModuleId` makes it answer as
  another model.

## To check on a real board

> **Warning: check the first five points first, before anything is connected to the relays.**

- The layouts of the ETH484-B and the ETH8020-B: the byte orders of 0x24 and 0x25, and the input bit order.
- That an input bit of 1 means a closed contact.
- The relay states after a power cut, with "Latched Outputs" on and off. The recorded ETH008-B had all relays off.
- The ETH1610's 0x24 and 0x23: that relays 9 and 10 are bits 0 and 1 of the second byte.
- The pulse: that the relay goes off by itself after `tt` × 100 ms, and what a 0x20, 0x21 or a second pulse for the
  relay does to a pulse that runs.
- What 0x20 and 0x21 answer while the board is locked. The app expects `01`.
- If the board answers 0x24 and 0x25 while it is locked.
- Which commands keep the board unlocked: if a poll (0x24, 0x25) counts as activity for the 30 s lock.
- The longest password, and which characters the board takes.
- What 0x79 answers when the board has no password.
- The ETH1610's 0x25: which bit is which input, what an input is on the terminals (its manual shows 16 analog inputs
  and no digital ones), and that a set bit means an active input. Then the app can read them.
- If the ETH1610 answers 0x78, which its manual does not list.
- The analog inputs: that 0x32 and 0x34 answer high byte first, that 0x34 answers 32 bytes with input 1 first, and
  that an open input reads about 0 (the ETH484-B has a pull-down, p. 5; the ETH1610 and ETH8020-B schematics show a
  100k resistor to 5 V through a jumper, whose purpose the manuals don't give).
- Which voltage reads 1023: 5 V (3.3 V on the ETH484-B) or one count less. Check with a known voltage.
- What 0x32 answers for an input the board doesn't have, such as `32 00` or `32 11`, and what 0x34 does on an ETH484-B
  or ETH8020-B. The app never sends them.
- If an ETH484-B answers 0x32 with full scale (1023) from 3.3 V to 5 V, as p. 5 says.
- If 0x32 and 0x34 answer while the board is locked. The manuals say only that relays need the password. If they
  don't, the app shows "no reading" while the board is locked.
- How long a poll takes on the ETH8020-B with its 10 requests.
- How many TCP clients the board accepts. The documents say five.
- That the board never sends data that the app did not ask for.
- How the board handles a poll every 2 s for days, and if it closes a connection that is idle, and after how long.
