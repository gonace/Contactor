# Signing keys

The Raspberry Pi only runs images signed with one of the two keys here: the main key, which signs every release,
and a reserve key, whose private half is kept offline so the main key can be replaced without touching the Pi.
Only the public halves (`*.pub`) belong in the repository.

## Create the key pairs (once)

With [cosign](https://docs.sigstore.dev/cosign/system_config/installation/) installed, in a folder outside the
repository, e.g. `~/contactor-keys`:

```sh
cosign generate-key-pair --output-key-prefix contactor-main
cosign generate-key-pair --output-key-prefix contactor-reserve
```

Each asks for a password. Then:

1. Copy `contactor-main.pub` and `contactor-reserve.pub` into this folder and commit them.
2. Copy both public keys into `deploy/upgrade.sh` (its `COSIGN_KEYS`), as `scripts/images-sign.sh --check` asks.
3. Keep `contactor-main.key` where you publish from, and `contactor-reserve.key` offline (a USB stick).
4. For the Images workflow, add two repository secrets: `COSIGN_PRIVATE_KEY` (the contents of
   `contactor-main.key`) and `COSIGN_PASSWORD` (its password).

`signing-config.json` lists no transparency log or timestamp service: the signature is checked with the public key
alone, so nothing about our releases is sent anywhere else.
