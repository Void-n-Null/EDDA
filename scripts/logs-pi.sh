#!/bin/bash
PI_HOST="edda"
PI_DIR="/home/blake/edda-voice-client"

echo "Tailing Pi logs (systemd journal + file log)..."
echo "Ctrl+C to exit"
echo ""

# Show both systemd and file logs
ssh $PI_HOST "journalctl -u edda-client -f --no-hostname -o cat --since '1 minute ago'"