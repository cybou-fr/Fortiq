#!/usr/bin/env bash
set -euo pipefail

# Automated smoke test for FORTIQ (Linux / macOS / WSL).
# Validates identity creation, HELLO handshake, remote command execution, and ticket closure.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

echo "=== FORTIQ Automated Smoke Test (Unix/Linux) ==="

echo "--> Building fortiq-service..."
cargo build -p fortiq-service

EXE="${ROOT_DIR}/target/debug/fortiq-service"
TEMP_DIR="$(mktemp -d)"

cleanup() {
    if [[ -n "${MANAGED_PID:-}" ]]; then
        kill -9 "${MANAGED_PID}" 2>/dev/null || true
    fi
    rm -rf "${TEMP_DIR}"
}
trap cleanup EXIT

OP_PORT=49162
MANAGED_PORT=49163

OP_ID="${TEMP_DIR}/operator.id"
MANAGED_ID="${TEMP_DIR}/managed.id"
MANAGED_TICKET="${TEMP_DIR}/managed.ticket.json"

echo "--> Initializing operator identity..."
cat <<EOF > "${TEMP_DIR}/operator.toml"
[node]
name = "smoke-operator"
[identity]
path = "${OP_ID}"
[network]
listen_quic = "127.0.0.1:${OP_PORT}"
EOF

"${EXE}" --config "${TEMP_DIR}/operator.toml" ticket status >/dev/null 2>&1 || true
OP_INIT_LOG="${TEMP_DIR}/op_init.log"
"${EXE}" --config "${TEMP_DIR}/operator.toml" > "${OP_INIT_LOG}" 2>&1 &
OP_INIT_PID=$!
sleep 1
kill "${OP_INIT_PID}" 2>/dev/null || true
OP_PEER_ID=$(grep -E "^12D3KooW" "${OP_INIT_LOG}" | head -n 1 || grep -o "12D3KooW[a-zA-Z0-9]*" "${OP_INIT_LOG}" | head -n 1)
echo "Operator PeerId: ${OP_PEER_ID}"

echo "--> Configuring managed peer..."
cat <<EOF > "${TEMP_DIR}/managed.toml"
[node]
name = "smoke-managed"
[identity]
path = "${MANAGED_ID}"
[authorization]
operator_peer_id = "${OP_PEER_ID}"
[network]
listen_quic = "127.0.0.1:${MANAGED_PORT}"
[ticket]
path = "${MANAGED_TICKET}"
EOF

echo "--> Opening support ticket on managed peer..."
"${EXE}" --config "${TEMP_DIR}/managed.toml" ticket open
"${EXE}" --config "${TEMP_DIR}/managed.toml" ticket status

echo "--> Starting managed node..."
MANAGED_LOG="${TEMP_DIR}/managed.log"
"${EXE}" --config "${TEMP_DIR}/managed.toml" > "${MANAGED_LOG}" 2>&1 &
MANAGED_PID=$!
sleep 2

MANAGED_PEER_ID=$(grep -o "12D3KooW[a-zA-Z0-9]*" "${MANAGED_LOG}" | head -n 1)
echo "Managed PeerId: ${MANAGED_PEER_ID}"

MANAGED_DIAL_ADDR="/ip4/127.0.0.1/udp/${MANAGED_PORT}/quic-v1/p2p/${MANAGED_PEER_ID}"

echo "--> Testing remote command execution via P2P stream..."
"${EXE}" --config "${TEMP_DIR}/operator.toml" \
  --dial "${MANAGED_DIAL_ADDR}" \
  --shell "${MANAGED_PEER_ID}" \
  --command "uname -a || whoami"

echo "--> Closing ticket remotely..."
"${EXE}" --config "${TEMP_DIR}/operator.toml" ticket close \
  --peer "${MANAGED_PEER_ID}" \
  --dial "${MANAGED_DIAL_ADDR}"

echo "--> Verifying closed ticket state..."
STATUS=$("${EXE}" --config "${TEMP_DIR}/managed.toml" ticket status)
echo "Status: ${STATUS}"

if [[ "${STATUS}" != *"CLOSED"* ]]; then
    echo "ERROR: Ticket was not closed properly!"
    exit 1
fi

echo "=== Smoke Test PASSED Successfully! ==="
