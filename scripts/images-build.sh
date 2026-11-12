#!/usr/bin/env bash
# Builds the image from the Dockerfile for linux/arm64 and linux/amd64, pushes it, and prints the digest of the image
# index it pushed, so the caller signs exactly that (images-sign.sh). Used by images-publish.sh and the Images
# workflow. The registry login (REGISTRY_USER and REGISTRY_TOKEN) goes in a Docker config of its own, written
# directly rather than with docker login, which would hand the token to the computer's credential store; the folder is
# removed again on exit, so the token is neither on the command line nor saved. Docker's contexts, plugins and builders
# are taken from your own config.
#
#   bash scripts/images-build.sh ghcr.io/gonace/contactor 2026.11.12 <commit>
set -euo pipefail

readonly IMAGE=$1 VERSION=$2 COMMIT=$3

fail() { echo "Error: $*" >&2; exit 1; }

cd "$(dirname "$0")/.."
[[ -n "${REGISTRY_TOKEN:-}" ]] || fail "REGISTRY_TOKEN isn't set."
host=${IMAGE%%/*}
mine=${DOCKER_CONFIG:-$HOME/.docker}
config=$(mktemp -d)
metadata=$(mktemp)
trap 'rm -rf "$config" "$metadata"' EXIT
for item in cli-plugins contexts buildx; do
    # Git Bash copies instead of linking; that works too.
    if [[ -e "$mine/$item" ]]; then
        ln -s "$mine/$item" "$config/$item"
    fi
done
context=$(sed -n 's/.*"currentContext": *"\([^"]*\)".*/\1/p' "$mine/config.json" 2> /dev/null || true)
auth=$(printf '%s:%s' "${REGISTRY_USER:-token}" "$REGISTRY_TOKEN" | base64 | tr -d '\n')
(umask 077 && printf '{"auths":{"%s":{"auth":"%s"}}%s}\n' "$host" "$auth" "${context:+,\"currentContext\":\"$context\"}" > "$config/config.json")
# Git Bash: docker.exe needs the folder as a Windows path.
if command -v cygpath > /dev/null; then
    DOCKER_CONFIG=$(cygpath -w "$config")
else
    DOCKER_CONFIG=$config
fi
export DOCKER_CONFIG

# A builder that can push several platforms at once, named rather than made the default; the image's own build needs no
# emulation (see the Dockerfile).
docker buildx inspect contactor > /dev/null 2>&1 \
    || docker buildx create --name contactor --driver docker-container > /dev/null \
    || fail "could not create the buildx builder."

docker buildx build . \
    --builder contactor \
    --platform linux/arm64,linux/amd64 \
    --build-arg VERSION="$VERSION" \
    --build-arg COMMIT="$COMMIT" \
    --tag "$IMAGE:$VERSION" \
    --tag "$IMAGE:latest" \
    --tag "$IMAGE:sha-${COMMIT::7}" \
    --metadata-file "$metadata" \
    --push >&2 || fail "could not build and push $IMAGE:$VERSION."

digest=$(sed -n 's/.*"containerimage.digest": *"\(sha256:[0-9a-f]\{64\}\)".*/\1/p' "$metadata")
[[ -n "$digest" ]] || fail "the build gave no image digest."
echo "$digest"
