#!/usr/bin/env bash
set -euo pipefail

# Build Debian (.deb) package for fortiq-service
# Usage: ./packaging/linux/build_deb.sh [version] [target-triple]

VERSION="${1:-0.1.0}"
TARGET="${2:-x86_64-unknown-linux-gnu}"
ARCH="amd64"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

echo "=== Building FORTIQ Debian Package (v${VERSION}, ${ARCH}) ==="

# 1. Locate or build release binary
BIN_PATH="${ROOT_DIR}/target/${TARGET}/release/fortiq-service"
if [[ ! -f "${BIN_PATH}" ]]; then
    BIN_PATH="${ROOT_DIR}/target/release/fortiq-service"
fi

CLI_PATH="${ROOT_DIR}/target/${TARGET}/release/fortiq"
if [[ ! -f "${CLI_PATH}" ]]; then
    CLI_PATH="${ROOT_DIR}/target/release/fortiq"
fi

if [[ ! -f "${BIN_PATH}" ]] || [[ ! -f "${CLI_PATH}" ]]; then
    echo "--> Compiling release binary for ${TARGET}..."
    cargo build --release --target "${TARGET}" -p fortiq-service -p fortiq-cli
    BIN_PATH="${ROOT_DIR}/target/${TARGET}/release/fortiq-service"
    CLI_PATH="${ROOT_DIR}/target/${TARGET}/release/fortiq"
fi

if [[ ! -f "${BIN_PATH}" ]]; then
    echo "ERROR: Release binary not found at ${BIN_PATH}"
    exit 1
fi

# 2. Prepare staging tree
PKG_NAME="fortiq-service"
STAGE_DIR="${ROOT_DIR}/target/debian/${PKG_NAME}_${VERSION}_${ARCH}"
rm -rf "${STAGE_DIR}"
mkdir -p "${STAGE_DIR}/DEBIAN"
mkdir -p "${STAGE_DIR}/usr/bin"
mkdir -p "${STAGE_DIR}/lib/systemd/system"
mkdir -p "${STAGE_DIR}/etc/fortiq"
mkdir -p "${STAGE_DIR}/var/lib/fortiq"

# 3. Copy binaries and system assets
cp -f "${BIN_PATH}" "${STAGE_DIR}/usr/bin/fortiq-service"
chmod 0755 "${STAGE_DIR}/usr/bin/fortiq-service"

if [[ -f "${CLI_PATH}" ]]; then
    cp -f "${CLI_PATH}" "${STAGE_DIR}/usr/bin/fortiq"
    chmod 0755 "${STAGE_DIR}/usr/bin/fortiq"
fi

cp -f "${SCRIPT_DIR}/fortiq.service" "${STAGE_DIR}/lib/systemd/system/fortiq.service"
chmod 0644 "${STAGE_DIR}/lib/systemd/system/fortiq.service"

cp -f "${SCRIPT_DIR}/fortiq.toml.example" "${STAGE_DIR}/etc/fortiq/fortiq.toml.example"
chmod 0644 "${STAGE_DIR}/etc/fortiq/fortiq.toml.example"

# 4. Generate DEBIAN/control
cat <<EOF > "${STAGE_DIR}/DEBIAN/control"
Package: ${PKG_NAME}
Version: ${VERSION}
Section: admin
Priority: optional
Architecture: ${ARCH}
Depends: libc6, systemd
Maintainer: FORTIQ Maintainers <support@fortiq.org>
Description: Sovereign Minimalist P2P Remote Administration Node
 FORTIQ is a sovereign peer-to-peer remote administration system in Rust,
 featuring authenticated QUIC transport, ConPTY/PTY terminal streaming,
 persistent tickets, and strict thin client / fat daemon architecture.
EOF

# 5. Generate DEBIAN/postinst
cat <<'EOF' > "${STAGE_DIR}/DEBIAN/postinst"
#!/bin/sh
set -e

if [ "$1" = "configure" ]; then
    # Reload systemd to recognize new unit
    if command -v systemctl >/dev/null 2>&1; then
        systemctl daemon-reload || true
        echo "FORTIQ service installed."
        echo "Configure /etc/fortiq/fortiq.toml and start with: systemctl start fortiq"
    fi
fi
exit 0
EOF
chmod 0755 "${STAGE_DIR}/DEBIAN/postinst"

# 6. Generate DEBIAN/prerm
cat <<'EOF' > "${STAGE_DIR}/DEBIAN/prerm"
#!/bin/sh
set -e

if [ "$1" = "remove" ] || [ "$1" = "upgrade" ] || [ "$1" = "deconfigure" ]; then
    if command -v systemctl >/dev/null 2>&1; then
        systemctl stop fortiq 2>/dev/null || true
        systemctl disable fortiq 2>/dev/null || true
    fi
fi
exit 0
EOF
chmod 0755 "${STAGE_DIR}/DEBIAN/prerm"

# 7. Generate DEBIAN/postrm
cat <<'EOF' > "${STAGE_DIR}/DEBIAN/postrm"
#!/bin/sh
set -e

if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload || true
fi
exit 0
EOF
chmod 0755 "${STAGE_DIR}/DEBIAN/postrm"

# 8. Build package with dpkg-deb
OUT_DEB="${ROOT_DIR}/target/debian/${PKG_NAME}_${VERSION}_${ARCH}.deb"
mkdir -p "${ROOT_DIR}/target/debian"
dpkg-deb --build --root-owner-group "${STAGE_DIR}" "${OUT_DEB}"

echo "=== Debian package created successfully: ${OUT_DEB} ==="
ls -lh "${OUT_DEB}"
