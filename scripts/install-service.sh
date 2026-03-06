#!/usr/bin/env bash
# install-service.sh
# Installs (or reinstalls) the StarTracker systemd service.
# Run as root on the Raspberry Pi: sudo bash install-service.sh
# Optionally pass the deploy directory as first arg (default: directory of this script).
set -euo pipefail

DEPLOY_DIR="${1:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)}"
APP_BIN="$DEPLOY_DIR/BPrpi4SW"
BOOT_PY="$DEPLOY_DIR/lcd_boot.py"
SERVICE_NAME="startracker"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

# ── Sanity checks ─────────────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
    echo "ERROR: run this script as root (sudo bash install-service.sh)" >&2
    exit 1
fi

if [[ ! -f "$APP_BIN" ]]; then
    echo "ERROR: C# binary not found at $APP_BIN" >&2
    exit 1
fi

if [[ ! -f "$BOOT_PY" ]]; then
    echo "ERROR: lcd_boot.py not found at $BOOT_PY" >&2
    exit 1
fi

# ── Ensure Python RPi.GPIO is available ──────────────────────────────────────
if ! python3 -c "import RPi.GPIO" 2>/dev/null; then
    echo "Installing python3-rpi-lgpio (RPi.GPIO compatible for Bookworm)..."
    apt-get install -y python3-rpi-lgpio || apt-get install -y python3-rpi.gpio
fi

# ── Mark scripts executable ───────────────────────────────────────────────────
chmod +x "$APP_BIN"
chmod +x "$BOOT_PY"

# ── Write the systemd unit ────────────────────────────────────────────────────
cat > "$SERVICE_FILE" << EOF
[Unit]
Description=StarTracker Control System (BPrpi4SW)
Documentation=https://github.com/tadeas/BPrpi4SW
# Start after basic system init and filesystems are mounted.
# We do NOT require network-online here — the app creates its own hotspot
# if no network is available.
After=sysinit.target local-fs.target
Wants=sysinit.target local-fs.target

[Service]
Type=simple
# lcd_boot.py shows boot progress on the LCD, then os.execv() into BPrpi4SW.
# systemd tracks the resulting C# process seamlessly.
ExecStart=/usr/bin/python3 ${BOOT_PY}
WorkingDirectory=${DEPLOY_DIR}
Restart=on-failure
RestartSec=5
# GPIO access requires root (or the gpio group — adjust User/Group as needed)
User=root
StandardOutput=journal
StandardError=journal
SyslogIdentifier=startracker

[Install]
WantedBy=multi-user.target
EOF

echo "Wrote $SERVICE_FILE"

# ── Enable and (re)start ─────────────────────────────────────────────────────
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"
echo "Service '$SERVICE_NAME' enabled and started."
echo ""
echo "  Useful commands:"
echo "    sudo journalctl -u $SERVICE_NAME -f   # live log"
echo "    sudo systemctl status $SERVICE_NAME   # status"
echo "    sudo systemctl stop   $SERVICE_NAME   # stop"
