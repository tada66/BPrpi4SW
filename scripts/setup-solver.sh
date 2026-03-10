#!/usr/bin/env bash
# setup-solver.sh
# One-time setup for plate solving on Raspberry Pi 4/5.
# Run as root: sudo bash setup-solver.sh
#
# Installs astrometry.net and downloads index files for the configured
# focal length.  After setup, plate solving works fully offline.
set -euo pipefail


INDEX_MIRROR="https://data.astrometry.net/4100"
INDEX_5200_LITE_URL="https://portal.nersc.gov/project/cosmo/temp/dstn/index-5200/LITE/"
INDEX_DIR="/usr/share/astrometry"

if [[ $EUID -ne 0 ]]; then
    echo "ERROR: run as root (sudo bash setup-solver.sh)" >&2
    exit 1
fi

# Install packages
echo "Installing astrometry.net, dcraw, and imagemagick..."
apt-get update -qq
apt-get install -y astrometry.net dcraw bc imagemagick

# Download all the 4100 series index files
INDICES=$(seq 4107 4119)
echo "Will download index files: 4107–4119"

mkdir -p "$INDEX_DIR"
for idx in $INDICES; do
    FILE="index-${idx}.fits"
    DEST="${INDEX_DIR}/${FILE}"
    if [[ -f "$DEST" ]]; then
        echo "  $FILE — already present, skipping"
    else
        echo "  Downloading $FILE..."
        wget -q --show-progress -O "$DEST" "${INDEX_MIRROR}/${FILE}" || {
            echo "  WARNING: failed to download $FILE — solve-field may still work without it"
            rm -f "$DEST"
        }
    fi
done

# Download all available 5200-series LITE index files from NERSC listing
echo ""
echo "Fetching 5200-series LITE index list..."

LITE_FILES=$(wget -qO- "$INDEX_5200_LITE_URL" \
    | grep -oE 'index-52[^"<>[:space:]]+\.fits' \
    | sort -u)

if [[ -z "$LITE_FILES" ]]; then
    echo "  WARNING: could not parse any 5200-series LITE index files from $INDEX_5200_LITE_URL"
else
    LITE_COUNT=$(echo "$LITE_FILES" | wc -l)
    echo "Will download $LITE_COUNT files from 5200-series LITE set"

    for FILE in $LITE_FILES; do
        DEST="${INDEX_DIR}/${FILE}"
        if [[ -f "$DEST" ]]; then
            echo "  $FILE — already present, skipping"
        else
            echo "  Downloading $FILE..."
            wget -q --show-progress -O "$DEST" "${INDEX_5200_LITE_URL}${FILE}" || {
                echo "  WARNING: failed to download $FILE"
                rm -f "$DEST"
            }
        fi
    done
fi

# Verify
echo ""
echo "Verifying installation..."
if command -v solve-field &>/dev/null; then
    echo "  solve-field: OK ($(solve-field --version 2>&1 | head -1))"
else
    echo "  ERROR: solve-field not found in PATH" >&2
    exit 1
fi

if command -v dcraw &>/dev/null; then
    echo "  dcraw: OK"
else
    echo "  WARNING: dcraw not found — RAW conversion will fail"
fi

if command -v convert &>/dev/null; then
    echo "  imagemagick convert: OK ($(convert --version 2>&1 | head -1))"
else
    echo "  WARNING: imagemagick convert not found — center crop will be skipped"
fi

NUM_INDEX=$(ls "$INDEX_DIR"/index-41*.fits 2>/dev/null | wc -l)
echo "  Index files: $NUM_INDEX installed in $INDEX_DIR"

NUM_INDEX_52=$(ls "$INDEX_DIR"/index-52*.fits 2>/dev/null | wc -l)
echo "  5200-series LITE index files: $NUM_INDEX_52 installed in $INDEX_DIR"