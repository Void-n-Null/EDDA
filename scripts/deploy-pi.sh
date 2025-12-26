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
  --exclude '.git/' \
  pi-client/ $PI_HOST:$PI_DIR/

echo "Installing dependencies on Pi..."
ssh $PI_HOST "cd $PI_DIR && source venv/bin/activate && pip install -r requirements.txt"

echo "Restarting service..."
ssh $PI_HOST "sudo systemctl restart edda-client"

echo "Deployment complete!"
echo "View logs with: ssh $PI_HOST journalctl -u edda-client -f"