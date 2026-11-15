# syntax=docker/dockerfile:1
# The demo's image: Contactor.Demo with the published app and simulator it starts for each visitor, for linux/amd64
# and linux/arm64. Built from the repository's root, with Dockerfile.dockerignore next to this file:
#
#   docker buildx build --platform linux/amd64 -f Contactor.Demo/Dockerfile --build-arg VERSION=2026.11.12 --build-arg COMMIT=$(git rev-parse HEAD) .
#
# Published and signed with the app's image (scripts/images-publish.sh or the Images workflow). No cloudflared or
# Tailscale inside: the tunnel is a container of its own (deploy/demo/compose.yml).

# Built on the machine's own platform and cross-compiled for the target: no emulation needed.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
ARG COMMIT=
WORKDIR /src
COPY . .
# Three programs, each in a folder of its own with its own settings: the demo, and the app and the simulator it starts.
RUN for project in Contactor.Demo:demo Contactor.Web:web Contactor.Simulator:simulator; do \
        dotnet publish "${project%%:*}" -c Release -a "$TARGETARCH" -o "/app/${project##*:}" \
            -p:Version="$VERSION" -p:SourceRevisionId="$COMMIT" || exit 1; \
    done \
    && mkdir -p /data/copies

# Chiseled (no shell, no package manager), as the app's image; "extra" adds the time zone data the copies' schedules
# need and ICU.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute-chiseled-extra
ARG VERSION=0.0.0-dev
ARG COMMIT=
LABEL org.opencontainers.image.title="Contactor demo" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$COMMIT" \
      org.opencontainers.image.source="https://github.com/gonace/Contactor"
COPY --from=build /app /app
COPY LICENSE THIRD-PARTY-NOTICES.md /licenses/
COPY licenses/ /licenses/
# The visitors' copies, each in a folder of its own that is deleted when the copy ends. Owned by the image's user
# (1654), so a new named volume is too.
COPY --from=build --chown=1654:1654 /data /data
VOLUME /data
WORKDIR /app/demo
# The garbage collector's low-memory mode, for the demo and every copy (they inherit DOTNET_GC* settings): about
# 250 MB per visitor instead of a server heap per process.
ENV DOTNET_gcServer=0 \
    Demo__AppPath=/app/web/Contactor.Web.dll \
    Demo__SimulatorPath=/app/simulator/Contactor.Simulator.dll \
    Demo__DataRoot=/data/copies
# The .NET image listens on 8080 (ASPNETCORE_HTTP_PORTS) as its user "app" (1654). The copies get none of the ASPNETCORE_
# settings: each listens on 127.0.0.1 only, on a port of its own.
EXPOSE 8080
# No shell or curl in the image: the demo asks its own /healthz.
HEALTHCHECK --interval=1m --timeout=10s --start-period=1m --start-interval=5s --retries=3 \
    CMD ["dotnet", "/app/demo/Contactor.Demo.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/demo/Contactor.Demo.dll"]
