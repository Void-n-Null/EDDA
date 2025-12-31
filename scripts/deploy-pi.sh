#!/bin/bash
set -e

PI_HOST="edda"  # (changed)
PI_DIR="/home/blake/edda-voice-client"

echo "Deploying EDDA client to Raspberry Pi..."

# Sync files
rsync -avz --delete \
  --exclude 'venv/' \
  --exclude '__pycache__/' \
  --exclude '*.pyc' \
  --exclude '*.pyo' \
  --exclude '*.egg-info/' \
  --exclude '.pytest_cache/' \
  --exclude '*.log' \
  --exclude '.git/' \
  pi-client/ $PI_HOST:$PI_DIR/

echo "Installing dependencies on Pi..."
ssh $PI_HOST "cd $PI_DIR && source venv/bin/activate && pip install -r requirements.txt"

echo "Installing systemd service..."
scp scripts/edda-client.service $PI_HOST:/tmp/
ssh $PI_HOST "sudo mv /tmp/edda-client.service /etc/systemd/system/ && sudo systemctl daemon-reload"

echo "Restarting service..."
ssh $PI_HOST "sudo systemctl restart edda-client"

echo ""
echo "Deployment complete!"
echo ""
echo "View logs:"
echo "  Systemd: ssh $PI_HOST journalctl -u edda-client -f"
echo "  File:    ssh $PI_HOST tail -f $PI_DIR/logs/edda-client.log"