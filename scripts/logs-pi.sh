#!/bin/bash
PI_HOST="edda"  # (changed)

echo "Tailing Pi logs (Ctrl+C to exit)..."
ssh $PI_HOST "journalctl -u edda-client -f --since '5 minutes ago'"