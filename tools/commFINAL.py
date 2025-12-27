#!/usr/bin/env python3
import serial # type: ignore
import time
import os
import glob
import threading
import sys
import binascii
import struct
import cobs.cobs as cobs  # type: ignore # pip install cobs
import random  # For message ID generation
import argparse

# Constants for commands - ensure these match the Pico
CMD_ACK = 0x01              # Fixed from 0x015
CMD_MOVE_STATIC = 0x10
CMD_MOVE_TRACKING = 0x11
CMD_PAUSE = 0x12
CMD_RESUME = 0x13
CMD_STOP = 0x14
CMD_GETPOS = 0x20
CMD_POSITION = 0x21
CMD_STATUS = 0x22
CMD_ESTOPTRIG = 0x30

# Define axis constants for clarity
AXIS_X = 0
AXIS_Y = 1
AXIS_Z = 2

# Global verbose flag
VERBOSE = False

def vprint(*args, **kwargs):
    """Print only in verbose mode"""
    if VERBOSE:
        print(*args, **kwargs)

def eprint(*args, **kwargs):
    """Print errors (always visible)"""
    print("ERROR:", *args, **kwargs)

def iprint(*args, **kwargs):
    """Print important info (always visible)"""
    print(*args, **kwargs)

def find_serial_port():
    """Try to find the most likely serial port for UART communication"""
    potential_ports = ['/dev/ttyS0', '/dev/serial0', '/dev/ttyAMA0', '/dev/ttyUSB0']
    existing_ports = [port for port in potential_ports if os.path.exists(port)]

    if existing_ports:
        return existing_ports[0]

    usb_ports = glob.glob('/dev/ttyUSB*')
    if usb_ports:
        return usb_ports[0]

    if VERBOSE:
        print("Available tty devices:", glob.glob('/dev/tty*'))
    return None

def calculate_crc8(data):
    """Calculate CRC8 checksum for data"""
    crc = 0xFF  # Initial value
    polynomial = 0x07  # x^8 + x^2 + x + 1 (standard CRC8)

    for byte in data:
        crc ^= byte
        for _ in range(8):
            if crc & 0x80:
                crc = (crc << 1) ^ polynomial
            else:
                crc <<= 1
            crc &= 0xFF

    return crc

# Global variables for ID tracking
last_sent_id = 0x00  # Initialize to invalid ID
pending_acks = {}    # Dictionary to track unacknowledged messages

def generate_msg_id():
    """Generate a new message ID that's different from the last one and not 0x00"""
    global last_sent_id
    new_id = last_sent_id
    while new_id == last_sent_id or new_id == 0x00:
        new_id = random.randint(1, 255)
    last_sent_id = new_id
    return new_id

def send_command(ser, cmd_type, data=None, timeout=2.0):
    """Send a command to the Pico with message ID and wait for ACK"""
    if data is None:
        data = []

    # Generate a new unique message ID
    msg_id = generate_msg_id()

    raw_command = bytearray([cmd_type, msg_id, len(data)])
    raw_command.extend(data)

    # Calculate CRC over the entire message
    crc = calculate_crc8(raw_command)
    raw_command.append(crc)

    # COBS encode the packet and add delimiter
    try:
        encoded = cobs.encode(raw_command)
        packet = encoded + b'\x00'  # Add zero delimiter
    except Exception as e:
        eprint(f"Error encoding COBS: {e}")
        return False

    # Track this message for ACK verification
    pending_acks[msg_id] = {
        'cmd': cmd_type,
        'timestamp': time.time(),
        'acked': False
    }

    # Send the packet
    ser.write(packet)
    
    vprint(f"\n--------Sent command: {binascii.hexlify(packet).decode('ascii')}--------")
    vprint(f"  CMD  : 0x{cmd_type:02X}")
    vprint(f"  ID   : {msg_id} (0x{msg_id:02X})")
    vprint(f"  LEN  : {len(data)}")
    if data:
        vprint(f"  DATA : {binascii.hexlify(bytearray(data)).decode('ascii')}")
    vprint(f"  CRC8 : 0x{crc:02X}")

    # For ACK commands, don't wait for an ACK to avoid infinite loops
    if cmd_type == CMD_ACK:  # Fixed constant
        return True

    # Wait for ACK with timeout
    start_time = time.time()
    while time.time() - start_time < timeout:
        if msg_id in pending_acks and pending_acks[msg_id]['acked']:
            vprint(f"Command ACKed within {time.time() - start_time:.3f} seconds")
            return True
        time.sleep(0.01)

    eprint(f"No ACK received for ID={msg_id}, CMD=0x{cmd_type:02X}")
    return False

def receiver_thread(ser):
    """Thread function to continuously read from serial port using read_until for reliability"""
    while not stop_thread:
        try:
            # Read until COBS delimiter (0x00) - this ensures complete frames
            frame = ser.read_until(b'\x00')
            
            if not frame or len(frame) <= 1:  # Empty or just delimiter
                continue
                
            # Remove the delimiter (last byte should be 0x00)
            if frame.endswith(b'\x00'):
                frame = frame[:-1]
            
            if len(frame) == 0:
                continue
                
            hex_str = binascii.hexlify(frame).decode('ascii')
            vprint(f"\n--------NEW DATA: {hex_str}--------")
            
            # Reasonable frame size check
            if len(frame) > 50:
                eprint(f"Oversized frame ({len(frame)} bytes) - likely corrupted")
                continue
                
            try:
                vprint(f"COBS frame received ({len(frame)} bytes): {hex_str}")
                decoded = cobs.decode(frame)
                vprint(f"Decoded ({len(decoded)} bytes): {binascii.hexlify(decoded).decode('ascii')}")
                process_binary_message(decoded, ser)
            except Exception as e:
                vprint(f"COBS decoding error: {e}")
                continue
                
        except KeyboardInterrupt:
            break
        except Exception as e:
            eprint(f"Receiver exception: {e}")
            time.sleep(0.05)

def process_binary_message(decoded, ser):
    """Process a decoded binary message"""
    if len(decoded) < 4:  # Need at least CMD+ID+LEN+CRC
        vprint(f"Decoded message too short: {len(decoded)} bytes")
        return
    
    # Extract message components
    cmd_type = decoded[0]
    msg_id = decoded[1]
    data_length = decoded[2]
    
    # Verify message length
    if len(decoded) != data_length + 4:  # CMD+ID+LEN+DATA+CRC
        vprint(f"Invalid message length: expected {data_length + 4}, got {len(decoded)}")
        return
    
    # Verify CRC
    received_crc = decoded[-1]
    calculated_crc = calculate_crc8(decoded[:-1])
    crc_valid = received_crc == calculated_crc
    
    # Check for invalid message ID
    if msg_id == 0x00:
        vprint("Received message with invalid ID 0x00, ignoring")
        return
    
    # Display message details (verbose only for most fields)
    if VERBOSE:
        print("\nReceived binary message:")
        print(f"  CMD  : 0x{cmd_type:02X}")
        print(f"  ID   : {msg_id} (0x{msg_id:02X})")
        print(f"  LEN  : {data_length}")
    
    # Process data if present
    if data_length > 0:
        data = decoded[3:-1]
        data_hex = binascii.hexlify(data).decode('ascii')
        vprint(f"  DATA : {data_hex}")
        
        # Process specific message types
        if cmd_type == CMD_ACK:  # Fixed constant
            if data_length >= 1:
                acked_id = data[0]
                vprint(f"  Received ACK for message ID: {acked_id} (0x{acked_id:02X})")
                if acked_id in pending_acks:
                    pending_acks[acked_id]['acked'] = True
                    vprint(f"  Message with ID={acked_id} marked as acknowledged")
                else:
                    vprint(f"  Received ACK for unknown message ID: {acked_id}")
        elif cmd_type == CMD_STATUS:
            # Telemetry: float temp + 3x int32 (X,Y,Z) + enabled(u8) + paused(u8) + fan_pct(u8)
            if data_length >= 19:
                try:
                    temp = struct.unpack('<f', data[0:4])[0]
                    x = struct.unpack('<i', data[4:8])[0]
                    y = struct.unpack('<i', data[8:12])[0]
                    z = struct.unpack('<i', data[12:16])[0]
                    enabled = bool(data[16])
                    paused  = bool(data[17])
                    fan_pct = int(data[18])
                    state_str = f"{'ENABLED' if enabled else 'DISABLED'}, {'PAUSED' if paused else 'RUNNING'}"
                    iprint(f"Status: Temp={temp:.2f}°C, Positions: X={x}, Y={y}, Z={z} arcseconds, Motors: {state_str}, Fan={fan_pct}%")
                except Exception as e:
                    eprint(f"Error parsing telemetry: {e}")
        elif cmd_type == CMD_POSITION:
            # New GETPOS layout: 3x int32 (X,Y,Z) arcseconds
            if data_length >= 12:
                try:
                    x = struct.unpack('<i', data[0:4])[0]
                    y = struct.unpack('<i', data[4:8])[0]
                    z = struct.unpack('<i', data[8:12])[0]
                    iprint(f"Positions: X={x}, Y={y}, Z={z} arcseconds")
                except Exception as e:
                    eprint(f"Error parsing positions: {e}")
    
    if VERBOSE:
        print(f"  CRC8 : 0x{received_crc:02X} ({'Valid' if crc_valid else 'INVALID'})")
    
    # Report CRC errors even in non-verbose mode
    if not crc_valid:
        eprint(f"CRC error in message ID {msg_id}")
    
    # Send ACK for valid non-ACK messages
    if cmd_type != CMD_ACK and crc_valid:  # Fixed constant
        vprint(f"  Sending ACK for message ID {msg_id}")
        send_command(ser, CMD_ACK, [msg_id])  # Fixed constant

def send_move_static_command(ser, axis, position_arcsec):
    """Send a static movement command"""
    data = bytearray([axis])
    position_bytes = struct.pack('<i', position_arcsec)
    data.extend(position_bytes)
    
    axis_name = "X" if axis == 0 else "Y" if axis == 1 else "Z" if axis == 2 else "Unknown"
    iprint(f"Moving {axis_name} axis to {position_arcsec} arcseconds...")
    return send_command(ser, CMD_MOVE_STATIC, data)

def send_tracking_command(ser, x_rate, y_rate, z_rate):
    """Send tracking command"""
    data = bytearray()
    data.extend(struct.pack('<f', x_rate))
    data.extend(struct.pack('<f', y_rate))
    data.extend(struct.pack('<f', z_rate))
    
    iprint(f"Starting tracking: X={x_rate}, Y={y_rate}, Z={z_rate} arcsec/sec")
    return send_command(ser, CMD_MOVE_TRACKING, data)

def print_help():
    """Print available commands"""
    print("\nAvailable commands:")
    print("1 - Send ping")
    print("2 - Pause motors")
    print("3 - Resume motors")
    print("4 - Get status")
    print("5 - Stop all movement")
    print("x - Move X axis (absolute)")
    print("y - Move Y axis (absolute)")
    print("z - Move Z axis (absolute)")
    print("p - Get current positions (all axes)")  # updated
    print("t - Start tracking mode")
    print("e - Emergency stop")
    print("h - Show this help")
    print("q - Quit")

# Parse command line arguments
def parse_args():
    global VERBOSE
    parser = argparse.ArgumentParser(description='UART Communication Test for BPpicoFW')
    parser.add_argument('-v', '--verbose', action='store_true', 
                        help='Enable verbose output (show all protocol details)')
    parser.add_argument('-p', '--port', type=str, 
                        help='Specify serial port (default: auto-detect)')
    
    args = parser.parse_args()
    VERBOSE = args.verbose
    return args

# Main program
def main():
    global stop_thread
    
    args = parse_args()
    random.seed()

    port = args.port if args.port else find_serial_port()
    if not port:
        eprint("No suitable serial port found!")
        if not VERBOSE:
            print("Use -v flag for more details or -p to specify port manually")
        exit(1)

    iprint(f"Using serial port: {port}")
    if VERBOSE:
        iprint("Verbose mode enabled - showing all protocol details")

    try:
        vprint("Setting up serial connection...")
        ser = serial.Serial(
            port=port,
            baudrate=9600,  # Matches UART.h
            parity=serial.PARITY_NONE,
            stopbits=serial.STOPBITS_ONE,
            bytesize=serial.EIGHTBITS,
            timeout=1.0  # Longer timeout for read_until
        )
        
        ser.reset_input_buffer()
        ser.reset_output_buffer()
        
        vprint("Sending reset bytes...")
        ser.write(b'\x00\x00\x00')
        time.sleep(0.1)
        
        if ser.in_waiting > 0:
            junk = ser.read(ser.in_waiting)
            vprint(f"Cleared {len(junk)} bytes of pending data")
            
        iprint("UART connection established.")
        print_help()

        # Start receiver thread
        stop_thread = False
        thread = threading.Thread(target=receiver_thread, args=(ser,))
        thread.daemon = True
        thread.start()

        # Main command loop
        while True:
            try:
                cmd = input("> ").strip().lower()
            except KeyboardInterrupt:
                print("\nExiting...")
                break

            if cmd == "q" or cmd == "quit":
                break
            elif cmd == "h" or cmd == "help":
                print_help()
            elif cmd == "1":
                iprint("Sending ping...")
                send_command(ser, 0x01)  # CMD_PING (same as CMD_ACK)
            elif cmd == "2":
                iprint("Pausing motors...")
                send_command(ser, CMD_PAUSE)
            elif cmd == "3":
                iprint("Resuming motors...")
                send_command(ser, CMD_RESUME)
            elif cmd == "4":
                iprint("Requesting status...")
                send_command(ser, CMD_STATUS)
            elif cmd == "5":
                iprint("Stopping all movement...")
                send_command(ser, CMD_STOP)
            elif cmd == "e":
                iprint("Triggering emergency stop...")
                send_command(ser, CMD_ESTOPTRIG)
            elif cmd == "x":
                try:
                    position = int(input("Enter X position (arcseconds): "))
                    send_move_static_command(ser, AXIS_X, position)
                except ValueError:
                    eprint("Invalid position. Please enter a number.")
                except KeyboardInterrupt:
                    print("\nCancelled")
            elif cmd == "y":
                try:
                    position = int(input("Enter Y position (arcseconds): "))
                    send_move_static_command(ser, AXIS_Y, position)
                except ValueError:
                    eprint("Invalid position. Please enter a number.")
                except KeyboardInterrupt:
                    print("\nCancelled")
            elif cmd == "z":
                try:
                    position = int(input("Enter Z position (arcseconds): "))
                    send_move_static_command(ser, AXIS_Z, position)
                except ValueError:
                    eprint("Invalid position. Please enter a number.")
                except KeyboardInterrupt:
                    print("\nCancelled")
            elif cmd == "p":
                send_command(ser, CMD_GETPOS)
            elif cmd == "t":
                try:
                    x_rate = float(input("Enter X rate (arcsec/sec): "))
                    y_rate = float(input("Enter Y rate (arcsec/sec): "))
                    z_rate = float(input("Enter Z rate (arcsec/sec): "))
                    send_tracking_command(ser, x_rate, y_rate, z_rate)
                except ValueError:
                    eprint("Invalid rate. Please enter a number.")
                except KeyboardInterrupt:
                    print("\nCancelled")
            elif cmd == "":
                continue  # Empty input, just continue
            else:
                print(f"Unknown command: '{cmd}'. Type 'h' for help.")

    except serial.SerialException as e:
        eprint(f"Serial error: {e}")
    except KeyboardInterrupt:
        print("\nProgram terminated by user")
    finally:
        stop_thread = True
        if 'thread' in locals() and thread.is_alive():
            thread.join(1.0)
        if 'ser' in locals() and ser.is_open:
            ser.close()
            vprint("Serial connection closed")

if __name__ == "__main__":
    main()
