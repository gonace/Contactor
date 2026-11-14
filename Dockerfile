# syntax=docker/dockerfile:1
# The Contactor image: the app, cloudflared and Tailscale, for linux/arm64 (the Raspberry Pi) and linux/amd64. Built
# and pushed by scripts/images-publish.sh or the Images workflow:
#
#   docker buildx build --platform linux/arm64,linux/amd64 --build-arg VERSION=2026.11.12 --build-arg COMMIT=$(git rev-parse HEAD) .
#
# The .NET images are taken by tag, so every release gets their newest security fixes. cloudflared and Tailscale are
# pinned by digest: they are copied in as they are, and only our own image is checked with cosign. The Aspire run
# builds this image too, so development has the same cloudflared and Tailscale.

FROM cloudflare/cloudflared:2026.10.7@sha256:4955524aeaa379e154023d5a027c250f7b35041a2e7e950cf13f144bc9b9c5ba AS cloudflared
# Raising Tailscale: check that tailscaled still passes connections from the Tailscale network only to 127.0.0.1
# (wgengine/netstack), never to Serve's 127.0.0.2:8081 (Tailscale identity relies on it).
FROM tailscale/tailscale:v1.104.7@sha256:72c1e19e0aedca96b8533bbb0b4368564410ff20b17e53b804eb6c1f04a31be4 AS tailscale

# Built on the machine's own platform and cross-compiled for the target: no emulation needed.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
ARG COMMIT=
WORKDIR /src
COPY . .
RUN dotnet publish Contactor.Web -c Release -a "$TARGETARCH" -o /app \
        -p:Version="$VERSION" -p:SourceRevisionId="$COMMIT" \
    && mkdir /data

# Chiseled (no shell, no package manager); "extra" adds the time zone data the schedules need and ICU.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute-chiseled-extra
ARG VERSION=0.0.0-dev
ARG COMMIT=
LABEL org.opencontainers.image.title="Contactor" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$COMMIT" \
      org.opencontainers.image.source="https://github.com/gonace/Contactor"
COPY --from=cloudflared /usr/local/bin/cloudflared /usr/local/bin/cloudflared
# Static Go programs; the app runs them only while Tailscale is the way in, with their state in /data/tailscale.
COPY --from=tailscale /usr/local/bin/tailscaled /usr/local/bin/tailscale /usr/local/bin/
COPY --from=build /app /app
# Our licence and the others' (cloudflared's Apache 2.0 and Tailscale's BSD licence ask for their texts to go with them).
COPY LICENSE THIRD-PARTY-NOTICES.md /licenses/
COPY licenses/ /licenses/
# Everything the app keeps: the database, its backups and ASP.NET Core's keys. Owned by the image's user (1654), so a
# new named volume is too.
COPY --from=build --chown=1654:1654 /data /data
VOLUME /data
WORKDIR /app
# The .NET image listens on 8080 (ASPNETCORE_HTTP_PORTS) as its user "app" (1654). Also on 127.0.0.2:8081 for Tailscale
# Serve only: Tailscale identity believes only requests that come in there. tailscaled passes connections from the
# Tailscale network only to 127.0.0.1, so they never reach it past Serve.
ENV Tailscale__ServeAddress=127.0.0.2:8081
EXPOSE 8080
# No shell or curl in the image: the app asks its own /healthz.
HEALTHCHECK --interval=1m --timeout=10s --start-period=1m --start-interval=5s --retries=3 \
    CMD ["dotnet", "/app/Contactor.Web.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/Contactor.Web.dll"]
