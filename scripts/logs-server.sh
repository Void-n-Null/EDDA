#!/bin/bash
SERVER_HOST="basement"

echo "Tailing server logs (Ctrl+C to exit)..."
echo ""

# Show existing log first, then follow
ssh $SERVER_HOST "cat /tmp/edda.log && echo '' && echo '--- Following live output ---' && echo '' && tail -f /tmp/edda.log"