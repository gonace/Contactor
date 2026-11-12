#!/usr/bin/env bash
# Builds the image from the Dockerfile for linux/arm64 and linux/amd64, pushes it, and prints the digest of the image
# index it pushed, so the caller signs exactly that (images-sign.sh). Used by images-publish.sh and the Images
# workflow; it logs in to the registry with REGISTRY_USER and REGISTRY_TOKEN in a Docker config of its own, removed
# again on exit, so the token is neither on the command line nor saved.
#
#   bash scripts/images-build.sh ghcr.io/gonace/contactor 2026.11.12 <commit>
set -euo pipefail

readonly IMAGE=$1 VERSION=$2 COMMIT=$3

fail() { echo "Error: $*" >&2; exit 1; }

cd "$(dirname "$0")/.."
[[ -n "${REGISTRY_TOKEN:-}" ]] || fail "REGISTRY_TOKEN isn't set."
config=$(mktemp -d)
metadata=$(mktemp)
trap 'rm -rf "$config" "$metadata"' EXIT
# The builder and its plugins stay where Docker keeps them; only the login is our own.
[[ -d "${DOCKER_CONFIG:-$HOME/.docker}/cli-plugins" ]] && ln -s "${DOCKER_CONFIG:-$HOME/.docker}/cli-plugins" "$config/cli-plugins"
export DOCKER_CONFIG=$config
printf '%s' "$REGISTRY_TOKEN" | docker login "${IMAGE%%/*}" -u "${REGISTRY_USER:-token}" --password-stdin > /dev/null \
    || fail "could not log in to ${IMAGE%%/*}."

# A builder that can push several platforms at once; the image's own build needs no emulation (see the Dockerfile).
docker buildx create --name contactor --driver docker-container --use > /dev/null 2>&1 || docker buildx use contactor

docker buildx build . \
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
