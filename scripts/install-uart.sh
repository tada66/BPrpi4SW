#!/bin/sh
# install-uart.sh
# Enable UART access for the StarTracker app on Raspberry Pi.
#
# Usage:
#   sudo bash install-uart.sh [run_user]
#
# This script:
# - enables hardware UART in the Pi boot config
# - disables the serial console/login shell on the UART
# - disables getty on serial ports that commonly grab the line
# - adds the run user to the dialout group
# - verifies the expected serial device nodes

set -eu

RUN_USER="${1:-${SUDO_USER:-pi}}"
BOOT_DIR=""
CONFIG_FILE=""
CMDLINE_FILE=""

if [ "$(id -u)" -ne 0 ]; then
    echo "ERROR: run as root (sudo sh install-uart.sh)" >&2
    exit 1
fi

if [ -d /boot/firmware ]; then
    BOOT_DIR="/boot/firmware"
elif [ -d /boot ]; then
    BOOT_DIR="/boot"
else
    echo "ERROR: could not locate /boot or /boot/firmware" >&2
    exit 1
fi

CONFIG_FILE="$BOOT_DIR/config.txt"
CMDLINE_FILE="$BOOT_DIR/cmdline.txt"

if [ ! -f "$CONFIG_FILE" ]; then
    echo "ERROR: config.txt not found at $CONFIG_FILE" >&2
    exit 1
fi

if [ ! -f "$CMDLINE_FILE" ]; then
    echo "ERROR: cmdline.txt not found at $CMDLINE_FILE" >&2
    exit 1
fi

echo "Using boot files:"
echo "  $CONFIG_FILE"
echo "  $CMDLINE_FILE"

echo "Adding run user '$RUN_USER' to dialout..."
if id "$RUN_USER" >/dev/null 2>&1; then
    usermod -aG dialout "$RUN_USER"
else
    echo "WARNING: user '$RUN_USER' does not exist yet; skipping group change" >&2
fi

backup_file() {
    file_path="$1"
    if [ -f "$file_path.bak" ]; then
        return
    fi
    cp "$file_path" "$file_path.bak"
}

backup_file "$CONFIG_FILE"
backup_file "$CMDLINE_FILE"

after_line() {
    needle="$1"
    line="$2"
    if grep -Fxq "$line" "$CONFIG_FILE"; then
        return
    fi
    if grep -Fqx "$needle" "$CONFIG_FILE"; then
        awk -v needle="$needle" -v line="$line" '
            { print }
            $0 == needle { print line }
        ' "$CONFIG_FILE" > "$CONFIG_FILE.tmp"
        mv "$CONFIG_FILE.tmp" "$CONFIG_FILE"
    else
        printf '%s\n' "$line" >> "$CONFIG_FILE"
    fi
}

set_or_add_config() {
    key="$1"
    value="$2"
    if grep -Eq "^[[:space:]]*#?[[:space:]]*${key}=" "$CONFIG_FILE"; then
        sed -i -E "s|^[[:space:]]*#?[[:space:]]*${key}=.*|${key}=${value}|" "$CONFIG_FILE"
    else
        printf '%s=%s\n' "$key" "$value" >> "$CONFIG_FILE"
    fi
}

set_or_add_config "enable_uart" "1"
after_line "enable_uart=1" "dtoverlay=disable-bt"

# Remove serial console from kernel cmdline so the UART is free for the app.
if grep -Eq '(^|[[:space:]])console=serial0,[^[:space:]]*' "$CMDLINE_FILE" || \
   grep -Eq '(^|[[:space:]])console=ttyAMA0,[^[:space:]]*' "$CMDLINE_FILE" || \
   grep -Eq '(^|[[:space:]])console=ttyS0,[^[:space:]]*' "$CMDLINE_FILE"; then
    sed -i -E 's/(^|[[:space:]])console=serial0,[^[:space:]]*//g; s/(^|[[:space:]])console=ttyAMA0,[^[:space:]]*//g; s/(^|[[:space:]])console=ttyS0,[^[:space:]]*//g; s/[[:space:]]+/ /g; s/^ //; s/ $//' "$CMDLINE_FILE"
fi

# Disable login shell on UART getty services if present.
systemctl disable --now serial-getty@serial0.service 2>/dev/null || true
systemctl disable --now serial-getty@ttyAMA0.service 2>/dev/null || true
systemctl disable --now serial-getty@ttyS0.service 2>/dev/null || true

# Rebuild the serial alias if the system uses udev-managed symlinks.
udevadm control --reload-rules || true
udevadm trigger || true

echo ""
echo "UART configuration updated. A reboot is required for boot config changes to take effect."
echo "After reboot, check:"
echo "  ls -l /dev/serial0 /dev/ttyAMA0 /dev/ttyS0"
echo "  groups $RUN_USER"
echo "  sudo systemctl status serial-getty@serial0.service serial-getty@ttyAMA0.service serial-getty@ttyS0.service"
