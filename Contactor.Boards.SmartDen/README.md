# Denkovi smartDEN IP-16R-MT

This project connects Contactor to the 16-relay Denkovi smartDEN IP-16R-MT over Modbus TCP. It has no web or database code.

## Sources

- [Denkovi product page](https://denkovi.com/smartden-modbus-tcp-ip-ethernet-16-relay-board)
- [smartDEN IP-16R-XX User Manual](https://denkovi.com/Documents/smartDen-IP-16R/Current-Version/UserManual.pdf), 28 September 2020.

The manual specifies Modbus TCP on port 502 by default and says that only one TCP socket is supported at a time. The client therefore keeps one connection open and serializes all requests.

## Model and protocol

| Catalogue id | Relays | Inputs | Protocol | Default port |
|---|---:|---:|---|---:|
| `denkovi-smartden-ip-16r-mt` | 16 SPDT | 0 | Modbus TCP | 502 |

- The MBAP header is 7 bytes; the protocol identifier is 0. No RTU CRC is used.
- Unit identifier 0 is used in the manufacturer's examples and can be configured.
- Function 01 reads relay coils at addresses 0–15. Relay 1 is address 0; bit 0 is relay 1.
- Function 05 writes one relay: `0xFF00` for ON and `0x0000` for OFF.
- Function 06 at register 0–15 generates a hardware pulse. The value is the duration in 100 ms units, 1–65535 (0.1–6553.5 seconds).

The client switches one relay per request and reads all relay states after commands. An echoed write is not treated as a state report. The client never writes outputs or configuration on connect: the board's reported state remains authoritative after connect and reconnect.

The manufacturer lists 16 SPDT contacts and ratings up to 10 A / 250 VAC, 15 A / 120 VAC or 10 A / 28 VDC. Follow the manufacturer's installation and inductive-load guidance.

## Settings

```json
"SmartDen": {
  "Host": "",
  "Port": 502,
  "PollSeconds": 2,
  "UnitId": 0
}
```

Modbus TCP has no authentication. Keep the board on a trusted, firewalled network and do not expose port 502 to the internet.

## Simulator

`SmartDenSimulator` listens on loopback, implements read-coils, write-single-coil and pulse-register requests, and serves one client at a time. It can reject connections, stop answering, or refuse selected relay writes for tests.

## To check on a real board

- Confirm firmware, Modbus TCP enablement, port and unit identifier.
- Verify one persistent client connection with a two-second poll interval.
- Verify coil bit order and relay numbering.
- Verify function 05 echo and the relay state read immediately afterwards.
- Verify function 06 pulse duration, 100 ms units, limits and what ON/OFF does during a pulse.
- Verify relay states after power loss with Save Outputs enabled and disabled. Contactor must never assume outputs are off.
- Verify behavior when another client connects and how an idle connection is handled.

Real-hardware verification has not been performed. Do not call this model hardware-verified until these checks are complete.
