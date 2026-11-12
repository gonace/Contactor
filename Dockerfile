# syntax=docker/dockerfile:1
# The Contactor image: the app and cloudflared, for linux/arm64 (the Raspberry Pi) and linux/amd64. Built and pushed
# by scripts/images-publish.sh or the Images workflow:
#
#   docker buildx build --platform linux/arm64,linux/amd64 --build-arg VERSION=2026.11.12 --build-arg COMMIT=$(git rev-parse HEAD) .
#
# The .NET images are taken by tag, so every release gets their newest security fixes. cloudflared is pinned by digest:
# it is copied in as it is, and only our own image is checked with cosign.

FROM cloudflare/cloudflared:2026.10.7@sha256:4955524aeaa379e154023d5a027c250f7b35041a2e7e950cf13f144bc9b9c5ba AS cloudflared

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
LABEL org.opencontainers.image.title="Contactor" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.source="https://github.com/gonace/Contactor"
COPY --from=cloudflared /usr/local/bin/cloudflared /usr/local/bin/cloudflared
COPY --from=build /app /app
# Everything the app keeps: the database, its backups and ASP.NET Core's keys. Owned by the image's user (1654), so a
# new named volume is too.
COPY --from=build --chown=1654:1654 /data /data
VOLUME /data
WORKDIR /app
# The .NET image listens on 8080 (ASPNETCORE_HTTP_PORTS) as its user "app" (1654).
EXPOSE 8080
# No shell or curl in the image: the app asks its own /healthz.
HEALTHCHECK --interval=1m --timeout=10s --start-period=1m --start-interval=5s --retries=3 \
    CMD ["dotnet", "/app/Contactor.Web.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/Contactor.Web.dll"]
