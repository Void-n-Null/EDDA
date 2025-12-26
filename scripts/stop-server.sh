#!/bin/bash
SERVER_HOST="basement"

echo "Stopping EDDA server..."
ssh $SERVER_HOST "if [ -f /tmp/edda.pid ]; then kill \$(cat /tmp/edda.pid) && rm /tmp/edda.pid; echo 'Server stopped'; else echo 'No PID file found'; fi"