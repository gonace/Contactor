#!/usr/bin/env bash
# Publishes the image to ghcr.io from this computer, like the Images workflow does, for when there are no Actions
# minutes: it computes the release version (YYYY.M.D in UTC, plus .N for a later release the same day, see
# release-version.sh), builds and runs every test (nothing is published unless they pass), builds and pushes the image
# (linux/arm64 and linux/amd64, images-build.sh) tagged with the version, `latest` and `sha-<commit>`, signs it by the
# digest the build pushed (images-sign.sh), then pushes the tag v<version> to the commit it published. Run it from a
# clean checkout of main, with the .NET SDK, Docker (with buildx) and cosign installed. It asks for a
# classic GitHub token with write:packages, the cosign private key's file (~/contactor-keys/contactor-main.key
# unless you name another) and its password; none of them is shown or saved. COSIGN_PRIVATE_KEY (the key itself) and
# COSIGN_PASSWORD, when set, are used instead of asking.
#
#   bash scripts/images-publish.sh
set -euo pipefail

readonly OWNER=gonace
readonly REPOSITORY=gonace/contactor
readonly BRANCH=main
readonly DEFAULT_KEY=~/contactor-keys/contactor-main.key

fail() { echo "Error: $*" >&2; exit 1; }

cd "$(dirname "$0")/.."
git fetch --quiet origin "$BRANCH" || fail "could not fetch $BRANCH."
[[ "$(git branch --show-current)" == "$BRANCH" ]] || fail "check out $BRANCH first (git checkout $BRANCH && git pull)."
[[ -z "$(git status --porcelain)" ]] || fail "this checkout has changes; publish only what is committed."
[[ "$(git rev-parse HEAD)" == "$(git rev-parse "origin/$BRANCH")" ]] || fail "this checkout isn't the latest $BRANCH; run git pull first."
command -v cosign > /dev/null || fail "cosign isn't installed; see https://docs.sigstore.dev/cosign/system_config/installation/"
docker buildx version > /dev/null 2>&1 || fail "Docker with buildx isn't installed or running."

# The Raspberry Pi takes a new image the same night, so nothing is published unless every test passes.
echo "== Building and testing"
dotnet build Contactor.slnx -c Release || fail "the build failed; nothing was published."
dotnet test --solution Contactor.slnx --no-build -c Release || fail "a test failed; nothing was published."

commit=$(git rev-parse HEAD)
sha=${commit::7}
version=$(bash scripts/release-version.sh) || fail "could not compute the version."
export REGISTRY_USER=$OWNER
read -rsp "GitHub token (write:packages): " REGISTRY_TOKEN && echo
export REGISTRY_TOKEN
trap 'unset REGISTRY_TOKEN COSIGN_PRIVATE_KEY COSIGN_PASSWORD' EXIT
if [[ -z "${COSIGN_PRIVATE_KEY:-}" ]]; then
    read -rp "cosign private key [$DEFAULT_KEY]: " key_file
    key_file=${key_file:-$DEFAULT_KEY}
    key_file=${key_file/#\~/$HOME}
    [[ -f "$key_file" ]] || fail "there is no key at $key_file."
    COSIGN_PRIVATE_KEY=$(< "$key_file")
fi
export COSIGN_PRIVATE_KEY
if [[ -z "${COSIGN_PASSWORD+set}" ]]; then
    read -rsp "cosign key password: " COSIGN_PASSWORD && echo
fi
export COSIGN_PASSWORD
# Checks the key before anything is published, so a wrong key or password stops here.
bash scripts/images-sign.sh --check || fail "this key can't sign a release."

repository=$REPOSITORY
echo "== $repository ($version, latest, sha-$sha)"
digest=$(bash scripts/images-build.sh "ghcr.io/$repository" "$version" "$commit") || fail "could not publish the image."
echo "Published ghcr.io/$repository@$digest"
bash scripts/images-sign.sh "ghcr.io/$repository@$digest" \
    || fail "$repository:$version is published but not signed, so the Raspberry Pi won't take it; sign ghcr.io/$repository@$digest with scripts/images-sign.sh."

# Tags the commit the image was built from, so the version maps to its exact code.
git tag "v$version" HEAD || fail "could not tag v$version."
git push --quiet origin "v$version" || fail "could not push the tag v$version; push it with: git push origin v$version"

echo "Published and signed $version (sha-$sha), and tagged v$version. The Raspberry Pi takes it on its next nightly update, between 03:00 and 04:00."
