#!/usr/bin/env bash
# Makes Contactor upgrade itself: a systemd timer runs contactor-upgrade (installed by install.sh) every night
# between 03:00 and 04:00 local time (Europe/Stockholm by default), when nobody is likely to be using it. A run with an unchanged image leaves the app
# untouched, and only images signed with our key are run. Missed nights are not made up at boot, so the app never
# restarts in the middle of the day. Safe to run again.
#
#   sudo contactor-autoupdate-install
#
# The runs log to the journal: journalctl -u contactor-upgrade
set -euo pipefail

readonly NAME=contactor-upgrade
readonly TARGET=/usr/local/sbin/$NAME
readonly UNITS=/etc/systemd/system

fail() { echo "Error: $*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || fail "run it as root, e.g. with sudo."
command -v systemctl > /dev/null || fail "this machine has no systemd."
[[ -x "$TARGET" ]] || fail "$TARGET is missing; run install.sh first."

echo "Installing $NAME.service and $NAME.timer"
# HOME, so the upgrade finds root's Docker login for the private image.
cat > "$UNITS/$NAME.service" << EOF
[Unit]
Description=Upgrade Contactor to the newest signed image
Wants=network-online.target
After=network-online.target docker.service
Requires=docker.service

[Service]
Type=oneshot
Environment=HOME=/root
ExecStart=$TARGET
EOF

cat > "$UNITS/$NAME.timer" << EOF
[Unit]
Description=Upgrade Contactor every night

[Timer]
OnCalendar=*-*-* 03:00:00 Europe/Stockholm
RandomizedDelaySec=1h
Persistent=false

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now "$NAME.timer"

echo "Done. Next run: systemctl list-timers $NAME.timer; logs: journalctl -u $NAME"
