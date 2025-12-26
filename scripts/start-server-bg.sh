#!/bin/bash
SERVER_HOST="basement"

echo "Starting EDDA server in background..."
ssh $SERVER_HOST "cd edda-server && nohup dotnet run --project EDDA.Server > /tmp/edda.log 2>&1 & echo \$! > /tmp/edda.pid"

echo "Server started! PID saved to /tmp/edda.pid"
echo "Watch logs: ./scripts/logs-server.sh"