# Denkovi

How the app talks to the Denkovi boards. What every board shares (`IRelayBoard`, `IBoardConnection`, the board
catalogue) is in `docs/ARCHITECTURE.md`.

This project connects Contactor to the Denkovi relay boards. Today it has two: the smartDEN IP-16R-MT (Modbus TCP) and
the DAEnetIP2 v2 with a Denkovi relay board (SNMPv1, see [The DAEnetIP2 v2](#daenetip2-v2)). For each it has the
codec, the client, the adapters for the app and a simulator. It has no web or database code. The types of each board
have the board's name as a prefix (`SmartDen*`, `DaenetIp2*`). `DenkoviBrand` has the brand that they share.

The sections under "smartDEN IP-16R-MT" are about the smartDEN IP-16R-MT.

## Sources

- Denkovi's "smartDEN IP-16R-XX User Manual", 28 September 2020
  (https://denkovi.com/Documents/smartDen-IP-16R/Current-Version/UserManual.pdf):
  - section 8.2: "Save Outputs";
  - section 8.6: the pulse mode of each relay ("Pulse, ms (x100)");
  - section 8.9: the week schedule;
  - section 8.13: the Modbus TCP settings (port 502, idle timeout 5 min, one TCP connection at a time);
  - section 10.2: the Modbus TCP protocol, tables 10.6 to 10.29.
- The product page: https://denkovi.com/smartden-modbus-tcp-ip-ethernet-16-relay-board
- Denkovi's "DAEnetIP2 v2 User Manual", 21 April 2020:
  - table 4.1: the default settings (SNMP on UDP port 161, the read-only community `000000000000`, the read-write
    community `private`, P3 and P5 as outputs);
  - section 6.3: "Save Outputs";
  - section 6.5: SNMP access only from one network ("Network IP" and "Network Mask") and from two MAC addresses;
  - section 6.6.1: the SNMP agent's port and the two communities, at most 12 characters each. The read-write community
    is "used in read/write operations";
  - sections 6.7, 6.9 and 6.10: the direction of each pin, the analog events and the ICMP monitoring, which can set P5's
    pins;
  - section 7: which of these wins over a set from the app;
  - section 8: the HTTP API, which writes a whole port at once (`iochange.cgi?…&01=00&02=FF`). The app doesn't use it;
  - section 9: SNMPv1, `snmpget` and `snmpset` of one OID at a time, no `snmpwalk`. The read-only community is for
    reading, the read-write community for changing;
  - tables 9.4 and 9.5: the OIDs of P3 and P5;
  - section 12 (appendix 3), steps 3.1 to 3.3: which ports each relay board is on;
  - section 14.2.3: the example `snmpget` and `snmpset` commands;
  - section 15 (appendix 9) and figure 15.1: an output is 3.3 V at "1" and 0.25 V at "0", and "1" switches a relay on
    through a transistor.
- SNMPv1 itself: RFC 1157 (the messages and the 484-byte limit) and its BER encoding.

## smartDEN IP-16R-MT

Nobody has tested the board with the app yet. The list in "To check on a real board" shows what is not known.

### Models

| Catalogue id | Name | Relays | Inputs | Pulse | Login |
|---|---|---|---|---|---|
| `denkovi-smartden-ip-16r-mt` | Denkovi smartDEN IP-16R-MT | 16 | 0 | 0.1 s to 6553.5 s | none |

The board uses port 502 and has no login: Modbus has none, and the password of the board's web page doesn't guard
Modbus. The other smartDEN IP-16R models (SNMP, MQTT) don't speak Modbus TCP, and the app doesn't support them.

### Protocol

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

### The app's client

`SmartDenClient` keeps one TCP connection and sends one request at a time. Each request waits for its answer.

- Each request and each connection attempt has `SmartDenClient.RequestTimeout` (5 s) on `TimeProvider`.
- The client reads the header first, and then only the length that the request expects: at most 260 bytes.
- An answer with a wrong transaction id, protocol, unit, function, length, byte count or echo is unreadable. Then the
  client closes the connection, because the stream can be out of step. The next request opens a new connection.
- A timeout also closes the connection. The next request opens a new one, so one lost answer doesn't lose the board.
- When the connection was open from before and turns out broken (the board can close a connection it finds idle), the
  client sends the request once more on a new connection. Reading the relays, switching one or starting its pulse has the
  same result when done twice.
- A poll that a command cancels keeps the connection, because the board may refuse a new one while it holds the old one.
  The poll's answer can still come. The next request reads it and drops it by its transaction id ("Skipped a late answer
  nobody waited for"). When part of that answer was read already, or 8 answers are still to come, the client opens a
  new connection.
- Bytes that wait before a request were not asked for. The client drops them and shows them on the Debug screen.
- `BoardPoller` does the polls, the reconnects and the count of failures, as for the other polled boards.

A command (ON, OFF or a pulse) switches the relays in the mask one at a time:

- The client sends a request for every relay in the mask, also when the board refuses one or its answer can't be read.
  "All off" and a group's interlock depend on every OFF being sent.
- A request with no answer, also on a new connection, stops the rest of the mask: the board is gone.
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
- When that read gets an unreadable answer or no answer, the command returns the relays that the board took. The client
  keeps the last state it read and reads again at once. An echo is not proof that a relay switched.

`SmartDenClient.PulseAsync` and `SmartDenBoard.PulseAsync` throw `ArgumentOutOfRangeException` for a length outside
0.1 s to 6553.5 s, or not in steps of 0.1 s, before anything is sent.

`SmartDenBoard`, `SmartDenConnection` and `SmartDenTraffic` are the adapters for the app. `Features/Denkovi` in
`Contactor.Web` registers them. `SmartDenTraceText` puts each frame in plain words. The polls and the answers that
change nothing are routine.

### Settings

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

### The simulator

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
- It takes one connection at a time, as the manual says. While one is open, it closes a new one at once. A connection
  whose client closed it doesn't count. With `NewConnectionTakesOver`, a new connection closes the one before instead.
  The manual doesn't say which one the board does: see below.
- Relays can be switched "at the board", and connections can be dropped or refused.
- For the tests, `NoAnswer` stops all answers and `FailRelays` refuses functions 05, 06 and 0F for some relays with code 4.

### Settings on the board that switch relays without the app

The app reads the board's relays on every poll, so the board stays the source of truth. These board settings switch
relays without the app. The app sees each switch only as a change from outside:

- "Save Outputs" (section 8.2): the board keeps the relay states in its memory and sets them again after a restart.
- The week schedule (sections 8.9 and 10.2, registers 0x6100 to 0x671D): up to 30 rows that switch relays at set times.
- The pulse mode of each relay (sections 8.6 and 10.2, registers 0x6000 to 0x600F): when it isn't 0, the board turns
  the relay off again after that time each time it goes on, also after an ON from the app.

The app doesn't read or change these settings. Set them on the board's web page: the week schedule off, and the pulse
mode 0 for every relay that the app switches.

## DAEnetIP2 v2

The DAEnetIP2 v2 is an IP controller with two 8-bit digital output ports, P3 and P5. Denkovi sells it as a kit, with a
cable and a Denkovi relay board on those ports. The app supports three kits. The types have the prefix `DaenetIp2`.

Nobody has tested a kit with the app yet. The list in [On a DAEnetIP2 v2 kit](#on-a-daenetip2-v2-kit) shows what is not
known. Issue #371 is for a check on real boards; it doesn't block the app's support.

### Models

| Catalogue id | Name | Relays | On pins | Pulse | Login |
|---|---|---|---|---|---|
| `denkovi-daenetip2-v2-16` | Denkovi DAEnetIP2 v2 16-relay kit | 16 | relays 1 to 8 on P3.1 to P3.8, relays 9 to 16 on P5.1 to P5.8 | none | read-write community |
| `denkovi-daenetip2-v2-8` | Denkovi DAEnetIP2 v2 8-relay kit | 8 | P5.1 to P5.8 | none | read-write community |
| `denkovi-daenetip2-v2-2` | Denkovi DAEnetIP2 v2 2-relay kit | 2 | relay 1 on P5.7, relay 2 on P5.8 | none | read-write community |

The manual says which ports each relay board is on (section 12, steps 3.1 to 3.3), but not which pin is which relay.
The app takes the manual's order: P3 before P5 (as in tables 5.1, 9.4 and 9.5, and in section 8), and pin 1 before pin
8. `DaenetIp2Models` has the layout of each kit, so a change is in one place.

A pin that is High (1) is a relay that is on. The manual's figure 15.1 shows a relay switched on by an output at "1".
Section 6.9 also says "1" switches the load on. The manual doesn't say it for the Denkovi relay boards themselves: see
below.

The kits have no inputs and no pulse in the app. The board can't pulse one relay by itself over SNMP: its only pulse is
the restart pulse of its ICMP monitoring (`cfgResetPulse`), which the app doesn't use.

### Protocol

SNMPv1 over UDP, port 161 (table 4.1). Each request has one OID (section 9).

- A message is a BER `SEQUENCE` of the version (`INTEGER 0` for SNMPv1), the community (`OCTET STRING`) and the PDU.
- The PDU is `GetRequest` (`A0`), `SetRequest` (`A3`) or the answer, `GetResponse` (`A2`). It holds the request id, the
  error-status, the error-index and one variable: its OID and its value (`NULL` in a `GetRequest`).
- Every OID starts with Denkovi's prefix `.1.3.6.1.4.1.42505` ("x" in the manual's tables).
- The board never sends data by itself (the app doesn't use its traps). The app polls it every 2 s.

| What | Request | OID | Value | Answer | Manual |
|---|---|---|---|---|---|
| Read port P3 | `GetRequest` | x.9.2.1.33.0 (`pctrlP3byte`) | `NULL` | `INTEGER` 0 to 255, bit 0 = P3.1 | table 9.4 |
| Read port P5 | `GetRequest` | x.9.2.2.33.0 (`pctrlP5byte`) | `NULL` | `INTEGER` 0 to 255, bit 0 = P5.1 | table 9.5 |
| Switch one pin of P3 | `SetRequest` | x.9.2.1.n.0 (`pctrlP3pin`n) | `INTEGER` 1 (High) or 0 (Low) | the value, echoed | table 9.4 |
| Switch one pin of P5 | `SetRequest` | x.9.2.2.n.0 (`pctrlP5pin`n) | `INTEGER` 1 (High) or 0 (Low) | the value, echoed | table 9.5 |

The app reads the ports that the kit's relays are on, P3 before P5. It switches one relay with one `SetRequest` of its
pin, so it never writes a whole port: a change at the board in between is never written over. The board also takes a
`SetRequest` of a port's byte (tables 9.4 and 9.5), and the HTTP API writes whole ports (section 8). The app uses
neither.

A refused request gets a `GetResponse` with an error-status: 1 too big, 2 no such name, 3 a value it doesn't take, 4
read-only, 5 it failed. `DaenetIp2RefusedException` has it.

The manual gives commands, not bytes. `DaenetIp2SnmpTests` writes its examples (section 14.2.3) in BER by hand and
checks them byte for byte:

| Frame | Meaning |
|---|---|
| `30 31 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A0 1E 02 01 01 02 01 00 02 01 00 30 13 30 11 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 21 00 05 00` | `snmpget -v1 -c 000000000000 … .1.3.6.1.4.1.42505.9.2.2.33.0` ("Get the whole P5"), request id 1 |
| `30 32 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A2 1F 02 01 01 02 01 00 02 01 00 30 14 30 12 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 21 00 02 01 05` | Its answer: P5 = 5, pins 1 and 3 High |
| `30 2D 02 01 00 04 07 70 72 69 76 61 74 65 A3 1F 02 01 02 02 01 00 02 01 00 30 14 30 12 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 01 00 02 01 01` | `snmpset -v1 -c private … .1.3.6.1.4.1.42505.9.2.2.1.0 i 1` (P5.1 High), request id 2 |

`DaenetIp2Snmp` is our own small codec, without a NuGet package. It writes and reads only these messages:

- A datagram is at most 484 bytes (`DaenetIp2Api.MaxMessageBytes`), as RFC 1157 asks every agent to take. A longer one
  is no answer.
- Lengths in the short form, and in the long form with 1 or 2 bytes. Every length is checked against what is left, so a
  datagram can't make the codec read past it.
- An `INTEGER` of 1 to 4 bytes, in two's complement. A value of one byte is read unsigned, 0 to 255: an agent may send
  a port of 255 as one raw byte (`02 01 FF`) as well as BER's `02 02 00 FF`. Anything outside 0 to 255 after that is
  refused. An OID of at most 32 arcs, each of at most 5 bytes and with no leading zero byte.
- Exactly one variable, and no bytes after the message.

An answer is the answer to a request only when it has all of these (`DaenetIp2Snmp.Value`):

- SNMPv1, a `GetResponse`, the request's id and the request's community;
- error-index 0 or 1, and error-status 0, or else the board refused it;
- the request's OID, and an `INTEGER` from 0 to 1 for a pin, or from 0 to 255 for a port;
- for a set, the value that was set.

#### Login: the read-write community

Every SNMP request carries a community, a word that works as a password. SNMPv1 sends it as plain text. The board has
two (section 6.6.1): the read-only community, for reading, and the read-write community, which is "used in read/write
operations". So one secret does both, and the app asks only for the read-write community. It is the login's password;
the model is `PasswordOnly`, with no user name. The read-only community is never needed.

- 4 to 12 characters: table 9.1 gives the community as `OCTET STRING (SIZE (4..12))`, and section 6.6.1 says "max 12
  symbols". The app takes letters, digits and plain symbols without spaces, so each character is one byte
  (`DaenetIp2Models.CheckCommunity`). A shorter or longer one is refused in words: "The password, the board's
  read-write community, must be 4 to 12 characters."
- The board always has a community, so the model is `LoginRequired`: the setup guide and Settings show the password
  field and the hint, and no "The board has no password" box. The hint names the factory community, `private`
  (table 4.1), so a person knows what to type on a new board.
- The community is a secret, like any board's password. It is never logged, traced or shown. The Debug screen shows the
  PDU of each message only, never the whole datagram, because the community is before the PDU. A datagram that the app
  can't read shows only its length.
- A wrong community gets no answer at all: SNMPv1 drops a message that isn't authentic (RFC 1157, section 4.1). So the
  app can't tell a wrong community from a wrong address. The login's hint says it: "With a wrong community the board
  doesn't answer: check the address and the community." The trace says the same after each request without an answer.
- With no community saved yet (a board added in the app starts so), the app sends an empty one, and the board doesn't
  answer. `DaenetIp2Connection.SetLogin` still takes no login for that, as every board does
  (`IBoardConnection.SetLogin`).

### The app's client

`DaenetIp2Client` has one UDP socket and sends one request at a time. Each request waits for its answer.

- Each request has its own request id (from 1 up, then from 1 again after 2147483647).
- It waits `DaenetIp2Client.RequestTimeout` (1 s) on `TimeProvider`. Without an answer it sends the same request once
  more, with the same id (`DaenetIp2Client.Attempts`, 2): reading a port or setting a pin has the same result when done
  twice. An answer to either try counts. Without an answer to both, the request has no answer: "check the address and
  the community".
- It skips each datagram from another address or port, one with another request id (an answer that came too late), and
  one that it can't read. They show on the Debug screen, the first 8 of each try (`MaxSkippedTraced`) and then one line
  for the rest. Each skip goes on waiting for the answer, until the timeout.
- On Windows, a "port unreachable" for an earlier datagram, or a datagram too long for the buffer, comes as an error on
  the next receive. The client skips both.
- The board counts as connected only after its first valid answer (`BoardPoller`). Before that, a command throws
  `BoardNotConnectedException`.
- A host name is looked up when the client starts asking, within the same 1 s. The client takes an IPv4 address first.
  Without an address it sends nothing: an empty name would find this computer itself.
- The trace says which local UDP port the client sends from. When the network refuses a datagram, the client closes
  the socket, and the next request opens a new one.
- A new address forgets the board's state. A new community asks again at once. The board counts as connected again only
  when it answers with the new community.
- `BoardPoller` does the polls, the new tries and the count of failures, as for the other polled boards. A request with
  no answer to both tries is one failed poll; after 3 in a row the board counts as lost.

A command (ON or OFF) switches the relays in the mask one at a time, as on the smartDEN:

- The client sends a `SetRequest` for every relay in the mask, also when the board refuses one or its answer can't be
  read. "All off" and a group's interlock depend on every OFF being sent.
- One relay with no answer to both tries doesn't stop the rest of the mask either: over UDP, one lost datagram says
  nothing about the next, and "All off" and a group's interlock depend on every OFF being sent. A relay without an
  answer isn't among the relays returned.
- Two relays in a row with no answer stop the rest (`MaxUnansweredInARow`): the board is then most likely gone. Each
  further relay would hold the board's lock 2 s more, and an interlock OFF and the screens wait behind it. So a board
  that is gone costs a command about 4 s, whatever its mask. The command then returns the relays taken before, without
  the read after, and the poller reads the board at once.
- An empty mask sends nothing and returns no relays.
- When the board takes no relay, the command throws "The DAEnetIP2 v2 board didn't take the command: …"
  (`BoardNotConnectedException`). A refusal never leaves the client as `BoardRefusedException`.
- Otherwise it returns the relays that the board confirmed. A refused relay, or one whose answer couldn't be read, isn't
  among them.
- When no relay's request got an answer, the command throws "The DAEnetIP2 v2 board didn't answer; the command may not
  have been carried out." The poller counts it as a failed poll and reads the board at once after it.
- After the switches, the client reads the ports in the same command. The poller takes that as the new state.
- When that read fails, the command returns the relays that the board took. The client keeps the last state it read and
  reads again at once. An echo is not proof that a relay switched: the board answers a set that its ICMP monitoring or
  an analog event then overrides (section 7).

`DaenetIp2Board`, `DaenetIp2Connection` and `DaenetIp2Traffic` are the adapters for the app. `Features/Denkovi` in
`Contactor.Web` registers them (`DaenetIp2Boards`). `DaenetIp2TraceText` puts each message in plain words, e.g. "Turn on
relay 3 (P3.3)". The polls and the answers that change nothing are routine.

### Settings

The `DaenetIp2` section:

```json
"DaenetIp2": {
  "Host": "",
  "Port": 161,
  "Community": "",
  "PollSeconds": 2
}
```

When `Host` is empty, the setup guide asks for the address. `Community` is the read-write community, a secret: put it
in an environment variable (`DaenetIp2__Community`) or type it in the app, never in a file in the repository. SNMPv1 has
no encryption, and the community goes as plain text. Keep the board on a trusted network, and never forward UDP port 161
from the internet (the manual's appendix 4 shows how; don't). If the board's SNMP access is limited to one network
(section 6.5), that network must hold the app's address.

### The simulator

`DaenetIp2Simulator` simulates a kit on a local UDP port. `Contactor.Simulator` offers port 8161, because port 161 needs
rights that a developer doesn't have.

- It answers a `GetRequest` and a `SetRequest` of each pin and each port's byte of P3 and P5, as above. Its relays are
  the kit's pins: a High pin is a relay that is on. A pin that no relay is on still switches.
- A get takes the read-only or the read-write community, a set only the read-write one. The read-write community is
  the one given on its page, or `private` without one. The read-only community is `000000000000` unless a test sets
  another (`ReadOnlyCommunity`).
- It gives no answer to a wrong community, to another version, to anything but a `GetRequest` or a `SetRequest`, and to
  a datagram it can't read or that is longer than 484 bytes. It goes on with the next datagram.
- It answers another OID with error-status 2 (no such name), and a value out of range or not an `INTEGER` with 3 (a
  value it doesn't take). The real board's codes are to check.
- UDP has no connection. Its page lists where the app asked from; "Drop connection" forgets them.
- For the tests: `NoAnswer` stops all answers, `DropRequests` drops the next few requests, `SilentRelays` never answers
  the sets of some relays, and `FailRelays` refuses the sets of some relays with error-status 5 (it failed).
- Like the board, it takes a community of 4 to 12 characters only.
- Its trace shows the PDU of each message, never the community.

### Settings on the board that switch relays without the app

The app reads the ports on every poll, so the board stays the source of truth. These board settings switch pins without
the app. The app sees each switch only as a change from outside:

- "Save Outputs" (section 6.3, `cfgNewMode` bit 0): the board sets the pins again after a restart.
- "Reset I/O Ports on Restart" (section 6.10.2): the board sets every pin of P3 and P5 to "0" at a restart.
- The ICMP monitoring (section 6.10): it pulses the P5 pins in `cfgP5DupRST` to restart another device. It wins over a
  set from the app (section 7).
- The analog events (section 6.9): they set a P5 pin by an analog input's level. They also win over a set from the app
  (section 7).
- The direction of each pin (section 6.7, `cfgP3Dir` and `cfgP5Dir`): a pin set as an input switches no relay.

The app doesn't read or change these settings. Set them on the board's web page: P3 and P5 as outputs, the ICMP
monitoring off or without P5 restart pins, and no analog events on the pins that the app switches.

## To check on a real board

### On the smartDEN IP-16R-MT

> **Warning: check the first four points first, before anything is connected to the relays.**

- The coil bit order (bit 0 = relay 1, bit 15 = relay 16).
- The relay states after a power cut, with "Save Outputs" on and off. The app must turn nothing off at start.
- The pulse (function 06 at 0x0000 to 0x000F): that it turns the relay on and then off, the unit of 100 ms, the limits
  (1 to 65535), and what a function 05 or a second pulse does to a pulse that runs.
- What a pulse does to a relay that is already on: does the relay go off after the length, or stay on?
- The factory address, and that Modbus TCP is on at port 502 (section 8.13: "Enable Modbus-TCP").
- That unit id 0 is answered, and which unit ids the board takes.
- What the board does with a second TCP connection while one is open: does it refuse the new one, or does the new one
  take over? The simulator refuses it. If the board refuses it, a connection that the app lost without the board's
  knowledge blocks the app until the board's idle timeout (5 min).
- That the idle timeout never closes the app's connection: the app polls every 2 s.
- The length in the header of an answer with 2 bytes of relays: 5, as Modbus TCP says, or 4, as table 10.11 says. The
  app takes only 5.
- That function 05 echoes the request, and that the read right after it shows the new state.
- Which exception codes the board sends for a coil out of range, a quantity above 16, and a function 05 value other than
  FF00, 0000 or FF02.
- How the board handles a poll every 2 s for days.

### On a DAEnetIP2 v2 kit

> **Warning: check these four first, before anything is connected to the relays.** If one is wrong, the app's OFF can
> turn a machine ON, or switch another relay than the one named on the screen. The manual doesn't say them for the
> kits' relay boards; the app takes the choices below.
>
> 1. **A High pin (1) turns its relay on.** The manual shows it only for a relay switched through a transistor (figure
>    15.1, and section 6.9).
> 2. **Relays 1 to 8 of the 16-relay kit are on P3**, relays 9 to 16 on P5.
> 3. **"In 1" is pin 1 of its port**, "In 2" pin 2, and so on, on every kit.
> 4. **Relay 1 of the 2-relay kit is on P5.7**, relay 2 on P5.8.
>
> To check them: with nothing connected to the relays' contacts, choose the kit in the setup guide, and in "Name your
> relays" press "Try" for each relay in turn. The board's own LED of that relay, and only that one, must go on for 2 s
> and then off. A relay that is on while the app shows every relay off means a pin works the other way round: stop, and
> don't use the kit with the app. Check relay 1 and relay 9 on the 16-relay kit, and relay 1 on the 2-relay kit.

The other points:

- The pin states after a power cut, with "Save Outputs" on and off. The app must turn nothing off at start.
- The communities of a new board: `000000000000` and `private` (table 4.1). That a get with the read-write community
  is answered (section 6.6.1 says it is).
- What the board does with a wrong community, and with a set with the read-only community: no answer, as the app
  expects, or an error-status?
- How the board encodes a port of 128 to 255: as BER's two bytes (`02 02 00 FF`) or as one raw byte (`02 01 FF`). The
  app takes both.
- That the board answers from UDP port 161 (or the port set), the one the app sent to: an answer from another port is
  skipped.
- The firmware version (`cfgVersion`, x.9.1.8.0) of the kits that Denkovi sells now.
- That the board answers within 1 s, every time, to a poll every 2 s for days, also with two gets per poll on the
  16-relay kit.
- That the answer carries the request's community, and that it is a `GetResponse` with the request's OID. The manual's
  example output (section 14.2.1) shows "enterprises.19865", not 42505.
- Which error-status the board sends for an OID it doesn't have and for a value out of range.
- That a set is echoed with the value set, and that the read right after it shows the new state.
