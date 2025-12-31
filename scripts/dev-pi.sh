#!/bin/bash
set -e

PI_HOST="edda"
PI_DIR="/home/blake/edda-voice-client"

echo "[PI] Deploying EDDA client..."

# Create directory
echo "--- Creating directory..."
ssh $PI_HOST "mkdir -p $PI_DIR"

# Recreate venv if --fresh flag is passed, otherwise create if missing
if [ "$1" = "--fresh" ]; then
    echo "--- Recreating Python environment (fresh install)..."
    ssh $PI_HOST "cd $PI_DIR && rm -rf venv && python3 -m venv venv"
else
    echo "--- Creating Python environment if needed..."
    ssh $PI_HOST "cd $PI_DIR && if [ ! -d venv ]; then python3 -m venv venv; fi"
fi

# Sync files FIRST (so requirements.txt is up to date)
echo "--- Syncing files..."
rsync -avz --delete \
  --exclude 'venv/' \
  --exclude '__pycache__/' \
  --exclude '*.pyc' \
  --exclude '*.pyo' \
  --exclude '*.egg-info/' \
  --exclude '.pytest_cache/' \
  --exclude '*.log' \
  pi-client/ $PI_HOST:$PI_DIR/

# THEN install requirements
echo "--- Installing Python dependencies..."
ssh $PI_HOST "cd $PI_DIR && ./venv/bin/pip install --upgrade -r requirements.txt"

# Install systemd service (always, in case it changed)
echo "--- Installing systemd service..."
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
scp "$SCRIPT_DIR/edda-client.service" $PI_HOST:/tmp/
ssh $PI_HOST "sudo mv /tmp/edda-client.service /etc/systemd/system/ && sudo systemctl daemon-reload"

# Restart via systemd
echo "--- Restarting edda-client service..."
ssh $PI_HOST "sudo systemctl restart edda-client"

# Tail logs
echo "------------------------------------------------"
echo "Streaming logs (Ctrl+C to stop)..."
echo "TIP: File logs at $PI_DIR/logs/edda-client.log"
echo ""
ssh $PI_HOST "journalctl -u edda-client -f --no-hostname -o cat"
