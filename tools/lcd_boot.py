#!/usr/bin/env python3
"""
StarTracker boot splash
=======================
Drives the 20×4 HD44780 LCD in 4-bit mode and shows boot progress messages,
then hands off to the main C# binary via os.execv() (so systemd tracks that
process instead).

Pin mapping (BCM numbering) — must match LcdController.cs:
  RS=26  EN=19  D4=25  D5=24  D6=22  D7=27

Requires:  python3-rpi.gpio  (Bullseye)
        or python3-rpi-lgpio (Bookworm — drop-in RPi.GPIO replacement)
"""

import os
import sys
import time

# ── Constants ────────────────────────────────────────────────────────────────

APP_DIR  = os.path.dirname(os.path.abspath(__file__))
APP_PATH = os.path.join(APP_DIR, "BPrpi4SW")

PIN_RS   = 26
PIN_EN   = 19
PINS_D   = [25, 24, 22, 27]   # D4 D5 D6 D7 (BCM)

E_PULSE  = 0.0005              # 500 µs
E_DELAY  = 0.0005

LCD_CMD  = False
LCD_CHR  = True

LINE_ADDR = [0x80, 0xC0, 0x94, 0xD4]   # rows 0-3 of a 2004

# ── GPIO helpers ─────────────────────────────────────────────────────────────

def _init_gpio():
    import RPi.GPIO as GPIO
    GPIO.setwarnings(False)
    GPIO.setmode(GPIO.BCM)
    for pin in [PIN_RS, PIN_EN] + PINS_D:
        GPIO.setup(pin, GPIO.OUT)
    return GPIO


def _toggle(GPIO):
    time.sleep(E_DELAY)
    GPIO.output(PIN_EN, True)
    time.sleep(E_PULSE)
    GPIO.output(PIN_EN, False)
    time.sleep(E_DELAY)


def _nibble(GPIO, val):
    """Send a 4-bit nibble on D4-D7 and pulse EN."""
    for i, pin in enumerate(PINS_D):
        GPIO.output(pin, bool((val >> i) & 1))
    _toggle(GPIO)


def _byte(GPIO, val, mode):
    GPIO.output(PIN_RS, mode)
    _nibble(GPIO, (val >> 4) & 0xF)   # high nibble first
    _nibble(GPIO, val & 0xF)           # low nibble


def _cmd(GPIO, val):
    _byte(GPIO, val, LCD_CMD)


def _char(GPIO, val):
    _byte(GPIO, val, LCD_CHR)

# ── LCD high-level ────────────────────────────────────────────────────────────

def lcd_init(GPIO):
    """Initialise the controller in 4-bit mode."""
    _cmd(GPIO, 0x33)     # init sequence
    _cmd(GPIO, 0x32)
    _cmd(GPIO, 0x28)     # 4-bit, 2 lines, 5×8 font
    _cmd(GPIO, 0x0C)     # display on, cursor off, no blink
    _cmd(GPIO, 0x06)     # entry mode: increment, no shift
    _cmd(GPIO, 0x01)     # clear display
    time.sleep(0.003)


def lcd_write(GPIO, row, text):
    """Write up to 20 characters to a row (0-3), padding with spaces."""
    text = (text[:20]).ljust(20)
    _cmd(GPIO, LINE_ADDR[row])
    for ch in text:
        _char(GPIO, ord(ch))

# ── Entry point ───────────────────────────────────────────────────────────────

def main():
    GPIO = None

    try:
        GPIO = _init_gpio()
        lcd_init(GPIO)

        # Static splash (rows 0-1 stay for the whole sequence)
        lcd_write(GPIO, 0, " -- Star Tracker --")
        lcd_write(GPIO, 1, "   Camera Control")
        lcd_write(GPIO, 2, "")

        steps = [
            (0.0,  "System init..."),
            (1.5,  "Loading runtime..."),
            (3.0,  "Starting server..."),
            (4.5,  "Launching app..."),
        ]

        deadline = time.monotonic()
        for delay, msg in steps:
            target = deadline + delay
            now = time.monotonic()
            if now < target:
                time.sleep(target - now)
            lcd_write(GPIO, 3, msg)

        # Brief pause so the last message is readable
        time.sleep(0.5)

    except Exception as ex:
        # GPIO/LCD failure is non-fatal — just launch the app anyway
        print(f"[lcd_boot] LCD init failed: {ex}", file=sys.stderr)

    finally:
        # Release GPIO before exec so the C# process can claim the pins
        try:
            if GPIO is not None:
                GPIO.cleanup()
        except Exception:
            pass

    # Replace this process with the C# binary.
    # systemd will track the C# binary from this point on.
    if not os.path.isfile(APP_PATH):
        print(f"[lcd_boot] ERROR: binary not found: {APP_PATH}", file=sys.stderr)
        sys.exit(1)

    os.execv(APP_PATH, [APP_PATH])


if __name__ == "__main__":
    main()
