# Signing keys

Every release is signed with cosign: the main key signs it, and a reserve key, whose private half is kept offline,
lets us replace the main key. Only the public halves (`*.pub`) belong in the repository.

## Create the key pairs (once)

With [cosign](https://docs.sigstore.dev/cosign/system_config/installation/) installed, in a folder outside the
repository, e.g. `~/contactor-keys`:

```sh
cosign generate-key-pair --output-key-prefix contactor-main
cosign generate-key-pair --output-key-prefix contactor-reserve
```

Each asks for a password. Then:

1. Copy `contactor-main.pub` and `contactor-reserve.pub` into this folder and commit them.
2. For the Images workflow, add two repository secrets: `COSIGN_PRIVATE_KEY` (the contents of
   `contactor-main.key`) and `COSIGN_PASSWORD` (its password).
3. Keep `contactor-reserve.key` and its password offline, e.g. on a USB stick. Keep a copy of `contactor-main.key`
   offline too, so you can sign a release again by hand (see the top of `scripts/images-sign.sh`).

`signing-config.json` lists no transparency log or timestamp service: the signature is checked with the public key
alone, so nothing about our releases is sent anywhere else.

## If the main key is lost or leaks

1. Stop using it at once: set the two repository secrets to the reserve key. `COSIGN_PRIVATE_KEY` gets the
   contents of `contactor-reserve.key`, and `COSIGN_PASSWORD` gets its password. The Images workflow then signs
   with the reserve key.
2. Make a new main key pair only, in the folder outside the repository:
   `cosign generate-key-pair --output-key-prefix contactor-main`. Replace `contactor-main.pub` in this folder with
   its public half, and merge that change into `main`.
3. Set the two secrets to the new main key, and publish a release with the Images workflow.
4. The reserve key was in the secrets, so it isn't offline anymore. Make a new reserve pair the same way
   (`--output-key-prefix contactor-reserve`), replace `contactor-reserve.pub`, and keep its private half offline.

A release signed before the change is checked with the old public key, from this folder's history in git.

`scripts/images-sign.sh` signs only with a key whose public half is in this folder.

If you publish your own images, make your own key pairs and put their public halves here instead.
