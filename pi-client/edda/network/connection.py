"""WebSocket connection handling."""

import asyncio
import json
import base64
from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from typing import AsyncGenerator, Optional, Callable, Any
import websockets


class MessageType(str, Enum):
    """Server message types."""
    AUDIO_PLAYBACK = "audio_playback"
    RESPONSE_COMPLETE = "response_complete"


@dataclass
class AudioPlaybackMessage:
    """Parsed audio playback message from server."""
    data: bytes  # Decoded audio data
    chunk: int
    total_chunks: int


@dataclass
class ServerMessage:
    """Wrapper for messages received from server."""
    type: MessageType
    audio: Optional[AudioPlaybackMessage] = None


class ServerConnection:
    """
    Manages WebSocket connection to the EDDA server.
    
    Handles:
    - Connection lifecycle
    - Message sending (audio chunks, end speech)
    - Message receiving with parsing
    - Automatic reconnection is handled at the orchestration level
    
    Usage:
        conn = ServerConnection(url)
        async with conn.connect() as ws:
            await conn.send_audio_chunk(audio_data)
            async for msg in conn.receive_messages():
                if msg.type == MessageType.AUDIO_PLAYBACK:
                    play(msg.audio.data)
    """
    
    def __init__(self, server_url: str):
        """
        Initialize the connection.
        
        Args:
            server_url: WebSocket URL (e.g., ws://10.0.0.176:8080/ws)
        """
        self.server_url = server_url
        self._websocket: Optional[websockets.WebSocketClientProtocol] = None
    
    @property
    def is_connected(self) -> bool:
        """Check if currently connected."""
        return self._websocket is not None and self._websocket.open
    
    def connect(self):
        """
        Connect to the server.
        Returns a context manager for the connection.
        
        Usage:
            async with conn.connect() as ws:
                ...
        """
        print(f"Connecting to {self.server_url}...")
        return websockets.connect(self.server_url)
    
    async def send_audio_chunk(self, websocket, audio_data: bytes) -> bool:
        """
        Send an audio chunk to the server.
        
        Args:
            websocket: Active WebSocket connection
            audio_data: Raw PCM audio bytes
            
        Returns:
            True if sent successfully, False otherwise
        """
        try:
            encoded_data = base64.b64encode(audio_data).decode('utf-8')
            message = {
                "type": "audio_chunk",
                "data": encoded_data,
                "timestamp": datetime.now().isoformat()
            }
            await websocket.send(json.dumps(message))
            return True
        except Exception as e:
            print(f"[ERROR] Failed to send audio chunk: {e}")
            return False
    
    async def send_end_speech(self, websocket) -> bool:
        """
        Signal end of speech to the server.
        
        Args:
            websocket: Active WebSocket connection
            
        Returns:
            True if sent successfully, False otherwise
        """
        try:
            message = {
                "type": "end_speech",
                "timestamp": datetime.now().isoformat()
            }
            await websocket.send(json.dumps(message))
            return True
        except Exception as e:
            print(f"[ERROR] Failed to send end_speech: {e}")
            return False
    
    async def receive_messages(self, websocket) -> AsyncGenerator[ServerMessage, None]:
        """
        Async generator that yields parsed messages from the server.
        
        Args:
            websocket: Active WebSocket connection
            
        Yields:
            ServerMessage objects
            
        Raises:
            websockets.exceptions.ConnectionClosed: When connection closes
        """
        async for raw_message in websocket:
            message = self._parse_message(raw_message)
            if message is not None:
                yield message
    
    def _parse_message(self, raw_message: str) -> Optional[ServerMessage]:
        """
        Parse a raw message from the server.
        
        Args:
            raw_message: JSON string from WebSocket
            
        Returns:
            Parsed ServerMessage or None if invalid/unknown
        """
        try:
            data = json.loads(raw_message)
            msg_type = data.get("type")
            
            if msg_type == MessageType.AUDIO_PLAYBACK.value:
                audio_data = base64.b64decode(data["data"])
                audio_msg = AudioPlaybackMessage(
                    data=audio_data,
                    chunk=data.get("chunk", 1),
                    total_chunks=data.get("total_chunks", 1)
                )
                return ServerMessage(type=MessageType.AUDIO_PLAYBACK, audio=audio_msg)
            
            elif msg_type == MessageType.RESPONSE_COMPLETE.value:
                return ServerMessage(type=MessageType.RESPONSE_COMPLETE)
            
            else:
                # Unknown message type - log and skip
                print(f"[WARN] Unknown message type: {msg_type}")
                return None
                
        except json.JSONDecodeError:
            print(f"[WARN] Received non-JSON message: {raw_message[:100]}")
            return None
        except KeyError as e:
            print(f"[WARN] Message missing required field: {e}")
            return None
        except Exception as e:
            print(f"[ERROR] Error parsing message: {e}")
            return None
