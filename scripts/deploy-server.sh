#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"

echo "Deploying EDDA server to basement..."

# Create directory on server if it doesn't exist
ssh $SERVER_HOST "mkdir -p $SERVER_DIR && mkdir -p $SERVER_DIR/models"

# Sync .env file (contains API keys - gitignored locally)
if [ -f "$PROJECT_ROOT/.env" ]; then
    echo "Syncing .env..."
    rsync -avz "$PROJECT_ROOT/.env" $SERVER_HOST:$SERVER_DIR/.env
    # Fix permissions and SELinux context so systemd can read it
    ssh $SERVER_HOST "chmod 644 $SERVER_DIR/.env && chcon -t etc_t $SERVER_DIR/.env 2>/dev/null"
else
    echo "WARNING: No .env file found at $PROJECT_ROOT/.env"
    echo "         OPENROUTER_API_KEY will not be available!"
fi

# Sync the C# project (exclude .env so we don't nuke the one we just synced)
echo "Syncing C# server..."
rsync -avz --delete \
  --exclude 'bin/' \
  --exclude 'obj/' \
  --exclude '.git/' \
  --exclude 'models/' \
  --exclude 'voices/' \
  --exclude 'docker/' \
  --exclude 'tts-service/' \
  --exclude 'piper-service/' \
  --exclude '.env' \
  server/src/ $SERVER_HOST:$SERVER_DIR/

echo "Building on server..."
ssh $SERVER_HOST "cd $SERVER_DIR && dotnet build EDDA.sln --configuration Release"

echo "Fixing SELinux context..."
ssh $SERVER_HOST "chcon -t bin_t $SERVER_DIR/EDDA.Server/bin/Release/net8.0/EDDA.Server"

# Sync and reload systemd service file
echo "Syncing systemd service..."
rsync -avz "$SCRIPT_DIR/edda-server.service" $SERVER_HOST:/tmp/edda-server.service
ssh $SERVER_HOST "sudo cp /tmp/edda-server.service /etc/systemd/system/ && sudo systemctl daemon-reload" || echo "[WARN] Could not update systemd service"

echo "Deployment complete!"
echo ""
echo "To start the server:"
echo "   sudo systemctl start edda-server"
echo "   journalctl -u edda-server -f"