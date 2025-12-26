import time
import socket
from datetime import datetime
import sys

# Force unbuffered output so SSH shows it in real-time
sys.stdout.reconfigure(line_buffering=True)
sys.stderr.reconfigure(line_buffering=True)

def main():
    print("[PI] EDDA Client starting...", flush=True)
    print(f"Started at: {datetime.now()}", flush=True)
    print(f"Hostname: {socket.gethostname()}", flush=True)
    print(f"User: {socket.getfqdn()}", flush=True)
    print(flush=True)
    
    count = 0
    while True:
        count += 1
        print(f"[{datetime.now().strftime('%H:%M:%S')}] [PI] Running Python (tick #{count})", flush=True)
        time.sleep(2)

if __name__ == "__main__":
    main()