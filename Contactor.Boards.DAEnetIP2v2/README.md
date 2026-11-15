# Denkovi DAEnetIP2v2

This project supports the Denkovi DAEnetIP2v2 controller family in the 8- and 16-relay kits. Both kits use the same Ethernet controller; the relay count is a model capability, not a separate driver.

## Protocol

The driver uses the controller's HTTP API on port 80:

- Reads digital port state from the documented `IO` array in `/ioreg.js`.
- Writes individual outputs with `P51`–`P58` for relays 1–8 and `P31`–`P38` for relays 9–16; this API requires firmware 1.25 or later.
- Uses HTTP Basic authentication when credentials are configured.

The current mapping assumes P5 is relays 1–8 and P3 is relays 9–16. This mapping, the response format across firmware versions, and command behavior still require verification against physical 8- and 16-relay kits. Do not treat simulator tests as hardware certification.

## Safety

The controller's HTTP Basic credentials and commands travel over plain HTTP. Use only on a trusted, isolated LAN, change factory credentials, and never expose the controller directly to the internet. Controller-side ICMP/analog automation can take priority over manual relay commands; disable or account for those features on the controller when using this driver.

## Scope

Digital inputs, analog inputs, SNMP, pulse commands, and other Denkovi controller families are not implemented.
