#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"

echo "Deploying EDDA server..."

# Sync .env file (contains API keys - gitignored locally)
if [ -f "$PROJECT_ROOT/.env" ]; then
    echo "Syncing .env..."
    rsync -avz "$PROJECT_ROOT/.env" $SERVER_HOST:$SERVER_DIR/.env 2>&1 | grep -v "WARNING: connection"
    # Fix permissions and SELinux context so systemd can read it
    ssh $SERVER_HOST "chmod 644 $SERVER_DIR/.env && chcon -t etc_t $SERVER_DIR/.env 2>/dev/null" 2>&1 | grep -v "WARNING: connection"
else
    echo "WARNING: No .env file found at $PROJECT_ROOT/.env"
    echo "         OPENROUTER_API_KEY will not be available!"
fi

# Sync files (exclude .env so we don't nuke the one we just synced)
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
  server/src/ $SERVER_HOST:$SERVER_DIR/ 2>&1 | grep -v "WARNING: connection"

# Build
echo "Building..."
ssh $SERVER_HOST "cd $SERVER_DIR && dotnet build EDDA.sln --configuration Release" 2>&1 | grep -v "WARNING: connection"

# Fix SELinux context (Fedora requires bin_t for systemd to execute)
echo "Fixing SELinux context..."
ssh $SERVER_HOST "chcon -t bin_t $SERVER_DIR/EDDA.Server/bin/Release/net8.0/EDDA.Server" 2>&1 | grep -v "WARNING: connection"

# Sync and reload systemd service file
echo "Syncing systemd service..."
rsync -avz "$SCRIPT_DIR/edda-server.service" $SERVER_HOST:/tmp/edda-server.service 2>&1 | grep -v "WARNING: connection"
ssh $SERVER_HOST "sudo cp /tmp/edda-server.service /etc/systemd/system/ && sudo systemctl daemon-reload" 2>&1 | grep -v "WARNING: connection" || echo "[WARN] Could not update systemd service"

# Restart via systemd (may fail if sudo needs password - that's OK, just restart manually)
echo "Restarting edda-server service..."
ssh $SERVER_HOST "sudo systemctl restart edda-server 2>/dev/null || echo '[WARN] Could not restart via systemd - run: ssh basement sudo systemctl restart edda-server'" 2>&1 | grep -v "WARNING: connection"

# Tail logs
echo "------------------------------------------------"
echo "Streaming logs (Ctrl+C to stop)..."
echo ""
ssh $SERVER_HOST "journalctl -u edda-server -f --no-hostname -o cat"
