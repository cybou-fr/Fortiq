#!/usr/bin/env bash
set -euo pipefail

PREFIX=/usr
CONFIG=/etc/fortiq/fortiq.toml
STATE=/var/lib/fortiq
BINARY=""
NAME=fortiq-vps-relay
PUBLIC_IP=""
PORT=4001
RELAY=false
RENDEZVOUS=true

usage() {
    cat <<'EOF'
Usage: bootstrap_vps.sh --binary PATH --public-ip IP [options]

Installs a FORTIQ Linux service from a locally built fortiq-service binary.
Options:
  --binary PATH       path to target/release/fortiq-service
  --public-ip IP      public IPv4 address advertised for QUIC
  --port PORT         UDP listen port (default: 4001)
  --name NAME         node name (default: fortiq-vps-relay)
  --relay             enable Circuit Relay v2 service
  --no-rendezvous     disable Rendezvous service
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --binary) BINARY=$2; shift 2 ;;
        --public-ip) PUBLIC_IP=$2; shift 2 ;;
        --port) PORT=$2; shift 2 ;;
        --name) NAME=$2; shift 2 ;;
        --relay) RELAY=true; shift ;;
        --no-rendezvous) RENDEZVOUS=false; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [[ "$(id -u)" -ne 0 ]]; then
    echo "Run this script as root (sudo)." >&2
    exit 1
fi
if [[ -z "$BINARY" || -z "$PUBLIC_IP" ]]; then
    echo "--binary and --public-ip are required." >&2
    usage >&2
    exit 2
fi
if [[ ! -x "$BINARY" ]]; then
    echo "Binary is missing or not executable: $BINARY" >&2
    exit 1
fi

if ! id fortiq >/dev/null 2>&1; then
    useradd --system --home-dir /var/lib/fortiq --create-home --shell /usr/sbin/nologin fortiq
fi

install -d -o root -g root -m 0755 /etc/fortiq /run/fortiq
install -d -o fortiq -g fortiq -m 0700 "$STATE"
install -o root -g root -m 0755 "$BINARY" "$PREFIX/bin/fortiq-service"
install -o root -g root -m 0644 packaging/systemd/fortiq.service /etc/systemd/system/fortiq.service

cat > "$CONFIG" <<EOF
[node]
name = "$NAME"

[identity]
path = "$STATE/identity.key"

[network]
listen_quic = "0.0.0.0:$PORT"
public_addr = "/ip4/$PUBLIC_IP/udp/$PORT/quic-v1"

[capabilities]
rendezvous = $RENDEZVOUS
relay = $RELAY
dcutr = false
relay_rate_limit = true

[ticket]
path = "$STATE/tickets.db"

[ipc]
sock = "/run/fortiq/fortiq.sock"
terminal_sock = "/run/fortiq/fortiq-terminal.sock"
EOF
chmod 0640 "$CONFIG"
chown root:fortiq "$CONFIG"

systemctl daemon-reload
systemctl enable --now fortiq.service
systemctl --no-pager --full status fortiq.service
printf '\nFORTIQ VPS relay installed. Allow UDP port %s in the VPS firewall.\n' "$PORT"
