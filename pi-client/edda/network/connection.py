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
    AUDIO_LOADING = "audio_loading"  # Deprecated: use audio_cache_play instead
    AUDIO_STREAM_START = "audio_stream_start"
    AUDIO_STREAM_CHUNK = "audio_stream_chunk"
    AUDIO_STREAM_END = "audio_stream_end"
    AUDIO_SENTENCE = "audio_sentence"  # Complete WAV file per sentence
    AUDIO_CACHE_PLAY = "audio_cache_play"  # Request to play cached audio
    AUDIO_CACHE_STORE = "audio_cache_store"  # Send audio to cache
    RESPONSE_COMPLETE = "response_complete"


@dataclass
class AudioPlaybackMessage:
    """Parsed audio playback message from server."""
    data: bytes  # Decoded audio data
    chunk: int
    total_chunks: int


@dataclass
class AudioStreamStartMessage:
    """Start of a raw PCM audio stream."""
    stream: str  # "loading" | "tts" (or future)
    sample_rate: int
    channels: int
    sample_format: str  # e.g. "s16le"
    tempo: float = 1.0  # Playback speed multiplier (1.0 = normal, 0.92 = 8% slower)


@dataclass
class AudioStreamChunkMessage:
    """PCM chunk for a previously started stream."""
    stream: str
    data: bytes


@dataclass
class AudioSentenceMessage:
    """Complete WAV file for a single TTS sentence."""
    data: bytes  # Complete WAV file
    sentence_index: int
    total_sentences: int
    duration_ms: int
    sample_rate: int
    tempo_applied: float


@dataclass
class AudioCachePlayMessage:
    """Request to play audio from cache."""
    cache_key: str  # Unique identifier for cached audio
    loop: bool = False  # Whether to loop the audio


@dataclass
class AudioCacheStoreMessage:
    """Audio file to store in cache."""
    cache_key: str
    data: bytes  # Complete WAV file
    sample_rate: int
    channels: int
    duration_ms: int


@dataclass
class ServerMessage:
    """Wrapper for messages received from server."""
    type: MessageType
    audio: Optional[AudioPlaybackMessage] = None
    stream_start: Optional[AudioStreamStartMessage] = None
    stream_chunk: Optional[AudioStreamChunkMessage] = None
    audio_sentence: Optional[AudioSentenceMessage] = None
    cache_play: Optional[AudioCachePlayMessage] = None
    cache_store: Optional[AudioCacheStoreMessage] = None
    stream: Optional[str] = None


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
        # max_size=4MB to handle large audio messages (loading audio, TTS chunks)
        return websockets.connect(self.server_url, max_size=4 * 1024 * 1024)
    
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
    
    async def send_cache_status(self, websocket, cache_key: str, status: str) -> bool:
        """
        Send cache status to the server.
        
        Args:
            websocket: Active WebSocket connection
            cache_key: The cache key being queried
            status: "have" if cached, "need" if not cached
            
        Returns:
            True if sent successfully, False otherwise
        """
        try:
            message = {
                "type": "audio_cache_status",
                "cache_key": cache_key,
                "status": status
            }
            await websocket.send(json.dumps(message))
            return True
        except Exception as e:
            print(f"[ERROR] Failed to send cache_status: {e}")
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
            
            elif msg_type == MessageType.AUDIO_LOADING.value:
                # Loading audio - same structure but different type (can be interrupted)
                audio_data = base64.b64decode(data["data"])
                audio_msg = AudioPlaybackMessage(
                    data=audio_data,
                    chunk=1,
                    total_chunks=1
                )
                return ServerMessage(type=MessageType.AUDIO_LOADING, audio=audio_msg)

            elif msg_type == MessageType.AUDIO_STREAM_START.value:
                start_msg = AudioStreamStartMessage(
                    stream=data.get("stream", ""),
                    sample_rate=int(data.get("sample_rate", 0)),
                    channels=int(data.get("channels", 0)),
                    sample_format=data.get("sample_format", ""),
                    tempo=float(data.get("tempo", 1.0)),
                )
                return ServerMessage(type=MessageType.AUDIO_STREAM_START, stream_start=start_msg)

            elif msg_type == MessageType.AUDIO_STREAM_CHUNK.value:
                pcm = base64.b64decode(data["data"])
                chunk_msg = AudioStreamChunkMessage(
                    stream=data.get("stream", ""),
                    data=pcm,
                )
                return ServerMessage(type=MessageType.AUDIO_STREAM_CHUNK, stream_chunk=chunk_msg)

            elif msg_type == MessageType.AUDIO_STREAM_END.value:
                return ServerMessage(type=MessageType.AUDIO_STREAM_END, stream=data.get("stream", ""))
            
            elif msg_type == MessageType.AUDIO_SENTENCE.value:
                wav_data = base64.b64decode(data["data"])
                sentence_msg = AudioSentenceMessage(
                    data=wav_data,
                    sentence_index=int(data.get("sentence_index", 1)),
                    total_sentences=int(data.get("total_sentences", 1)),
                    duration_ms=int(data.get("duration_ms", 0)),
                    sample_rate=int(data.get("sample_rate", 24000)),
                    tempo_applied=float(data.get("tempo_applied", 1.0)),
                )
                return ServerMessage(type=MessageType.AUDIO_SENTENCE, audio_sentence=sentence_msg)
            
            elif msg_type == MessageType.AUDIO_CACHE_PLAY.value:
                cache_play_msg = AudioCachePlayMessage(
                    cache_key=data.get("cache_key", ""),
                    loop=bool(data.get("loop", False))
                )
                return ServerMessage(type=MessageType.AUDIO_CACHE_PLAY, cache_play=cache_play_msg)
            
            elif msg_type == MessageType.AUDIO_CACHE_STORE.value:
                wav_data = base64.b64decode(data["data"])
                cache_store_msg = AudioCacheStoreMessage(
                    cache_key=data.get("cache_key", ""),
                    data=wav_data,
                    sample_rate=int(data.get("sample_rate", 24000)),
                    channels=int(data.get("channels", 2)),
                    duration_ms=int(data.get("duration_ms", 0))
                )
                return ServerMessage(type=MessageType.AUDIO_CACHE_STORE, cache_store=cache_store_msg)
            
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
