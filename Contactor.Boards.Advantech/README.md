# Advantech ADAM-6000 relay boards

This project supports the ADAM-6060 and ADAM-6066. Both use Modbus TCP on port 502 and the same six-output, six-input map in the ADAM-6000 manual. The station field in the manual's Modbus examples is `01`; this client uses unit id 1. Their relay contact ratings differ:

| Model | Relay contacts | Resistive rating |
|---|---|---|
| ADAM-6060 | Six Form A | 120 VAC at 0.5 A; 30 VDC at 1 A |
| ADAM-6066 | Six Form A | 250 VAC at 5 A; 30 VDC at 3 A |

| Channels | Manual reference | Modbus operation | Function |
|---|---|---|---|
| Relay coils 1–6 (`00017–00022`) | Appendix B.2, pp. 187–188 | Read the six relay states together | 01 (Read Coils) |
| Relay coil `00017` + channel − 1 | Appendix B.2, pp. 187–188 | Write one relay at a time, ON `FF 00`, OFF `00 00` | 05 (Write Single Coil) |
| Digital inputs 1–6 (`00001–00006`) | Appendix B.2, pp. 187–188 | Read six input states together | 02 (Read Discrete Inputs) |

These are the same addresses for both models; only their contact ratings differ. An energized coil is ON in Contactor. The NC terminal on another model is a wiring choice and does not change how a relay coil's state is shown.

The client uses the Modbus TCP MBAP header, then the function PDU. The manual's FC 01 example request is `01 01 00 17 00 08`, with answer `01 01 01 42`; its FC 05 example is `01 05 00 03 FF 00`, echoed as `01 05 00 03 FF 00`. These are the function messages without MBAP headers. The TCP ADU adds transaction id, protocol id `00 00`, byte length, and unit id before each PDU; the examples in `Adam6000ModbusTests` check both the manual messages and MBAP encoding. The examples read eight coils, while Contactor reads the six relays in the table.

The manual supports continuous pulse trains and a fixed total number of pulses. The low-level and high-level widths use 0.1 ms units. The register pairs are `40013–40024` (low width), `40025–40036` (high width), `40037–40048` (absolute count), and `40049–40060` (incremental count). The manual does not state their numeric limits or a maximum relay pulse frequency. These settings describe a pulse train, not one exact ON duration, so this integration does not expose `PulseAsync`.

## Manual source

- Advantech, [*ADAM-6000 User Manual*, Edition 12](https://advdownload.advantech.com/productfile/Downloadfile4/1-2B6FKTG/ADAM-6000_User_Manaul_Ed.12-FINAL.pdf), 2022-11-28: sections 5.5–5.6 and 7.3, Modbus protocol and examples; chapter 6, pulse output, pages 80–82; Appendix B.2.5, ADAM-6060/6066, pages 216–218.

The manual shows the default Modbus TCP port as 502 in its example code and uses station `01` in function-code examples. Access Control lets the installer restrict control to listed IP or MAC addresses. The configuration password (default `00000000`) protects configuration actions; the manual does not describe password authentication in Modbus requests.

The Host Idle timeout is configurable. The manual's example shows 720 seconds and says it affects the TCP connection. The Communication WDT sets configured fail-safe outputs when no TCP packet arrives for longer than Host Idle. The manual does not state the maximum number of simultaneous Modbus TCP connections. Check both settings on the installed firmware before use.

## Check on real hardware

- Confirm Modbus TCP port, unit id and the coil/input addresses on each model and firmware version.
- Read all six inputs and switch each relay individually. Confirm that a write to one relay leaves the other five unchanged.
- Confirm response framing, exception responses, reconnect after a dropped connection and reported state after reconnection.
- Review the module's Access Control and Communication WDT / Fail Safe Value settings, including what happens to outputs after a network loss.
- Confirm the relay contact type and model-specific rating against the load and wiring.

## ADAM-6200 series: ADAM-6260

The ADAM-6260 is supported separately from the ADAM-6000 series. The only shared protocol type is `ModbusTcpFrame`, which handles the common seven-byte Modbus TCP MBAP header and bounded PDU. It has its own model, register map, client, simulator and adapters. Although both manuals list relay outputs at `00017–00022`, the series-specific maps remain separate: the ADAM-6200 manual also describes user-defined 0X/4X addresses, so a device configured with a changed map needs explicit support and hardware verification.

| Channels | ADAM-6000 (6060/6066) | ADAM-6200 (6260) |
|---|---|---|
| Relays | `00017–00022`, FC01 read / FC05 per-relay write | `00017–00022`, FC01 read / FC05 per-relay write |
| Digital inputs | `00001–00006`, FC02 read | None |
| Pulse configuration | Manual documents pulse trains on the relay outputs | `40001–40012` low width, `40013–40024` high width, `40025–40036` absolute count, `40037–40048` incremental count (Appendix A.1.7, p. 146) |

ADAM-6260 contact type is five Form C and one Form A. Its contact rating is 250 VAC at 5 A and 30 VDC at 5 A (section 2.5, p. 27). The coil state is what Contactor shows as ON; Form C normally-closed wiring does not invert the app state. The module has six relay outputs and no digital inputs.

The module's TCP Modbus port is 502 (section 4.3.2.2, p. 58). The manual does not specify a Modbus TCP Unit ID or a maximum number of simultaneous Modbus TCP connections. The app defaults Unit ID to 1 and makes it configurable. Its administrator password (default `00000000`) is for utility/configuration access; the manual does not describe password authentication in Modbus requests. Access Control can limit control to configured IP or MAC addresses (section 4.3.2.7, p. 61). Host Idle is configurable and the Communication WDT can drive configured fail-safe values when no TCP packet arrives before that timeout (sections 4.3.2.2 and 4.3.3.6, pp. 58, 83). The manual does not give a connection limit.

The manual describes continuous or finite pulse trains, with low/high width in 0.1 ms units (section 4.3.3.3, pp. 78–79); it does not give the numeric width/count range for the ADAM-6260 map or a maximum relay pulse frequency. This integration does not expose Contactor's one-shot `PulseAsync`. The ADAM-6200 manual gives no byte-for-byte Modbus TCP example request/answer for the ADAM-6260; Appendix A.1.1 lists supported function codes and Appendix A.1.7 gives its addresses.

## Check the ADAM-6260 on real hardware

- Confirm the configured Modbus TCP Unit ID and whether the device's 0X/4X address map was changed from its manual defaults.
- Read all six relay states, then switch each relay individually and confirm other relays remain unchanged.
- Confirm response framing, Modbus exception handling and reconnect after a dropped connection.
- Verify Access Control and Host Idle / Communication WDT fail-safe behavior with the installed firmware.
- Confirm the Form C/Form A terminals and the 250 VAC / 5 A and 30 VDC / 5 A ratings against the load and wiring.
