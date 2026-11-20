# Waveshare

How the app talks to these boards. What every board shares (`IRelayBoard`, `IBoardConnection`, the board catalogue) is
in `docs/ARCHITECTURE.md`.

This project connects Contactor to the Waveshare Modbus PoE ETH relay boards. It has the Modbus codec, the client, the
adapters for the app and a simulator. It has no web or database code.

The facts below come from Waveshare's wiki, read through search extracts. Nobody has tested them on a real board yet.
The list in "To check on a real board" shows what is not known.

## Sources

Wiki pages:

- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay
- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay_(B)
- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay_(C)
- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay_16CH
- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay_30CH

The relay control modes come from the (B) and (C) pages (the same text on both, checked on 17 Nov 2026): §1.5
"Operation Modes Introduction", the register table in §5.2 "Register Address Introduction" (`4x1000…4x1007`, "Channel
1~8 relay control mode", read/write with 0x03, 0x06, 0x10), §5.3.7 "Read Relay Control Mode", §5.3.8 "Set Single Relay
Control Mode", §5.3.9 "Set Multiple Relay Control Mode", and the FAQ "Why does the device provide command feedback when
sending relay on/off action commands, but the relay does not activate …".

Sources for the pulse: Waveshare's development protocol pages for the Modbus POE ETH IO 8CH
(https://docs.waveshare.com/RS232-RS485-CAN-DALI2/Modbus_POE_ETH_IO_8CH/Development-Protocol), the Modbus RTU Module (A)
(https://docs.waveshare.com/Modbus_RTU_Module_A/Development-Protocol) and the Modbus RTU Relay 16CH
(https://waveshare.com/wiki/Modbus_RTU_Relay_16CH). They disagree a little on the address ranges per model, so the
addresses are to check on a real board.

## Models

| Catalogue id | Name | Relays | Inputs | Pulse | Login |
|---|---|---|---|---|---|
| `waveshare-8` | Waveshare Modbus PoE ETH Relay | 8 | 0 | 0.1 s to 3276.7 s | none |
| `waveshare-8b` | Waveshare Modbus PoE ETH Relay (B) | 8 | 8 | 0.1 s to 3276.7 s | none |
| `waveshare-8c` | Waveshare Modbus PoE ETH Relay (C) | 8 | 8 | 0.1 s to 3276.7 s | none |
| `waveshare-16` | Waveshare Modbus PoE ETH Relay 16CH | 16 | 0 | 0.1 s to 3276.7 s | none |
| `waveshare-30` | Waveshare Modbus PoE ETH Relay 30CH | 30 | 0 | 0.1 s to 3276.7 s | none |

All five use port 4196 and have no login. `WaveshareModels` has the five models. The 16CH and the 30CH come from the
wiki only. The wiki's table for the 16CH is not clear about inputs, so the app gives it no inputs.

## Protocol

The board ships in the mode "Modbus RTU over TCP". The app uses only this mode. The app does not use Modbus TCP, which
has an MBAP header and port 502.

- A frame is the unit id, the function code, the data and a CRC-16/Modbus. The CRC uses the polynomial 0xA001
  (reflected) and starts at 0xFFFF. The CRC goes low byte first. Numbers in the data are big-endian.
- The frames go on a TCP connection with no header. The length of an answer follows from the request.
- The unit id is 1.
- Addresses start at 0. Relay 1 is coil 0.
- The board never sends data by itself. The app polls it every 2 s.

| What | Function | Address | Data | Answer |
|---|---|---|---|---|
| Read the relays | 01 | 0 | quantity = relay count | byte count, then a bit per relay, bit 0 = relay 1 |
| Read the inputs, (B) and (C) only | 02 | 0 | quantity = 8 | byte count, then a bit per input |
| Switch one relay | 05 | relay − 1 | FF00 on, 0000 off | the request, echoed |
| Pulse one relay ("flash on") | 05 | 0x0200 + relay − 1 | the time in 100 ms, 0001 to 7FFF | the request, echoed |
| Read the relay modes, (B) and (C) only | 03 | 0x1000 | quantity = 8 | 16 bytes, a register per relay |
| Set one relay's mode, (B) and (C) only | 06 | 0x1000 + relay − 1 | the mode, 0000 to 0003 | the request, echoed |
| Set all relay modes, (B) and (C) only | 10 | 0x1000 | 8 registers | address and quantity |

A pulse turns the relay on, and the board turns it off again by itself after the time, also when the app stops in
between. So all five models pulse for 0.1 s to 3276.7 s, in steps of 0.1 s (`WaveshareModels.Pulse`). The board also
has "flash off" at 0x0400 + relay − 1, which the app does not use.

A refused request gets an exception answer: the function code with 0x80 added, then an exception code.
`WaveshareRefusedException` has the code. The board also takes 5500 (toggle) at function 05, and address 0x00FF for
all relays. The app does not use them.

Frames from the wiki, unit 1:

| Frame | Meaning |
|---|---|
| `01 01 00 00 00 08 3D CC` | Read 8 relays |
| `01 01 01 41 91 B8` | Answer: relays 1 and 7 on |
| `01 05 00 00 FF 00 8C 3A` | Turn on relay 1 (the answer is the same frame) |
| `01 05 00 00 00 00 CD CA` | Turn off relay 1 |
| `01 05 02 00 00 07 8D B0` | Pulse relay 1 for 0.7 s (the answer is the same frame) |
| `01 02 00 00 00 08 79 CC` | Read 8 inputs |
| `01 02 01 00 A1 88` | Answer: all inputs off |
| `01 85 03 02 91` | Exception answer to function 05, code 3 |
| `01 03 10 00 00 08 40 CC` | Read the 8 relay modes (§5.3.7) |
| `01 03 10 00 00 … 00 E4 59` | Answer: 16 bytes of 0, every relay in normal mode (§5.3.7) |
| `01 06 10 00 00 01 4C CA` | Set relay 1 to linkage mode (the answer is the same frame, §5.3.8) |
| `01 06 10 01 00 02 5D 0B` | Set relay 2 to toggle mode (§5.3.8) |

`WaveshareModbusTests` checks these frames byte for byte.

## The app's client

`WaveshareClient` keeps one TCP connection and sends one request at a time. Each request waits for its answer.

- Each request and each connection attempt has 5 s on `TimeProvider`.
- The client reads only the length that the request expects, at most 256 bytes.
- An answer with a wrong unit, function, length, echo or CRC is unreadable. Then the client closes the connection,
  because the stream can be out of step. The next request opens a new connection.
- A timeout also closes the connection. The next request opens a new one, so one lost answer doesn't lose the board.
- When the connection was open from before (not one just opened while connecting) and turns out broken (the board or the
  network can close a connection that is idle), the client sends the request once more on a new connection. It does this
  only when no byte of the answer came. Each request the client sends has the same result when done twice: reading the
  relays, the inputs or the modes, switching one relay, starting its pulse again, and setting one relay's mode to
  normal.
- Bytes that wait before a request were not asked for. The client drops them and shows them on the Debug screen.
- `BoardPoller` does the polls, the reconnects and the count of failures, as for the VM204 and the VM208.

A command (ON, OFF or a pulse) switches the relays in the mask one at a time with function 05:

- The client sends a request for every relay in the mask, also when the board refuses one.
- A request with no answer, also on a new connection, stops the rest of the mask: the board is gone.
- When the board takes no relay, the command throws "The Waveshare board didn't take the command: …".
- Otherwise it returns the relays the board confirmed. A refused relay, or one whose answer couldn't be read, isn't among them.
- When a request gets no answer, the command throws "The Waveshare board didn't answer; the command may not have been
  carried out."
- The poller reads the board at once after each of these two failures. It counts a request with no answer as a failed
  poll, and also a command with an answer that cannot be read. When the board refused every relay with an exception
  answer, the board answered: the command is a refusal (`BoardRefusedException`), which does not count.
- After the switches, the client reads the relays and the inputs in the same command. The poller takes that as the
  new state.
- When that read gets an unreadable answer or no answer, the command returns the relays that the board took. The client
  keeps the last state it read and reads again at once. The echo of function 05 is not proof that a relay switched: a
  relay in linkage mode echoes and stays as it is.

`WaveshareClient.PulseAsync` and `WaveshareBoard.PulseAsync` throw `ArgumentOutOfRangeException` for a length outside
0.1 s to 3276.7 s, or not in steps of 0.1 s, before anything is sent.

`WaveshareBoard`, `WaveshareConnection` and `WaveshareTraffic` are the adapters for the app.
`Features/Waveshare` in `Contactor.Web` registers them. `WaveshareTraceText` puts each frame in plain words. The polls
and the answers that change nothing are routine.

## Settings

The `Waveshare` section applies to all five models:

```json
"Waveshare": {
  "Host": "",
  "Port": 4196,
  "PollSeconds": 2
}
```

`Waveshare:ResetRelayModes` is gone (issue #497); one left in an installation's settings is ignored.

When `Host` is empty, the setup guide asks for the address.

## The simulator

`WaveshareSimulator` simulates each of the five models on a local port. `Contactor.Simulator` offers port 4196.

- It answers functions 01, 02, 03, 05, 06 and 10 as described above. A relay in linkage mode echoes function 05 and
  stays as it is, as the wiki's FAQ says the board does.
- A pulse ("flash on") turns the relay off again after its time, on the simulator's `TimeProvider`. The next poll shows
  it. A later function 05 for the relay ends its pulse; a later pulse starts it again. A relay in linkage mode doesn't
  pulse.
- It gives an exception answer for a function it does not know (code 1), an address it does not have (code 2), or a
  value or quantity it does not take (code 3).
- It gives no answer to a frame with a bad CRC or for another unit, as on a serial Modbus line.
- On the (B) and (C) it has the mode registers. A relay in mode 1, 2 or 3 follows its input when the input changes.
- Relays, inputs and each relay's mode (`ISimulatedBoardRelayModes`, a select per relay on the simulator's page) can be
  set "at the board", and connections can be dropped or refused.
- For the tests, `NoAnswer` stops all answers and `FailRelays` refuses function 05 for some relays with code 4.
- A power cut (`ISimulatedBoardPower`): the wiki doesn't say whether the relays come back off or as they were, nor whether the relay modes stay (see "To check on a real board"). The simulator brings every relay back off and keeps the modes.

## Settings on the board that switch relays without the app

### Relay modes on the (B) and (C)

On the (B) and the (C), each relay has a control mode in the registers 0x1000 to 0x1007:

- 0: normal. Only Modbus commands switch the relay.
- 1: linkage. The relay follows its input, and commands do not switch it.
- 2: toggle. Each pulse on the input flips the relay.
- 3: edge. Each change of the input flips the relay.

Relay n's mode is tied to input n ("corresponding input channel", §5.3.7). In modes 1 to 3, an input switches a relay
without the app; in mode 1 the board also ignores the app's commands for it: it echoes function 05 and the relay stays
as it is (§1.5: "The linked mode output is controlled only by input and does not respond to commands", and the FAQ).

These modes are the board's own rules (issue #497, `docs/ARCHITECTURE.md`, "The board's own rules"). The app never
changes them on its own:

1. The client reads the 8 mode registers with one function 03 at the first reading on each new connection, and then
   every minute (`WaveshareClient.ModesInterval`). The modes go in the status (`WaveshareStatus.Modes`).
2. A read the board refuses, whose answer can't be read, or that holds a mode other than 0 to 3, gives no modes (null):
   the app then knows no rules for the board, and guesses none. A read with no answer fails the poll, as any read does.
   The relays the last read that worked showed in linkage mode stay unavailable meanwhile (`WaveshareStatus.Linkage`,
   `BoardState.KeptUnavailable`, issue #567), also on a new connection: the board still ignores commands for them. Only a
   read that works changes them, and a new address or model starts with none.
3. The poller compares only the relays and the inputs, so a change in the modes alone is raised as
   `OwnRulesChanged`, never as `StateChanged`: the safety services aren't woken by it.
4. `WaveshareBoard` maps mode 1 to `InputLinkage`, 2 to `InputToggle` and 3 to `InputEdge`; 0 is no rule.
5. Only a person's press sets a relay back to normal (`TurnOffModesAsync`, through `TurnOffOwnRulesAsync`): one function
   06 per relay, writing 0 to `0x1000 + relay − 1`, never function 10 over all 8, so the other relays' modes stay as
   they are. Every relay asked for is sent, also after one is refused; the relays the board took are returned, and the
   command throws when it took none. The modes are read back in the same command.

A relay in linkage mode is "unavailable to Contactor": the app shows it apart, with "Reset", and never sends it a
command. Until issue #497 the client wrote all 8 modes to normal at each connection (`Waveshare:ResetRelayModes`); the
events it logged then (`board-relay-modes-reset`, `board-relay-modes-not-reset`) still read on the Events screen.

## To check on a real board

> **Warning: check the first five points first, before anything is connected to the relays.**

- The relay states after a power cut: all off, or the last states.
- If the mode registers stay after a power cut, and how a relay in linkage mode answers function 05.
- Whether writing mode 0 to a relay switches it: does a relay in linkage mode that is on stay on when it is reset, or
  does it go off?
- The input bit order (bit 0 = input 1), and if a closed input reads 1.
- That function 06 on one mode register leaves the other 7 as they are, and that the board echoes it as in §5.3.8.
- The pulse ("flash on"): its address on each model (0x0200 + relay − 1), the longest time (0x7FFF), that the board
  echoes the request, what a function 05 or a second pulse for the relay does to a pulse that runs, and what a relay in
  linkage mode does with it.
- The factory address and port: 192.168.1.200:4196, a static address, mode "Modbus RTU over TCP".
- Which exception codes the board sends for a coil out of range, a quantity above the relay count, and a function 05
  value other than FF00, 0000 or 5500.
- How many TCP clients the board accepts at once, and if a second client closes the first.
- That the board never sends data that the app did not ask for.
- That the 16CH has no inputs, and that the 16CH and the 30CH answer as above.
- How the board handles a poll every 2 s for days, and if it closes a connection that is idle, and after how long.
