import socket
import struct
import cv2
import numpy as np
import json
import threading
import tkinter as tk
from tkinter import ttk
from PIL import Image, ImageTk
import io

HOST = '0.0.0.0'
PORT = 5000

class LiveViewApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Live View Controller")
        self.root.geometry("1280x768")
        
        self.conn = None
        self.lock = threading.Lock()
        self.running = True

        self.setup_gui()
        
        # Start server thread
        self.server_thread = threading.Thread(target=self.server_loop, daemon=True)
        self.server_thread.start()

    def setup_gui(self):
        # Main layout
        self.main_container = ttk.Frame(self.root)
        self.main_container.pack(fill=tk.BOTH, expand=True)

        # Control Panel (Left)
        self.controls = ttk.Frame(self.main_container, width=200, padding="10")
        self.controls.pack(side=tk.LEFT, fill=tk.Y)
        
        # Video Panel (Right)
        self.video_panel = ttk.Frame(self.main_container)
        self.video_panel.pack(side=tk.RIGHT, fill=tk.BOTH, expand=True)
        
        self.video_label = ttk.Label(self.video_panel, text="Waiting for connection...")
        self.video_label.pack(fill=tk.BOTH, expand=True)

        # Controls
        self.create_control_group("ISO", "set_iso", ["100", "200", "400", "800", "1600", "3200", "6400"])
        self.create_control_group("Shutter", "set_shutter", ["1/30", "1/60", "1/125", "1/250", "1/500"])
        self.create_control_group("Aperture", "set_aperture", ["1.8", "2.8", "4.0", "5.6", "8.0"])

        # Magnify
        ttk.Separator(self.controls, orient='horizontal').pack(fill='x', pady=10)
        self.btn_magnify = ttk.Button(self.controls, text="Magnify On", command=self.toggle_magnify)
        self.btn_magnify.pack(fill='x', pady=5)
        self.magnify_state = False

        # Focus
        ttk.Separator(self.controls, orient='horizontal').pack(fill='x', pady=10)
        focus_frame = ttk.LabelFrame(self.controls, text="Manual Focus", padding=5)
        focus_frame.pack(fill='x', pady=5)
        
        self.focus_step = tk.StringVar(value="2")
        ttk.Label(focus_frame, text="Step (1-7):").pack(anchor='w')
        ttk.Entry(focus_frame, textvariable=self.focus_step).pack(fill='x', pady=2)

        btn_near = ttk.Button(focus_frame, text="<<< Near", command=lambda: self.send_command("focus_closer", self.focus_step.get()))
        btn_near.pack(fill='x', pady=2)
        
        btn_far = ttk.Button(focus_frame, text="Far >>>", command=lambda: self.send_command("focus_further", self.focus_step.get()))
        btn_far.pack(fill='x', pady=2)

        # Status
        ttk.Separator(self.controls, orient='horizontal').pack(fill='x', pady=10)
        self.lbl_status = ttk.Label(self.controls, text="Status: Disconnected")
        self.lbl_status.pack(pady=5)
        
        self.lbl_current_iso = ttk.Label(self.controls, text="ISO: --")
        self.lbl_current_iso.pack(anchor='w')
        self.lbl_current_shutter = ttk.Label(self.controls, text="Shutter: --")
        self.lbl_current_shutter.pack(anchor='w')
        self.lbl_current_aperture = ttk.Label(self.controls, text="Aperture: --")
        self.lbl_current_aperture.pack(anchor='w')

    def create_control_group(self, label, action, values):
        frame = ttk.LabelFrame(self.controls, text=label, padding=5)
        frame.pack(fill='x', pady=5)
        
        entry = ttk.Combobox(frame, values=values)
        entry.pack(fill='x', pady=2)
        
        btn = ttk.Button(frame, text="Set", command=lambda: self.send_command(action, entry.get()))
        btn.pack(fill='x', pady=2)

    def toggle_magnify(self):
        if self.magnify_state:
            self.send_command("magnify_off", "")
            self.btn_magnify.config(text="Magnify On")
        else:
            self.send_command("magnify", "")
            self.btn_magnify.config(text="Magnify Off")
        self.magnify_state = not self.magnify_state

    def send_command(self, action, value):
        if not self.conn:
            print("Not connected")
            return
        
        cmd = {"Action": action, "Value": value}
        json_bytes = json.dumps(cmd).encode('utf-8')
        
        try:
            with self.lock:
                # Type 0x03 (Command)
                self.conn.sendall(b'\x03')
                # Length
                self.conn.sendall(struct.pack('>I', len(json_bytes)))
                # Payload
                self.conn.sendall(json_bytes)
            print(f"Sent: {cmd}")
        except Exception as e:
            print(f"Send error: {e}")

    def server_loop(self):
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.bind((HOST, PORT))
        server.listen(1)
        print(f"Listening on {HOST}:{PORT}...")

        while self.running:
            try:
                conn, addr = server.accept()
                self.conn = conn
                self.root.after(0, lambda: self.lbl_status.config(text=f"Connected: {addr[0]}"))
                print(f"Connected by {addr}")
                
                while self.running:
                    # Read Type
                    type_byte = self.recv_exact(conn, 1)
                    if not type_byte: break
                    msg_type = type_byte[0]

                    # Read Length
                    len_bytes = self.recv_exact(conn, 4)
                    if not len_bytes: break
                    length = struct.unpack('>I', len_bytes)[0]

                    # Read Payload
                    payload = self.recv_exact(conn, length)
                    if not payload: break

                    if msg_type == 0x01: # Metadata
                        self.root.after(0, self.update_metadata, payload)
                    elif msg_type == 0x02: # Image
                        self.root.after(0, self.update_image, payload)
            
            except Exception as e:
                print(f"Connection error: {e}")
            finally:
                if self.conn: self.conn.close()
                self.conn = None
                self.root.after(0, lambda: self.lbl_status.config(text="Status: Disconnected"))

    def recv_exact(self, conn, n):
        data = b''
        while len(data) < n:
            chunk = conn.recv(n - len(data))
            if not chunk: return None
            data += chunk
        return data

    def update_metadata(self, payload):
        try:
            meta = json.loads(payload.decode('utf-8'))
            self.lbl_current_iso.config(text=f"ISO: {meta.get('iso', '--')}")
            self.lbl_current_shutter.config(text=f"Shutter: {meta.get('shutter', '--')}")
            self.lbl_current_aperture.config(text=f"Aperture: {meta.get('aperture', '--')}")
        except Exception as e:
            print(f"Meta parse error: {e}")

    def update_image(self, payload):
        try:
            # Decode JPEG
            nparr = np.frombuffer(payload, np.uint8)
            img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
            if img is None: return

            # Convert to RGB (OpenCV is BGR)
            img = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
            
            # Resize to fit window (optional, simple scaling)
            h, w, _ = img.shape
            # display_w = self.video_panel.winfo_width()
            # display_h = self.video_panel.winfo_height()
            # if display_w > 10 and display_h > 10:
            #     scale = min(display_w/w, display_h/h)
            #     new_w, new_h = int(w*scale), int(h*scale)
            #     img = cv2.resize(img, (new_w, new_h))

            im_pil = Image.fromarray(img)
            imgtk = ImageTk.PhotoImage(image=im_pil)
            
            self.video_label.imgtk = imgtk # Keep reference
            self.video_label.configure(image=imgtk, text="")
        except Exception as e:
            print(f"Image update error: {e}")

def main():
    root = tk.Tk()
    app = LiveViewApp(root)
    root.mainloop()

if __name__ == '__main__':
    main()
