#!/usr/bin/env bash
# install-gphoto2.sh
# Install libgphoto2 runtime and USB access on Raspberry Pi (Bookworm/Bullseye).
#
# Usage:
#   sudo bash install-gphoto2.sh [app_dir] [run_user]
#
# Examples:
#   sudo bash install-gphoto2.sh /home/tada66/BPrpi4SW tada66
#   sudo bash install-gphoto2.sh

set -euo pipefail

APP_DIR="${1:-/home/tada66/BPrpi4SW}"
RUN_USER="${2:-${SUDO_USER:-pi}}"
UDEV_RULE_FILE="/etc/udev/rules.d/99-bprpi4sw-camera.rules"

if [[ $EUID -ne 0 ]]; then
    echo "ERROR: run as root (sudo bash install-gphoto2.sh)" >&2
    exit 1
fi

echo "Installing libgphoto2 and USB support packages..."
apt-get update -qq
apt-get install -y \
    libgphoto2-6 \
    libgphoto2-port12 \
    gphoto2 \
    libusb-1.0-0 \
    usbutils \
    udev

echo "Configuring USB camera permissions (udev)..."
# This rule grants plugdev access to devices detected by libgphoto2.
cat > "$UDEV_RULE_FILE" << 'EOF'
SUBSYSTEM=="usb", ENV{ID_GPHOTO2}=="1", MODE="0664", GROUP="plugdev"
EOF

if getent group plugdev >/dev/null 2>&1; then
    usermod -aG plugdev "$RUN_USER" || true
fi

udevadm control --reload-rules
udevadm trigger

LIB_PATH="$(ldconfig -p | awk '/libgphoto2\.so\.6 \(/{print $NF; exit}')"
if [[ -z "$LIB_PATH" ]]; then
    echo "ERROR: libgphoto2.so.6 not found by ldconfig after install." >&2
    echo "Try: ldconfig -p | grep libgphoto2" >&2
    exit 1
fi

echo "Found libgphoto2 at: $LIB_PATH"

# Optional compatibility symlink for deployments that probe app-local first.
if [[ -d "$APP_DIR" ]]; then
    ln -sf "$LIB_PATH" "$APP_DIR/libgphoto2.so.6"
    echo "Linked: $APP_DIR/libgphoto2.so.6 -> $LIB_PATH"
fi

echo "Running camera probe (camera must be connected and powered on):"
if gphoto2 --auto-detect; then
    echo "gphoto2 auto-detect completed."
else
    echo "WARNING: gphoto2 probe failed. Check cable, camera mode, and USB permissions." >&2
fi

echo "Done."
echo "If you just changed group membership for $RUN_USER, re-login (or reboot) before running the app."
