#!/usr/bin/env bash
# Publishes the image to ghcr.io from this computer, like the Images workflow does, for when there are no Actions
# minutes: it computes the release version (YYYY.M.D in UTC, plus .N for a later release the same day, see
# release-version.sh), builds and runs every test (nothing is published unless they pass), publishes the image (linux-arm64 and linux-x64) tagged with the version, `latest` and
# `sha-<commit>`, signs it by the digest the publish pushed (images-sign.sh), then pushes the tag v<version> to the
# commit it published. Run it from a clean checkout of main, with the .NET SDK and cosign installed. It asks for a
# classic GitHub token with write:packages, the cosign private key's file (~/contactor-keys/contactor-main.key
# unless you name another) and its password; none of them is shown or saved. COSIGN_PRIVATE_KEY (the key itself) and
# COSIGN_PASSWORD, when set, are used instead of asking.
#
#   bash scripts/images-publish.sh
set -euo pipefail

readonly OWNER=gonace
readonly PROJECT=Contactor.Web
readonly BRANCH=main
readonly DEFAULT_KEY=~/contactor-keys/contactor-main.key

fail() { echo "Error: $*" >&2; exit 1; }

cd "$(dirname "$0")/.."
git fetch --quiet origin "$BRANCH" || fail "could not fetch $BRANCH."
[[ "$(git branch --show-current)" == "$BRANCH" ]] || fail "check out $BRANCH first (git checkout $BRANCH && git pull)."
[[ -z "$(git status --porcelain)" ]] || fail "this checkout has changes; publish only what is committed."
[[ "$(git rev-parse HEAD)" == "$(git rev-parse "origin/$BRANCH")" ]] || fail "this checkout isn't the latest $BRANCH; run git pull first."
command -v cosign > /dev/null || fail "cosign isn't installed; see https://docs.sigstore.dev/cosign/system_config/installation/"

# The Raspberry Pi takes a new image the same night, so nothing is published unless every test passes.
echo "== Building and testing"
dotnet build Contactor.slnx -c Release || fail "the build failed; nothing was published."
dotnet test --solution Contactor.slnx --no-build -c Release || fail "a test failed; nothing was published."

sha=$(git rev-parse --short=7 HEAD)
version=$(bash scripts/release-version.sh) || fail "could not compute the version."
export SDK_CONTAINER_REGISTRY_UNAME=$OWNER
read -rsp "GitHub token (write:packages): " SDK_CONTAINER_REGISTRY_PWORD && echo
export SDK_CONTAINER_REGISTRY_PWORD
trap 'unset SDK_CONTAINER_REGISTRY_PWORD COSIGN_PRIVATE_KEY COSIGN_PASSWORD' EXIT
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

repository="$OWNER/$(dotnet msbuild "$PROJECT" -getProperty:ContainerRepository)"
echo "== $repository ($version, latest, sha-$sha)"
# -t, not /t: Git Bash turns arguments that start with / into Windows paths. -getProperty prints the image index
# (the manifest list of the linux-arm64 and linux-x64 images) exactly as the publish pushed it; errors still show.
index=$(dotnet publish "$PROJECT" -c Release -t:PublishContainer \
    -p:Version="$version" \
    -p:ContainerRegistry=ghcr.io \
    -p:ContainerRepository="$repository" \
    -p:ContainerImageTags="\"$version;latest;sha-$sha\"" \
    -getProperty:GeneratedImageIndex) \
    || fail "could not publish $PROJECT."
[[ "$index" == '{'*'"manifests"'*'}' ]] || fail "publishing $PROJECT gave no image index."
# Its digest is the hash of those bytes, so this signs exactly what was pushed, whatever the tags point to now.
digest="sha256:$(printf '%s' "$index" | sha256sum | cut -d' ' -f1)"
echo "Published ghcr.io/$repository@$digest"
bash scripts/images-sign.sh "ghcr.io/$repository@$digest" \
    || fail "$repository:$version is published but not signed, so the Raspberry Pi won't take it; sign ghcr.io/$repository@$digest with scripts/images-sign.sh."

# Tags the commit the image was built from, so the version maps to its exact code.
git tag "v$version" HEAD || fail "could not tag v$version."
git push --quiet origin "v$version" || fail "could not push the tag v$version; push it with: git push origin v$version"

echo "Published and signed $version (sha-$sha), and tagged v$version. The Raspberry Pi takes it on its next nightly update, between 03:00 and 04:00."
