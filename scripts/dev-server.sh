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
  server/src/ $SERVER_HOST:$SERVER_DIR/ 2>&1 | grep -v "WARNING: connection"

# Build
echo "Building..."
ssh $SERVER_HOST "cd $SERVER_DIR && dotnet build EDDA.sln --configuration Release" 2>&1 | grep -v "WARNING: connection"

# Kill old process
echo "Stopping old process..."
ssh $SERVER_HOST "pkill -f 'dotnet.*EDDA.Server' || true" 2>&1 | grep -v "WARNING: connection"

# Clear old log
ssh $SERVER_HOST "rm -f /tmp/edda.log" 2>&1 | grep -v "WARNING: connection"

# Start and immediately tail logs
echo "Starting server and streaming logs..."
echo "------------------------------------------------"
echo ""

ssh $SERVER_HOST "cd $SERVER_DIR && dotnet run --project EDDA.Server 2>&1"