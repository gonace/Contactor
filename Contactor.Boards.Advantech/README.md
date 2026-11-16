# The Advantech ADAM modules (`Contactor.Boards.Advantech`)

How the app talks to the Advantech ADAM modules. What every board shares (`IRelayBoard`, `IBoardConnection`, the board
catalogue) is in `docs/ARCHITECTURE.md`.

This project connects Contactor to the ADAM-6060 and the ADAM-6066, two modules of Advantech's ADAM-6000 series. It has
the codec, the client, the adapters for the app and a simulator. It has no web or database code. The types have the
prefix `Adam`. `AdvantechBrand` has the brand.

Nobody has tested a module with the app yet. The list in [To check on a real module](#to-check-on-a-real-module) shows
what is not known. Above all, the manual doesn't settle which address goes in the frame for its coil 00017: see
[Addresses](#addresses).

## Sources

- Advantech's "ADAM-6000 Series User Manual", Edition 12, November 2022, part no. 2003600006
  (https://advdownload.advantech.com/productfile/Downloadfile4/1-2B6FKTG/ADAM-6000_User_Manaul_Ed.12-FINAL.pdf):
  - section 5.5, pages 42–44: the ADAM-6060, 6 digital inputs and 6 relays (Form A), 120 VAC 0.5 A and 30 VDC 1 A;
  - section 5.6, pages 45–46: the ADAM-6066, 6 digital inputs and 6 power relays (Form A), 250 VAC 5 A and 30 VDC 3 A;
  - page 58: the Network tab, with the "host idle time (timeout)";
  - page 63: the Access Control tab, which limits control to listed IP or MAC addresses;
  - page 64: the Modbus Address tab: the module's Modbus addresses can be changed;
  - page 76: the fail-safe values and the Communication WDT;
  - pages 80–83: the four DO modes: DO, Pulse Output, Low-to-High Delay and High-to-Low Delay;
  - page 84: P2P, and chapter 8 (pages 149–179): GCL;
  - page 90: the web page's login (`root`, `00000000`);
  - section 7.3, pages 96–100: the Modbus TCP frame and function codes 01 to 16, with examples;
  - appendix B.1, page 186, figure B.1: a whole frame, header included, and "unit identifier - 1 (ADAM-6000 always 1)";
  - appendix B.2.8, pages 217–218: the ADAM-6060/6066 map: the inputs at 00001 to 00006, the relays ("DO value") at
    00017 to 00022, and the pulse registers at 40013 to 40060.
- Modbus TCP itself, for what the manual doesn't show: the MBAP header's fields, the exception answers, and the rule
  that a coil numbered n in a 0X table is at address n − 1 in the frame.

Edition 9 (August 2018) has the same examples and map.

## Models

| Catalogue id | Name | Relays | Inputs | Contacts |
|---|---|---|---|---|
| `advantech-adam-6060` | Advantech ADAM-6060 | 6 | 6 | Form A, 120 VAC 0.5 A, 30 VDC 1 A |
| `advantech-adam-6066` | Advantech ADAM-6066 | 6 | 6 | Form A, 250 VAC 5 A, 30 VDC 3 A |

Both have the same map and are switched the same way; only their relays' ratings differ. They use port 502 and have no
login: Modbus has none, and the module's password (`00000000` from the factory) guards only its web page and its
configuration. The ADAM-6000 manual names no port; port 502 is Modbus TCP's own, and the ADAM-6200 manual lists
"TCP Modbus TCP 502".

The modules have no pulse in the app. Their Pulse Output DO mode and the pulse registers (40013 to 40060: the low and
high widths in 0.1 ms, an absolute and an incremental count) make a pulse train, at most 3 Hz, not one pulse of a given
length.

## Protocol

Modbus TCP, section 7.3 and appendix B.

- A frame is the "command head" (the MBAP header, 7 bytes with the unit id) and the "command body" (the PDU). The header
  is the transaction id, the protocol id (always 0) and the length (2 bytes each, big-endian), then the unit id
  (1 byte). The length counts the unit id and the PDU.
- The PDU is the function code and its data. Numbers are big-endian. There is no CRC.
- The app sends unit id 1: "ADAM-6000 always 1" (figure B.1). The answer must carry the same transaction id and unit id.
- Bit 0 of a read's data is the first coil or input asked for, as the manual's FC 15 example maps its bits (page 100).
- The module never sends data by itself (the app doesn't use P2P or the data stream). The app polls it every 2 s.

| What | Function | Address in the frame | Data | Answer | Manual |
|---|---|---|---|---|---|
| Read the relays | 01 "Read coil status" | 0x0010 (coil 00017) | quantity = 6 | byte count 1, then a bit per relay, bit 0 = relay 1 | 7.3.2, B.2.8 |
| Read the inputs | 02 "Read input status" | 0x0000 (00001) | quantity = 6 | byte count 1, then a bit per input, bit 0 = input 1 | 7.3.2, B.2.8 |
| Switch one relay | 05 "Force single coil" | 0x0010 + relay − 1 (coil 00017 + relay − 1) | FF00 on, 0000 off | the request, echoed | 7.3.2, B.2.8 |

The app switches one relay with one function 05, so it never writes all relays at once: a change at the module in
between is never written over. The module also takes function 0F (several coils at once), 03, 04, 06 and 10 (registers)
and 08 (loopback). The app uses none of them. Function 05 also takes FFFF, "the forced value to be released"; the
manual doesn't say what that does to a relay, and the app never sends it.

The manual shows no exception answer. The app reads one as Modbus TCP defines it: the function code with 0x80 added,
then an exception code (1: unknown function, 2: no such address, 3: a value it doesn't take, 4: it failed).
`AdamRefusedException` has the code.

### Addresses

The manual gives each address as a number in a 0X table (00001, 00017), not as the bytes in a frame. Its examples don't
agree with each other on how such a number goes into the frame:

| Example (section 7.3.2) | Manual's address | In the frame |
|---|---|---|
| FC 01, "Read Coils 1~8 (Addresses 00017~00024)" | 00017 | `00 17` (23) |
| FC 15, "starting at Address 00017 ("11" in hex)" | 00017 | `00 11` (17) |
| FC 02, "Addresses 00001~00008" | 00001 | `00 01` (1) |
| FC 05, "Force Coil 3 (Address 00003)" | 00003 | `00 03` (3) |
| Figure B.1, "Start address: 40001" | 40001 | `00 01` (1) |

So the same coil 00017 is 23 in one example and 17 in another, and neither is what Modbus says. The manual says the
addresses follow "the Modbus/TCP standard" (section 5.5.3), and in Modbus a coil or register numbered n in a
0X or 4X table is at address n − 1 in the frame. The app takes Modbus's rule: relay 1 is coil 00017, address 0x0010, and
input 1 is 00001, address 0x0000 (`AdamApi.RelayAddress` and `AdamApi.InputAddress`, the one place to change it). If
a real module takes the examples' 17 instead, the app's relay 1 is relay 2 of the module's map. This must be checked on
a real module before anyone uses it for a machine.

The Modbus Address tab (page 64) lets the installer move these addresses. The app takes the factory map of appendix
B.2.8: leave the tab as it is.

### Frames from the manual

The manual gives frames for the command body and one whole frame. `AdamModbusTests` checks each of them byte for byte:

| Frame | Meaning |
|---|---|
| `00 00 00 00 00 06 01 04 00 01 00 02` | Read 40001~40002 of an ADAM-6017 (figure B.1, the whole frame) |
| `01 01 00 17 00 08` | Read coils 1~8 at 00017~00024 (FC 01) |
| `01 01 01 42` | Its answer: 42, coils 2 and 7 on (the manual: "Coils 2~7 are on") |
| `01 02 00 01 00 08` | Read inputs at 00001~00008 (FC 02) |
| `01 02 01 60` | Its answer: 60, inputs 6 and 7 on in Modbus's bit order (the manual: "Inputs 2 and 3 are on", read the other way round) |
| `01 05 00 03 FF 00` | Force coil 3 on (FC 05); the answer is the same bytes |

Figure B.2, the answer to figure B.1, gives the length 6 for a unit id, a function, a byte count and 4 bytes of data,
which are 7. The app takes only the length Modbus TCP gives.

The app's own frames, e.g. `00 07 00 00 00 06 01 01 00 10 00 06` (read the relays) and
`00 03 00 00 00 06 01 05 00 15 FF 00` (turn on relay 6), are in `AdamModbusTests` too.

## Client

`AdamClient` is the smartDEN's client (`Contactor.Boards.Denkovi`) with the ADAM's map. It keeps one TCP connection and
sends one request at a time. Each request waits for its answer.

- Each request and each connection attempt has `AdamClient.RequestTimeout` (5 s) on `TimeProvider`.
- A poll reads the relays (FC 01), then the inputs (FC 02). Both must be answered for the module to count as connected.
- The client reads the header first, and then only the length that the request expects: at most 260 bytes.
- An answer with a wrong transaction id, protocol, unit, function, length, byte count or echo is unreadable. Then the
  client closes the connection, because the stream can be out of step. The next request opens a new connection.
- A timeout also closes the connection. The next request opens a new one, so one lost answer doesn't lose the module.
- When the connection was open from before and turns out broken (the module may close a connection it finds idle), the
  client sends the request once more on a new connection. Reading or switching one relay has the same result when done
  twice.
- A poll that a command cancels keeps the connection. The poll's answer can still come. The next request reads it and
  drops it by its transaction id ("Skipped a late answer nobody waited for"). When part of that answer was read
  already, or 8 answers are still to come, the client opens a new connection.
- Bytes that wait before a request were not asked for. The client drops them and shows them on the Debug screen.
- `BoardPoller` does the polls, the reconnects and the count of failures, as for the other polled boards.

A command (ON or OFF) switches the relays in the mask one at a time:

- The client sends a request for every relay in the mask, also when the module refuses one or its answer can't be read.
  "All off" and a group's interlock depend on every OFF being sent.
- A request with no answer, also on a new connection, stops the rest of the mask: the module is gone.
- An empty mask sends nothing and returns no relays.
- When the module takes no relay, the command throws "The ADAM module didn't take the command: …"
  (`BoardNotConnectedException`). A refusal never leaves the client as `BoardRefusedException`.
- Otherwise it returns the relays the module confirmed. A refused relay, or one whose answer couldn't be read, isn't
  among them.
- When a request gets no answer, the command throws "The ADAM module didn't answer; the command may not have been
  carried out."
- The poller reads the module at once after each of these two failures. It counts a request with no answer as a failed
  poll, and also a command with an answer that cannot be read. When the module refused every relay with an exception
  answer, the module answered: that does not count.
- After the switches, the client reads the relays and inputs in the same command. The poller takes that as the new
  state.
- When that read gets an unreadable answer or no answer, the command returns the relays that the module took. The
  client keeps the last state it read and reads again at once. An echo is not proof that a relay switched.

`AdamBoard.PulseAsync` throws `NotSupportedException`: the modules have no pulse in the app.

`AdamBoard`, `AdamConnection` and `AdamTraffic` are the adapters for the app. `Features/Advantech` in `Contactor.Web`
registers them (`AdamBoards`). `AdamTraceText` puts each frame in plain words. The polls and the answers that change
nothing are routine.

## Inputs

The app shows an input as on when the module's DI value is 1. On a wet contact that is 10 to 30 VDC. On a dry contact
the module reads a closed contact as 0 and an open one as 1 (pages 42 and 45), so a closed switch shows as off. The
module can invert its inputs ("Support inverted digital input status"); the app shows the value the module gives.

## What the module does by itself

The app reads the relays on every poll, so the module stays the source of truth. These settings switch relays without
the app. The app sees each switch only as a change from outside:

- The DO modes (pages 80–83): Pulse Output makes a pulse train; Low-to-High Delay and High-to-Low Delay switch a relay
  some time after it was told to. Only the DO mode is plain on and off.
- The fail-safe values and the Communication WDT (page 76): when the module has had no TCP packet for longer than its
  host idle time, it turns on each relay whose FSV box is checked.
- P2P (page 84) and GCL (chapter 8): another module's inputs, or the module's own logic, set its relays.

The app doesn't read or change these settings. Set them in Advantech's utility or on the module's web page: the DO mode
for every relay that the app switches, no FSV box checked unless the installation wants that relay on when the network
is lost, and P2P and GCL off for those relays. The app polls every 2 s, so a host idle time longer than that never
starts the WDT while the app runs.

## Settings

The `Advantech` section:

```json
"Advantech": {
  "Host": "",
  "Port": 502,
  "PollSeconds": 2
}
```

When `Host` is empty, the setup guide asks for the address. Modbus TCP has no encryption and no login. Keep the module
on a trusted network, and never open port 502 to the internet. The module's Access Control (page 63) can limit control
to the Raspberry Pi's address.

## Simulator

`AdamSimulator` simulates a module of a model, the ADAM-6060 or the ADAM-6066, on a local port. `Contactor.Simulator`
offers port 8502, because port 502 needs rights that a developer doesn't have.

- It answers function 01 at the relays (0x0010 to 0x0015), 02 at the inputs (0x0000 to 0x0005), 05 with FF00 and 0000,
  and 0F at the relays. Its answer carries the request's transaction id and unit id, and a byte for each 8 bits asked
  for.
- It gives an exception answer for a function it does not know (code 1), an address it does not have (code 2), and a
  value, quantity or PDU length it does not take (code 3). It has no registers: functions 03, 04, 06 and 10 get code 2.
  It doesn't take FFFF in function 05, as the manual doesn't say what it does.
- It gives no answer to a frame with another protocol id.
- A header with a length no Modbus frame has (below 2 or above 254) closes the connection, as nothing after it can be
  read in step. A client that goes away in the middle of a frame closes only its own connection. The simulator takes
  the next connection as usual.
- It takes any number of connections at a time: the manual names no limit.
- Relays can be switched and inputs set "at the module", and connections can be dropped or refused.
- For the tests, `NoAnswer` stops all answers and `FailRelays` refuses functions 05 and 0F for some relays with code 4.

## To check on a real module

- **The address in the frame:** that FC 01 at 0x0010 with quantity 6 reads the six relays, and FC 05 at 0x0010 switches
  relay 1 (DO 0), not relay 2. If the module takes 0x0011 for relay 1 instead, as the manual's FC 15 example has it,
  change `AdamApi.RelayAddress` and `AdamApi.InputAddress` before anyone uses the app on a machine.
- That FC 02 at 0x0000 with quantity 6 reads the six inputs.
- That Modbus TCP is on port 502, and that unit id 1 is answered.
- The bit order of a read (bit 0 = relay 1, bit 5 = relay 6).
- That function 05 echoes the request, and that the read right after it shows the new state.
- Which exception codes the module sends for an address it doesn't have and a quantity it doesn't take.
- How many Modbus TCP connections the module takes at a time, and what it does with one more.
- What the host idle time does to the TCP connection: does the module close a connection after it? The app polls every
  2 s.
- What the fail-safe values do after a network loss, and that the module never turns a relay off by itself then.
- How the inputs read with a dry and a wet contact, and with the inverted DI status on and off.
- The relay states after a power cut. The app must turn nothing off at start.
- What the Access Control does to a request from an address it doesn't list: no answer, a closed connection or an
  exception answer.
- How the module handles a poll every 2 s for days.
