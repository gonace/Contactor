# The Waveshare Modbus PoE ETH relays (`Contactor.Boards.Waveshare`)

How the app talks to these boards. What every board shares (`IRelayBoard`, `IBoardConnection`, the board catalogue) is
in `docs/ARCHITECTURE.md`.

This project connects Contactor to the Waveshare Modbus PoE ETH relay boards. It has the Modbus codec, the client, the
adapters for the app and a simulator. It has no web or database code.

The facts below come from Waveshare's wiki, read through search extracts. Nobody has tested them on a real board yet.
The list in "To check on a real board" shows what is not known.

## Models

| Catalogue id | Name | Relays | Inputs |
|---|---|---|---|
| `waveshare-8` | Waveshare Modbus PoE ETH Relay | 8 | 0 |
| `waveshare-8b` | Waveshare Modbus PoE ETH Relay (B) | 8 | 8 |
| `waveshare-8c` | Waveshare Modbus PoE ETH Relay (C) | 8 | 8 |
| `waveshare-16` | Waveshare Modbus PoE ETH Relay 16CH | 16 | 0 |
| `waveshare-30` | Waveshare Modbus PoE ETH Relay 30CH | 30 | 0 |

All five use port 4196 and have no login. `WaveshareModels` has the five models. The 16CH and the 30CH come from the
wiki only. The wiki's table for the 16CH is not clear about inputs, so the app gives it no inputs.

Wiki pages:

- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay
- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay_(B)
- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay_(C)
- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay_16CH
- https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay_30CH

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
| Read the relay modes, (B) and (C) only | 03 | 0x1000 | quantity = 8 | 16 bytes, a register per relay |
| Write the relay modes, (B) and (C) only | 10 | 0x1000 | 8 registers | address and quantity |

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
| `01 02 00 00 00 08 79 CC` | Read 8 inputs |
| `01 02 01 00 A1 88` | Answer: all inputs off |
| `01 85 03 02 91` | Exception answer to function 05, code 3 |

`WaveshareModbusTests` checks these frames byte for byte.

## Client

`WaveshareClient` keeps one TCP connection and sends one request at a time. Each request waits for its answer.

- Each request and each connection attempt has 5 s on `TimeProvider`.
- The client reads only the length that the request expects, at most 256 bytes.
- An answer with a wrong unit, function, length, echo or CRC is unreadable. Then the client closes the connection,
  because the stream can be out of step. The next request opens a new connection.
- A timeout also closes the connection.
- Bytes that wait before a request were not asked for. The client drops them and shows them on the Debug screen.
- `BoardPoller` does the polls, the reconnects and the count of failures, as for the VM204 and the VM208.

A command switches the relays in the mask one at a time with function 05:

- The client sends a request for every relay in the mask, also when the board refuses one.
- A request with no answer stops the rest of the mask.
- When the board takes no relay, the command throws "The Waveshare board didn't take the command: …".
- Otherwise it returns the relays the board confirmed. A refused relay, or one whose answer couldn't be read, isn't among them.
- When a request gets no answer, the command throws "The Waveshare board didn't answer; the command may not have been
  carried out."
- The poller reads the board at once after each of these two failures. It counts a request with no answer as a failed
  poll, and also a command with an answer that cannot be read. When the board refused every relay with an exception
  answer, the board answered: the command is a refusal (`BoardRefusedException`), which does not count.
- After the switches, the client reads the relays and the inputs in the same command. The poller takes that as the
  new state.
- When that read gets an unreadable answer, the client keeps the last state it read and reads again at once. The echo
  of function 05 is not proof that a relay switched: a relay in linkage mode echoes and stays as it is.

`WaveshareBoard`, `WaveshareConnection` and `WaveshareTraffic` are the adapters for the app.
`Features/Waveshare` in `Contactor.Web` registers them. `WaveshareTraceText` puts each frame in plain words. The polls
and the answers that change nothing are routine.

## Relay modes on the (B) and (C)

On the (B) and the (C), each relay has a control mode in the registers 0x1000 to 0x1007:

- 0: normal. Only Modbus commands switch the relay.
- 1: linkage. The relay follows its input, and commands do not switch it.
- 2: toggle. Each pulse on the input flips the relay.
- 3: edge. Each change of the input flips the relay.

In modes 1 to 3, an input switches a relay without the app. The app sees that only as a change from outside, and a
group's interlock cannot stop it before it happens. So the client sets all relays to normal mode on each connection:

1. It reads the 8 mode registers with function 03.
2. When all are 0, it writes nothing and logs nothing.
3. When one is not 0, it writes 0 to all 8 with one function 10 request.
4. When the board refuses the read or its answer cannot be read, the modes are unknown. The client writes 0 to all 8
   all the same.
5. When the write succeeds, the client raises `RelayModesReset` with the modes it found, or none when they were
   unknown. `WaveshareRelayModesService` logs the action `board-relay-modes-reset` with Actor `system` and the details
   `{"modes":[…]}` or `{"modes":"unknown"}`.
6. When the board refuses the write or its answer cannot be read, the client raises `RelayModesNotReset`.
   `WaveshareRelayModesService` logs the action `board-relay-modes-not-reset` with Actor `system` and the reason. The
   Events screen shows it as a red row: "The board couldn't have its relays set to normal mode, so its inputs may switch
   them". The board still works. This happens at most once per connection, not on each poll.
7. When the board does not answer the read or the write, the connection fails and the poller tries again.

The client reads first because the board can keep the modes in its flash memory. A write on each reconnect could wear
the flash, and it would fill the Events screen. Function 10 writes all 8 registers in one request and one answer.

The client checks the modes only when it connects. A mode set with the board's own tool while the app is connected
stays until the next connection.

Set `Waveshare:ResetRelayModes` to `false` to keep the modes.

## Settings

The `Waveshare` section applies to all five models:

```json
"Waveshare": {
  "Host": "",
  "Port": 4196,
  "PollSeconds": 2,
  "ResetRelayModes": true
}
```

When `Host` is empty, the setup guide asks for the address.

## Simulator

`WaveshareSimulator` simulates each of the five models on a local port. `Contactor.Simulator` offers port 4196.

- It answers functions 01, 02, 03, 05, 06 and 10 as described above.
- It gives an exception answer for a function it does not know (code 1), an address it does not have (code 2), or a
  value or quantity it does not take (code 3).
- It gives no answer to a frame with a bad CRC or for another unit, as on a serial Modbus line.
- On the (B) and (C) it has the mode registers. A relay in mode 1, 2 or 3 follows its input when the input changes.
- Relays and inputs can be set "at the board", and connections can be dropped or refused.
- For the tests, `NoAnswer` stops all answers and `FailRelays` refuses function 05 for some relays with code 4.

## To check on a real board

- The factory address and port: 192.168.1.200:4196, a static address, mode "Modbus RTU over TCP".
- The relay states after a power cut: all off, or the last states.
- Which exception codes the board sends for a coil out of range, a quantity above the relay count, and a function 05
  value other than FF00, 0000 or 5500.
- The input bit order (bit 0 = input 1), and if a closed input reads 1.
- How many TCP clients the board accepts at once, and if a second client closes the first.
- That the board never sends data that the app did not ask for.
- If the mode registers stay after a power cut, and how a relay in linkage mode answers function 05.
- That the 16CH has no inputs, and that the 16CH and the 30CH answer as above.
- How the board handles a poll every 2 s for days.
