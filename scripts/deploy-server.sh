#!/bin/bash
set -e

SERVER_HOST="basement"
SERVER_DIR="/home/blake/edda-server"

echo "Deploying EDDA server to basement..."

# Create directory on server if it doesn't exist
ssh $SERVER_HOST "mkdir -p $SERVER_DIR && mkdir -p $SERVER_DIR/models"

# Sync the C# project
echo "Syncing C# server..."
rsync -avz --delete \
  --exclude 'bin/' \
  --exclude 'obj/' \
  --exclude '.git/' \
  --exclude 'models/' \
  server/src/ $SERVER_HOST:$SERVER_DIR/

echo "Building on server..."
ssh $SERVER_HOST "cd $SERVER_DIR && dotnet build EDDA.sln --configuration Release"

echo "Deployment complete!"
echo ""
echo "To run on server:"
echo "   ssh basement"
echo "   cd $SERVER_DIR"
echo "   dotnet run --project EDDA.Server"