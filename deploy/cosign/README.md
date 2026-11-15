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
2. Copy both public keys into `install/contactor-updater.sh` (its `COSIGN_KEYS`), as `scripts/images-sign.sh --check`
   asks. The update helper only runs an image signed with one of them.
3. For the Release workflow, add two repository secrets: `COSIGN_PRIVATE_KEY` (the contents of
   `contactor-main.key`) and `COSIGN_PASSWORD` (its password).
4. Keep `contactor-reserve.key` and its password offline, e.g. on a USB stick. Keep a copy of `contactor-main.key`
   offline too, so you can sign a release again by hand (see the top of `scripts/images-sign.sh`).

`signing-config.json` lists no transparency log or timestamp service: the signature is checked with the public key
alone, so nothing about our releases is sent anywhere else.

## If the main key is lost or leaks

The update helper brings itself up to date from each new release it checked (Issue #325), and it runs as root.
So anyone who has a signing key and can also push to `ghcr.io/gonace/contactor` can get root on every installation
that then presses "Update now". A key that leaks must be replaced before the next release. Replacing it later
can't undo a helper that a bad release already replaced.

An installation's update helper trusts only the keys in its own copy of `install/contactor-updater.sh`. It gets new
keys with the next "Update now": after a healthy update it brings itself up to date from the copy in the new image
(Issue #325). A helper from before that change doesn't, so such an installation gets them only when someone runs the
install command (`install.sh`) once more. So:

1. Stop using the main key at once: set the two repository secrets to the reserve key. `COSIGN_PRIVATE_KEY` gets
   the contents of `contactor-reserve.key`, and `COSIGN_PASSWORD` gets its password. The Release workflow then
   signs with the reserve key, which every installation trusts.
2. Make a new main key pair only, in the folder outside the repository:
   `cosign generate-key-pair --output-key-prefix contactor-main`. Replace `contactor-main.pub` in this folder and
   the old main key in `COSIGN_KEYS` with its public half. Merge that change into `main`, and publish a release,
   signed with the reserve key. An installation that updates to it gets the new keys. Its notes ask anyone whose
   helper is older than that change to run the install command once. Until an installation has the new keys, it
   still trusts the old main key.
3. Once you expect most installations to have that release, set the two secrets to the new main key. One that
   hasn't then refuses the next update ("isn't signed with our key") and stays on its version until `install.sh`
   is run again.
4. The reserve key was in the secrets, so it isn't offline anymore. Make a new reserve pair the same way
   (`--output-key-prefix contactor-reserve`), replace `contactor-reserve.pub` and the old reserve key in
   `COSIGN_KEYS`, and keep its private half offline.

A release signed before the change is checked with the old public key, from this folder's history in git.

`scripts/images-sign.sh` signs only with a key whose public half is in this folder.

If you publish your own images, make your own key pairs and put their public halves here instead.
