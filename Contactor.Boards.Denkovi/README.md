# The Denkovi boards (`Contactor.Boards.Denkovi`)

How the app talks to the Denkovi boards. What every board shares (`IRelayBoard`, `IBoardConnection`, the board
catalogue) is in `docs/ARCHITECTURE.md`.

This project connects Contactor to the Denkovi relay boards. Today it has one: the smartDEN IP-16R-MT. It has the Modbus
TCP codec, the client, the adapters for the app and a simulator. It has no web or database code. The types of each board
have the board's name as a prefix (`SmartDen*`). `DenkoviBrand` has the brand that they share.

## Sources

- Denkovi's "smartDEN IP-16R-XX User Manual", 28 September 2020
  (https://denkovi.com/Documents/smartDen-IP-16R/Current-Version/UserManual.pdf):
  - section 8.2: "Save Outputs";
  - section 8.6: the pulse mode of each relay ("Pulse, ms (x100)");
  - section 8.9: the week schedule;
  - section 8.13: the Modbus TCP settings (port 502, idle timeout 5 min, one TCP connection at a time);
  - section 10.2: the Modbus TCP protocol, tables 10.6 to 10.29.
- The product page: https://denkovi.com/smartden-modbus-tcp-ip-ethernet-16-relay-board

Nobody has tested the board with the app yet. The list in "To check on a real board" shows what is not known.

## Model

| Catalogue id | Name | Relays | Inputs |
|---|---|---|---|
| `denkovi-smartden-ip-16r-mt` | Denkovi smartDEN IP-16R-MT | 16 | 0 |

The board uses port 502 and has no login: Modbus has none, and the password of the board's web page doesn't guard
Modbus. The other smartDEN IP-16R models (SNMP, MQTT) don't speak Modbus TCP, and the app doesn't support them.

## Protocol

Modbus TCP, section 10.2 of the manual.

- A frame is the MBAP header (7 bytes) and the PDU. The header is the transaction id, the protocol id (always 0) and the
  length (2 bytes each, big-endian), then the unit id (1 byte). The length counts the unit id and the PDU.
- The PDU is the function code and its data. Numbers are big-endian. There is no CRC.
- The app sends unit id 0, as the manual's examples do. The answer must carry the same transaction id and unit id.
- Addresses start at 0. Relay 1 is coil 0, relay 16 is coil 15.
- The board never sends data by itself. The app polls it every 2 s.

| What | Function | Address | Data | Answer | Manual |
|---|---|---|---|---|---|
| Read the relays | 01 | 0 | quantity = 16 | byte count 2, then a bit per relay, bit 0 = relay 1 | 10.2.1.1, tables 10.8–10.12 |
| Switch one relay | 05 | relay − 1 | FF00 on, 0000 off | the request, echoed | "Write Single Coil" (also numbered 10.2.1.1), tables 10.13–10.15 |
| Pulse one relay | 06 | 0x0000 + relay − 1 | the length in 100 ms, 0001 to FFFF | the request, echoed | 10.2.1.4, tables 10.7, 10.24–10.26 |

A pulse turns the relay on, and the board turns it off again by itself after the length, also when the app stops in
between. So the board pulses for 0.1 s to 6553.5 s, in steps of 0.1 s (`SmartDenModels.Pulse`).

The board also takes function 05 with FF02 (flip the relay), 0F (write several relays), 03 and 10 (configuration
registers from 0x6000). The app does not use them.

A refused request gets an exception answer: the function code with 0x80 added, then an exception code (1: unknown
function, 2: no such address, 3: a value it doesn't take). `SmartDenRefusedException` has the code.

Frames from the manual:

| Frame | Meaning |
|---|---|
| `00 01 00 00 00 06 00 01 00 00 00 03` | Read relays 1 to 3 (table 10.8) |
| `00 01 00 00 00 04 00 01 01 05` | Answer: relays 1 and 3 on (table 10.10) |
| `00 01 00 00 00 06 00 01 00 03 00 0D` | Read relays 4 to 16 (table 10.9) |
| `00 01 00 00 00 05 00 01 02 59 0B` | Answer to it (table 10.11, see below) |
| `00 01 00 00 00 03 00 81 02` | Exception answer to function 01, code 2 (table 10.12) |
| `00 02 00 00 00 06 00 05 00 01 FF 00` | Turn on relay 2 (table 10.13; the answer is the same frame, table 10.14) |
| `00 05 00 00 00 06 00 06 00 09 00 1E` | Pulse relay 10 for 3 s (table 10.24; the answer is the same frame, table 10.25) |

Table 10.11 gives the length 0x0004, but the unit id, the function, the byte count and two bytes of data are 5. The app
takes 5, as Modbus TCP says. `SmartDenModbusTests` checks these frames byte for byte.

## Client

`SmartDenClient` keeps one TCP connection and sends one request at a time. Each request waits for its answer.

- Each request and each connection attempt has `SmartDenClient.RequestTimeout` (5 s) on `TimeProvider`.
- The client reads the header first, and then only the length that the request expects: at most 260 bytes.
- An answer with a wrong transaction id, protocol, unit, function, length, byte count or echo is unreadable. Then the
  client closes the connection, because the stream can be out of step. The next request opens a new connection.
- A timeout also closes the connection, and so does a poll that a command cancels. The next request opens a new one, so
  one lost answer doesn't lose the board.
- Bytes that wait before a request were not asked for. The client drops them and shows them on the Debug screen.
- `BoardPoller` does the polls, the reconnects and the count of failures, as for the other polled boards.

A command (ON, OFF or a pulse) switches the relays in the mask one at a time:

- The client sends a request for every relay in the mask, also when the board refuses one or its answer can't be read.
  "All off" and a group's interlock depend on every OFF being sent.
- A request with no answer stops the rest of the mask.
- An empty mask sends nothing and returns no relays.
- When the board takes no relay, the command throws "The smartDEN board didn't take the command: …"
  (`BoardNotConnectedException`). A refusal never leaves the client as `BoardRefusedException`.
- Otherwise it returns the relays the board confirmed. A refused relay, or one whose answer couldn't be read, isn't among
  them.
- When a request gets no answer, the command throws "The smartDEN board didn't answer; the command may not have been
  carried out."
- The poller reads the board at once after each of these two failures. It counts a request with no answer as a failed
  poll, and also a command with an answer that cannot be read. When the board refused every relay with an exception
  answer, the board answered: that does not count.
- After the switches, the client reads the relays in the same command. The poller takes that as the new state.
- When that read gets an unreadable answer, the client keeps the last state it read and reads again at once. An echo is
  not proof that a relay switched.

`SmartDenClient.PulseAsync` and `SmartDenBoard.PulseAsync` throw `ArgumentOutOfRangeException` for a length outside
0.1 s to 6553.5 s, or not in steps of 0.1 s, before anything is sent.

`SmartDenBoard`, `SmartDenConnection` and `SmartDenTraffic` are the adapters for the app. `Features/Denkovi` in
`Contactor.Web` registers them. `SmartDenTraceText` puts each frame in plain words. The polls and the answers that
change nothing are routine.

## What the board does by itself

The app reads the board's relays on every poll, so the board stays the source of truth. These board settings switch
relays without the app. The app sees each switch only as a change from outside:

- "Save Outputs" (section 8.2): the board keeps the relay states in its memory and sets them again after a restart.
- The week schedule (sections 8.9 and 10.2, registers 0x6100 to 0x671D): up to 30 rows that switch relays at set times.
- The pulse mode of each relay (sections 8.6 and 10.2, registers 0x6000 to 0x600F): when it isn't 0, the board turns
  the relay off again after that time each time it goes on, also after an ON from the app.

The app doesn't read or change these settings. Set them on the board's web page: the week schedule off, and the pulse
mode 0 for every relay that the app switches.

## Settings

The `SmartDen` section:

```json
"SmartDen": {
  "Host": "",
  "Port": 502,
  "PollSeconds": 2
}
```

When `Host` is empty, the setup guide asks for the address. Modbus TCP has no encryption and no login. Keep the board
on a trusted network, and never open port 502 to the internet.

## Simulator

`SmartDenSimulator` simulates the board on a local port. `Contactor.Simulator` offers port 8502, because port 502 needs
rights that a developer doesn't have.

- It answers functions 01, 05 (FF00, 0000 and FF02), 06 at the pulse registers and 0F, as described above. Its answer
  carries the request's transaction id and unit id, and a byte for each 8 relays asked for.
- A pulse turns the relay off again after its length, on the simulator's `TimeProvider`. The next poll shows it. A later
  function 05 for the relay ends its pulse, and a later pulse starts it again.
- It gives an exception answer for a function it does not know (code 1), an address it does not have (code 2), and a
  value, quantity or PDU length it does not take (code 3). It has no configuration registers: functions 03 and 10 get
  code 2.
- It gives no answer to a frame with another protocol id.
- A header with a length no Modbus frame has (below 2 or above 254) closes the connection, as nothing after it can be
  read in step. A client that goes away in the middle of a frame closes only its own connection. The simulator takes
  the next connection as usual.
- It takes one connection at a time, as the manual says. A new connection closes the one before. The manual doesn't say
  what the board does: see below.
- Relays can be switched "at the board", and connections can be dropped or refused.
- For the tests, `NoAnswer` stops all answers and `FailRelays` refuses functions 05, 06 and 0F for some relays with code 4.

## To check on a real board

- The factory address, and that Modbus TCP is on at port 502 (section 8.13: "Enable Modbus-TCP").
- That unit id 0 is answered, and which unit ids the board takes.
- What the board does with a second TCP connection while one is open: does it refuse the new one, or close the old one?
  If it refuses the new one, a connection that the app lost without the board's knowledge blocks the app until the
  board's idle timeout (5 min).
- That the idle timeout never closes the app's connection: the app polls every 2 s.
- The coil bit order (bit 0 = relay 1, bit 15 = relay 16), and the length the board sends for a read of 16 relays (5, or
  4 as table 10.11 says).
- That function 05 echoes the request, and that the read right after it shows the new state.
- The pulse (function 06 at 0x0000 to 0x000F): that it turns the relay on and then off, the unit of 100 ms, the limits
  (1 to 65535), and what a function 05 or a second pulse does to a pulse that runs.
- Which exception codes the board sends for a coil out of range, a quantity above 16, and a function 05 value other than
  FF00, 0000 or FF02.
- The relay states after a power cut, with "Save Outputs" on and off. The app must turn nothing off at start.
- How the board handles a poll every 2 s for days.
