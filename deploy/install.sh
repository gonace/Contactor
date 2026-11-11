#!/usr/bin/env bash
# Installs or updates Contactor on a Raspberry Pi with Raspberry Pi OS (64-bit), or any Debian or Ubuntu machine.
# Copy the deploy folder to the Raspberry Pi and run it there:
#
#   scp -r deploy <pi>:contactor-deploy
#   ssh -t <pi> sudo contactor-deploy/install.sh
#
# (-t, so it can ask its questions; without a terminal give every value as a variable.)
#
# Everything it needs is in that folder, so the Raspberry Pi never reads the (private) repository. Running it again
# updates: compose.yml and the upgrade script are rewritten and the newest signed image is started; the settings in
# /opt/contactor/.env are kept. Asked for on the first run, or given as variables:
#   CF_ACCESS_TEAM_DOMAIN    the Cloudflare Access team domain, e.g. myteam.cloudflareaccess.com
#   CF_ACCESS_AUDIENCE       the AUD tag of the app's Access application
#   CLOUDFLARE_TUNNEL_TOKEN  the token of the Cloudflare tunnel made for Contactor
#   GITHUB_TOKEN             a classic GitHub token with only read:packages, to pull the private image
#   VM201_HOST               the relay board's first address, e.g. 192.168.1.50; change it later in
#                            the app under Settings, where the saved address wins over this one
# Optional:
#   VM201_USERNAME, VM201_PASSWORD  the board's login, when it has one (max 8 characters each)
#   CONTACTOR_IMAGE        another image, e.g. a pinned version: ghcr.io/gonace/contactor:2026.11.4
set -euo pipefail

readonly DIR=/opt/contactor
readonly DATA=/var/lib/contactor
# The image's user (the .NET images' "app" user), who must own the database folder.
readonly APP_UID=1654
readonly HERE="$(cd "$(dirname "$0")" && pwd)"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nError: %s\n' "$*" >&2; exit 1; }

# A value from .env, without the single quotes set_env puts around it.
get_env() {
    local value
    value=$(grep "^$1=" "$DIR/.env" | cut -d= -f2- || true)
    value=${value#\'}
    printf '%s' "${value%\'}"
}

# Sets KEY='value' in .env, keeping every other line. In single quotes Compose takes the value as it is: unquoted, it
# would expand a "$" and cut at " #", e.g. in a password. So a value can't hold a single quote or a newline.
set_env() {
    local key="$1" value="$2" rest
    [[ "$value" != *$'\n'* ]] || fail "$key must be one line."
    [[ "$value" != *\'* ]] || fail "$key can't hold a single quote (')."
    rest=$(grep -v "^${key}=" "$DIR/.env" || true)
    # Written readable by root only: the new file would otherwise get the default mode and the secrets with it.
    (umask 077 && { [[ -n "$rest" ]] && printf '%s\n' "$rest"; printf "%s='%s'\n" "$key" "$value"; } > "$DIR/.env.new")
    mv "$DIR/.env.new" "$DIR/.env"
}

# Asks on the terminal; a secret isn't shown as it's typed.
ask() {
    local prompt="$1" secret="${2:-}" answer
    if ! { : < /dev/tty; } 2> /dev/null; then
        fail "no terminal to ask for \"$prompt\" on: run it with ssh -t, or give the value as a variable (see the top of this script)."
    fi
    if [[ -n "$secret" ]]; then
        read -rsp "$prompt: " answer < /dev/tty && echo > /dev/tty
    else
        read -rp "$prompt: " answer < /dev/tty
    fi
    printf '%s' "$answer"
}

# A value from the variable, else from .env, else asked for.
setting() {
    local key="$1" prompt="$2" secret="${3:-}" value
    value="${!key:-$(get_env "$key")}"
    if [[ -z "$value" ]]; then
        value="$(ask "$prompt" "$secret")"
    fi
    printf '%s' "$value"
}

[[ $EUID -eq 0 ]] || fail "run it as root, e.g. with sudo."
[[ -f "$HERE/compose.yml" && -f "$HERE/upgrade.sh" ]] || fail "compose.yml and upgrade.sh must be next to this script."

if ! command -v docker > /dev/null; then
    log "Installing Docker"
    command -v curl > /dev/null || { apt-get update -qq && apt-get install -y -qq curl > /dev/null; }
    curl -fsSL https://get.docker.com | sh
fi
if [[ -d /run/systemd/system ]]; then
    systemctl enable --now docker > /dev/null
fi

log "Writing $DIR"
# It holds secrets: readable by root only.
mkdir -p "$DIR"
chmod 700 "$DIR"
install -m 0600 "$HERE/compose.yml" "$DIR/compose.yml"
touch "$DIR/.env"
chmod 600 "$DIR/.env"

team_domain="$(setting CF_ACCESS_TEAM_DOMAIN "Cloudflare Access team domain, e.g. myteam.cloudflareaccess.com")"
[[ "$team_domain" =~ ^[a-z0-9-]+\.cloudflareaccess\.com$ ]] \
    || fail "the team domain looks like myteam.cloudflareaccess.com; it is shown in Zero Trust → Settings."
set_env CF_ACCESS_TEAM_DOMAIN "$team_domain"

audience="$(setting CF_ACCESS_AUDIENCE "AUD tag of the Access application")"
[[ "$audience" =~ ^[0-9a-f]{64}$ ]] \
    || fail "an AUD tag is 64 characters 0-9 and a-f; it is in Zero Trust → Access controls → Applications → Edit → Additional settings."
set_env CF_ACCESS_AUDIENCE "$audience"

tunnel_token="$(setting CLOUDFLARE_TUNNEL_TOKEN "Cloudflare tunnel token" secret)"
[[ -n "$tunnel_token" && "$tunnel_token" != *[[:space:]]* ]] || fail "the Cloudflare tunnel token is needed."
set_env CLOUDFLARE_TUNNEL_TOKEN "$tunnel_token"

host="${VM201_HOST:-$(get_env VM201_HOST)}"
if [[ -z "$host" ]]; then
    host="$(ask "The relay board's address, e.g. 192.168.1.50")"
fi
[[ -n "$host" ]] || fail "the relay board's address is needed."
[[ "$host" =~ ^[A-Za-z0-9.:-]+$ ]] || fail "$host is not an address."
set_env VM201_HOST "$host"

# The board's login is optional: most boards have none. Only given as variables, or kept from an earlier run.
for key in VM201_USERNAME VM201_PASSWORD; do
    value="${!key:-$(get_env "$key")}"
    (( ${#value} <= 8 )) || fail "$key has at most 8 characters on the VM201."
    set_env "$key" "$value"
done

if [[ -n "${CONTACTOR_IMAGE:-}" ]]; then
    set_env CONTACTOR_IMAGE "$CONTACTOR_IMAGE"
fi

# The image is private; Docker keeps the login, so the token is only asked for once.
if ! grep -q '"ghcr.io"' /root/.docker/config.json 2> /dev/null; then
    log "Logging in to ghcr.io"
    github_token="${GITHUB_TOKEN:-$(ask "GitHub token (classic, read:packages only)" secret)}"
    [[ -n "$github_token" ]] || fail "a GitHub token is needed to pull the private image."
    printf '%s' "$github_token" | docker login ghcr.io -u token --password-stdin > /dev/null \
        || fail "ghcr.io refused the token; it needs the read:packages scope."
fi

log "Preparing $DATA for the database and its backups"
mkdir -p "$DATA/backups"
chown -R "$APP_UID:$APP_UID" "$DATA"
chmod 750 "$DATA" "$DATA/backups"

log "Installing the upgrade as /usr/local/sbin/contactor-upgrade"
install -m 0755 "$HERE/upgrade.sh" /usr/local/sbin/contactor-upgrade
if [[ -f "$HERE/autoupdate-install.sh" ]]; then
    install -m 0755 "$HERE/autoupdate-install.sh" /usr/local/sbin/contactor-autoupdate-install
fi

log "Starting the newest signed image"
/usr/local/sbin/contactor-upgrade

log "Done"
echo "Open the app at the hostname of its Access application. For nightly updates, run once: sudo contactor-autoupdate-install"
