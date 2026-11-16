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
