#!/usr/bin/env bash
# Signs published images with cosign, by digest, so anyone can check with deploy/cosign that we built them. The
# Images workflow signs with it right after publishing, and passes it the digest of the image index the publish itself
# pushed, so what gets signed is exactly what was built: a tag could be moved to another image
# between publishing and signing. It takes the private key itself (not a path) in COSIGN_PRIVATE_KEY and its
# password in COSIGN_PASSWORD, and the registry login in REGISTRY_USER and
# REGISTRY_TOKEN. To sign a release again by hand, e.g. after a failed signing, name the image by its
# digest (the publish prints it; GitHub shows it on the package's version):
#
#   export COSIGN_PRIVATE_KEY="$(cat ~/contactor-keys/contactor-main.key)" REGISTRY_USER=gonace
#   read -rsp "Key password: " COSIGN_PASSWORD && echo && export COSIGN_PASSWORD
#   read -rsp "GitHub token (write:packages): " REGISTRY_TOKEN && echo && export REGISTRY_TOKEN
#   bash scripts/images-sign.sh ghcr.io/gonace/contactor@sha256:<digest>
#
# With --check instead of images it only checks the key and the password, before anything is published.
# The key must belong to deploy/cosign/contactor-main.pub or contactor-reserve.pub (see deploy/cosign/README.md).
# The signature is stored next to the image on ghcr.io and isn't sent to a transparency log (signing-config.json
# lists no services), so it is checked with the public key alone.
set -euo pipefail

readonly KEYS=(deploy/cosign/contactor-main.pub deploy/cosign/contactor-reserve.pub)
readonly SIGNING_CONFIG=deploy/cosign/signing-config.json

fail() { echo "Error: $*" >&2; exit 1; }

cd "$(dirname "$0")/.."
[[ $# -gt 0 ]] || fail "name the images to sign by digest, e.g. ghcr.io/gonace/contactor@sha256:<digest>, or --check."
command -v cosign > /dev/null || fail "cosign isn't installed; see https://docs.sigstore.dev/cosign/system_config/installation/"
[[ -n "${COSIGN_PRIVATE_KEY:-}" ]] || fail "COSIGN_PRIVATE_KEY must hold the private key."
[[ -n "${COSIGN_PASSWORD+set}" ]] || fail "COSIGN_PASSWORD must hold the key's password."
for candidate in "${KEYS[@]}"; do
    [[ -f "$candidate" ]] || fail "$candidate is missing; create the key pairs as deploy/cosign/README.md says."
done

# Line endings dropped, so a Windows checkout compares the same.
public_key() { tr -d '\r' < "$1"; }

# The key's public half must be one in deploy/cosign: otherwise nothing could check this release's signature.
public=$(cosign public-key --key env://COSIGN_PRIVATE_KEY | tr -d '\r') || fail "could not read the private key; is the password right?"
key=""
for candidate in "${KEYS[@]}"; do
    if [[ "$public" == "$(public_key "$candidate")" ]]; then
        key=$candidate
    fi
done
[[ -n "$key" ]] || fail "the private key belongs to neither ${KEYS[*]}."
if [[ "$1" == "--check" ]]; then
    echo "The key belongs to $key."
    exit 0
fi
echo "Signing with $key"

# cosign reads the registry login from a Docker config of its own, removed again on exit, so the token is neither
# on the command line nor saved.
login=$(mktemp -d)
trap 'rm -rf "$login"' EXIT
host=${1%%/*}
if [[ -n "${REGISTRY_TOKEN:-}" ]]; then
    auth=$(printf '%s:%s' "${REGISTRY_USER:-token}" "$REGISTRY_TOKEN" | base64 | tr -d '\n')
    (umask 077 && printf '{"auths":{"%s":{"auth":"%s"}}}\n' "$host" "$auth" > "$login/config.json")
fi
# Git Bash: cosign.exe needs the folder as a Windows path.
if command -v cygpath > /dev/null; then
    DOCKER_CONFIG=$(cygpath -w "$login")
else
    DOCKER_CONFIG=$login
fi
export DOCKER_CONFIG

# Every image is checked before the first is signed, so a wrong argument signs nothing.
for image in "$@"; do
    if [[ ! "$image" =~ ^[^@]+@sha256:[0-9a-f]{64}$ ]]; then
        fail "$image isn't named by its digest (<image>@sha256:<64 hex digits>)."
    fi
done

for image in "$@"; do
    echo "== $image"
    cosign sign --yes --key env://COSIGN_PRIVATE_KEY --signing-config "$SIGNING_CONFIG" "$image" \
        || fail "could not sign $image."
    # Checks it with the public key, which also proves the registry serves the signature.
    cosign verify --key "$key" --insecure-ignore-tlog=true "$image" > /dev/null 2>&1 \
        || fail "$image is signed, but its signature doesn't verify with $key."
done
echo "Signed ${#} image(s) with $key."
