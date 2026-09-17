# WSL and VPS Deployment

This guide covers the current native Rust/Slint deployment path. It does not
require Cargo on the managed Windows client, but the VPS and WSL setup starts
from release binaries built in CI or on a build machine.

## VPS Relay (Debian/Ubuntu)

Build the Linux service binary first:

```bash
cargo build --release -p fortiq-service
```

Copy the binary and bootstrap script to the VPS:

```bash
scp target/release/fortiq-service debian@VPS_HOST:/tmp/
scp scripts/bootstrap_vps.sh packaging/systemd/fortiq.service debian@VPS_HOST:/tmp/
ssh debian@VPS_HOST
sudo bash /tmp/bootstrap_vps.sh \
  --binary /tmp/fortiq-service \
  --public-ip VPS_PUBLIC_IPV4 \
  --relay
```

Open the configured UDP port in the VPS firewall/security group:

```bash
sudo ufw allow 4001/udp
sudo systemctl status fortiq
sudo journalctl -u fortiq -f
```

The relay's first startup creates its transport identity under
`/var/lib/fortiq/identity.key`. Record the PeerId from the service log and use
that PeerId in client `[network] relay_peer` addresses. Do not copy private key
files between nodes.

## WSL Peer

Enable systemd in the WSL distribution:

```ini
# /etc/wsl.conf
[boot]
systemd=true
```

Restart WSL from PowerShell:

```powershell
wsl --shutdown
```

Build or copy the Linux binaries inside WSL, then create a peer configuration:

```toml
[node]
name = "wsl-peer"

[identity]
path = "/var/lib/fortiq/identity.key"

[network]
listen_quic = "0.0.0.0:4002"
relay_peer = "/ip4/VPS_PUBLIC_IPV4/udp/4001/quic-v1/p2p/VPS_RELAY_PEER_ID"

[capabilities]
dcutr = true
relay = false
rendezvous = false
relay_rate_limit = true

[ticket]
path = "/var/lib/fortiq/tickets.db"

[ipc]
sock = "/run/fortiq/fortiq.sock"
terminal_sock = "/run/fortiq/fortiq-terminal.sock"
```

Run it directly while validating the node:

```bash
sudo mkdir -p /var/lib/fortiq /run/fortiq
sudo chown -R "$USER":"$USER" /var/lib/fortiq /run/fortiq
./target/release/fortiq-service --config /etc/fortiq/fortiq.toml
```

For a persistent WSL peer, install the repository unit after changing its
`ExecStart` config path and enable it with `systemctl enable --now fortiq`.
Use a different UDP port, identity path, ticket database, and IPC socket for
every local peer.

## Network checklist

- VPS relay and WSL/client UDP listen ports are allowed by host and cloud firewalls.
- Every node has its own identity key and Genesis file where operator unlock is required.
- `relay_peer` includes the relay PeerId and uses `/p2p-circuit` only for a target address.
- The managed ticket owner explicitly enables remote access before shell use.
- Shell traffic uses `/fortiq/shell/next`; legacy `/fortiq/shell/2.0` is rejected.
