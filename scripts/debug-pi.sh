#!/bin/bash
# Show Pi client status and both log sources for debugging crashes

PI_HOST="edda"
PI_DIR="/home/blake/edda-voice-client"

echo "=========================================="
echo "EDDA Pi Client - Debug Info"
echo "=========================================="
echo ""

# Service status
echo "[Service Status]"
ssh $PI_HOST "systemctl status edda-client --no-pager" || true
echo ""

# Recent systemd logs
echo "[Recent Systemd Logs (last 50 lines)]"
ssh $PI_HOST "journalctl -u edda-client -n 50 --no-pager --no-hostname -o cat"
echo ""

# Recent file logs
echo "[Recent File Logs (last 50 lines)]"
ssh $PI_HOST "tail -50 $PI_DIR/logs/edda-client.log 2>/dev/null || echo 'No file log found yet'"
echo ""

echo "=========================================="
echo "Follow live logs? (Ctrl+C to exit)"
echo "=========================================="
read -p "Press Enter to continue..."

# Follow both
echo ""
echo "Following logs (systemd + file)..."
ssh $PI_HOST "tail -f $PI_DIR/logs/edda-client.log 2>&1 & journalctl -u edda-client -f --no-hostname -o cat"
