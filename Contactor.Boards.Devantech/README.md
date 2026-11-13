# The Devantech ETH relay boards (`Contactor.Boards.Devantech`)

How the app talks to these boards. What every board shares (`IRelayBoard`, `IBoardConnection`, the board catalogue) is
in `docs/ARCHITECTURE.md`.

This project connects Contactor to four Devantech ETH relay boards. It has the command bytes, the client, the adapters
for the app and a simulator. It has no web or database code.

Nobody has tested the app on a real board yet. Devantech's own site was not reachable when this was written, so the
facts come from copies of Devantech's documents and code, and from a session recorded on a real ETH008-B. The list in
"To check on a real board" shows what is not known.

## Sources

- *ETH002-B Technical Documentation*, Devantech, 13/11/2023, a copy in `github.com/julienGautier77/relay`.
- The *ETH008 Technical Documentation* web page (robot-electronics.co.uk), as saved in 2015, a copy in
  `github.com/arundaleais/yachtsource_racestation`.
- Devantech's own code: `github.com/devantech/devantech_eth_python`, `ethx_java`, `eth008_python`, `eth008_dotnet`.
- A session on a real ETH008-B (module id 19, hardware 2, firmware 28): `github.com/KrisChaplin/kris-lab`,
  `devantech-eth008b/`.
- The ETH8020 command table, as `github.com/kiloreven/devantech_relays` quotes it, and the libsigrok driver
  `src/hardware/devantech-eth008/`.

## Models

| Catalogue id | Name | Relays | Inputs | Module id | Bytes of 0x24 | Bytes of 0x25 |
|---|---|---|---|---|---|---|
| `devantech-eth002` | Devantech ETH002-B | 2 | 0 | 18 | 1 | none |
| `devantech-eth008` | Devantech ETH008-B | 8 | 0 | 19 | 1 | none |
| `devantech-eth484` | Devantech ETH484-B | 4 | 8 | 20 | 2 | 2 |
| `devantech-eth8020` | Devantech ETH8020-B | 20 | 8 | 21 | 3 | 4 |

All four use port 17494. Their only login is an optional TCP password. `DevantechModels` has the four models and
their layouts. The original ETH002, ETH008, ETH484 and ETH8020 have the same module ids, so the app also takes them.

The ETH484-B and the ETH8020-B also have analog inputs. The app does not read them yet: that is issue #113.

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

`nn` is the relay number. `tt` is a pulse time in units of 100 ms. The app always sends `tt` = `00`, so the relay stays
as set. A relay number that the board does not have gets `01`.

Layouts:

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

`DevantechProtocolTests` and `DevantechSimulatedBoardTests` check these exchanges byte for byte.

## Password

The TCP password is set on the board's configuration page. It is not the login of the board's web page.

- With a password, each TCP connection starts locked. The board reads its state, but it does not switch a relay.
- The password unlocks only the connection that sent it.
- The board locks again after 30 s without a command. 0x7A gives the seconds that are left.
- The board has no user name. The Settings screen and the setup guide show only "Password" for these models
  (`BoardModel.PasswordOnly`).
- The app takes at most 32 characters: letters, digits and plain symbols, without spaces.
- The app never logs or traces the password. The Debug screen shows its command as `79 …`.

## Client

`DevantechClient` keeps one TCP connection and sends one command at a time. Each command waits for its answer.

On each new connection, the client:

1. Sends 0x10. When the module id is not the chosen model's, the client switches nothing. The connection fails with,
   for example, "The board at this address is an ETH484, not an ETH008.". The poller tries again with its backoff.
2. Sends 0x7A. When the board is locked, the client sends the password. When the board refuses the password, or when
   there is no password, the connection fails, and the poller tries again.

Then:

- Each command and each connection attempt has 5 s on `TimeProvider`.
- The client reads only the length of the answer. A short answer counts as no answer after 5 s.
- An answer with a value it cannot have is unreadable, for example `05` to 0x20. Then the client closes the
  connection, because the stream can be out of step. The next command opens and checks a new connection.
- Bytes that wait before a command were not asked for. The client drops them and shows them on the Debug screen.
- `BoardPoller` does the polls, the reconnects and the count of failures, as for the VM204, the VM208 and the
  Waveshare boards.

A command switches the relays in the mask one at a time:

- First the client sends 0x7A. When the board locked again, it sends the password again.
- The client sends 0x20 or 0x21 for every relay in the mask, also when the board refuses one.
- A command with no answer stops the rest of the mask.
- When the board takes no relay, the command throws "The Devantech board didn't take the command: …".
- Otherwise it returns the relays the board confirmed. A refused relay, or one whose answer couldn't be read, isn't among them.
- When a command gets no answer, the command throws "The Devantech board didn't answer; the command may not have been
  carried out."
- The poller reads the board at once after each of these two failures. It counts a command with no answer as a failed
  poll, and also a command with an answer that cannot be read, or a connection it cannot check or unlock: a new one, or
  the open one when `7A` before a command fails. When the board answered `01` to every relay, the board answered: the
  command is a refusal (`BoardRefusedException`), which does not count.
- After the switches, the client reads the relays and the inputs in the same command. The poller takes that as the
  new state. When that read cannot be read, the client keeps the last state it read and reads again at once.

`DevantechBoard`, `DevantechConnection` and `DevantechTraffic` are the adapters for the app. `Features/Devantech` in
`Contactor.Web` registers them. `DevantechTraceText` puts each command in plain words. The polls and the answers that
change nothing are routine.

## Settings

The `Devantech` section applies to all four models:

```json
"Devantech": {
  "Host": "",
  "Port": 17494,
  "Password": "",
  "PollSeconds": 2
}
```

When `Host` is empty, the setup guide asks for the address.

## Simulator

`DevantechSimulator` simulates each of the four models on a local port. `Contactor.Simulator` offers port 17494.

- It answers 0x10, 0x20, 0x21, 0x23, 0x24, 0x77, 0x78, 0x79, 0x7A and 0x7B, and 0x25 on the models with inputs.
- It gives no answer to a command that it does not know.
- It reads each TCP packet as the commands in it, and a password as the rest of its packet.
- With a password, each connection starts locked and locks again after 30 s without a command. A relay command while
  locked gets `01`. Every command counts as activity.
- It takes a pulse time as permanent.
- Relays and inputs can be set "at the board", and connections can be dropped or refused.
- For the tests, `NoAnswer` stops all answers, `FailRelays` refuses some relays, and `ModuleId` makes it answer as
  another model.

## To check on a real board

- What 0x20 and 0x21 answer while the board is locked. The app expects `01`.
- If the board answers 0x24 and 0x25 while it is locked.
- Which commands keep the board unlocked: if a poll (0x24, 0x25) counts as activity for the 30 s lock.
- The longest password, and which characters the board takes.
- What 0x79 answers when the board has no password.
- The layouts of the ETH484-B and the ETH8020-B: the byte orders of 0x24 and 0x25, and the input bit order.
- That an input bit of 1 means a closed contact.
- The relay states after a power cut, with "Latched Outputs" on and off. The recorded ETH008-B had all relays off.
- How many TCP clients the board accepts. The documents say five.
- That the board never sends data that the app did not ask for.
- How the board handles a poll every 2 s for days.
