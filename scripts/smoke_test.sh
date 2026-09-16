#!/usr/bin/env bash
set -euo pipefail

echo "=== FORTIQ protocol smoke tests (isolated test harness) ==="
cargo test -p fortiq-p2p --test e2e_two_nodes --test e2e_relay_three_nodes
