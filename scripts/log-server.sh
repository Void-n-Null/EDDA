#!/bin/bash
SERVER_HOST="basement"

echo "Tailing server logs (Ctrl+C to exit)..."
ssh $SERVER_HOST "tail -f /tmp/edda.log"