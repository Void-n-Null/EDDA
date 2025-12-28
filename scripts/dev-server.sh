#!/bin/bash
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"

echo "Deploying EDDA server..."

# Sync files
rsync -avz --delete \
  --exclude 'bin/' \
  --exclude 'obj/' \
  --exclude '.git/' \
  --exclude 'models/' \
  --exclude 'voices/' \
  --exclude 'docker/' \
  --exclude 'tts-service/' \
  --exclude 'piper-service/' \
  server/src/ $SERVER_HOST:$SERVER_DIR/ 2>&1 | grep -v "WARNING: connection"

# Build
echo "Building..."
ssh $SERVER_HOST "cd $SERVER_DIR && dotnet build EDDA.sln --configuration Release" 2>&1 | grep -v "WARNING: connection"

# Fix SELinux context (Fedora requires bin_t for systemd to execute)
echo "Fixing SELinux context..."
ssh $SERVER_HOST "chcon -t bin_t $SERVER_DIR/EDDA.Server/bin/Release/net8.0/EDDA.Server" 2>&1 | grep -v "WARNING: connection"

# Restart via systemd (may fail if sudo needs password - that's OK, just restart manually)
echo "Restarting edda-server service..."
ssh $SERVER_HOST "sudo systemctl restart edda-server 2>/dev/null || echo '[WARN] Could not restart via systemd - run: ssh basement sudo systemctl restart edda-server'" 2>&1 | grep -v "WARNING: connection"

# Tail logs
echo "------------------------------------------------"
echo "Streaming logs (Ctrl+C to stop)..."
echo ""
ssh $SERVER_HOST "journalctl -u edda-server -f --no-hostname -o cat"
