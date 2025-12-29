"""
Server message handling - dispatches incoming messages to appropriate handlers.
Mirrors the server's thin-handler pattern: parse, dispatch, delegate.
"""

import asyncio
from datetime import datetime
from typing import TYPE_CHECKING

from .connection import (
    MessageType, ServerMessage,
    AudioSentenceMessage, AudioCachePlayMessage, AudioCacheStoreMessage, StatusMessage
)

if TYPE_CHECKING:
    from ..audio import AudioPlayer
    from ..cache import CacheManager
    from ..speech import SpeechDetector


class MessageHandler:
    """
    Handles incoming server messages and coordinates playback.
    
    Responsibilities:
    - Dispatch messages to appropriate handlers
    - Manage playback state (playback_event)
    - Track TTFA metrics
    - Coordinate with AudioPlayer, CacheManager
    
    This is the client-side equivalent of the server's VoiceSession:
    encapsulates state and provides a clean interface.
    """
    
    def __init__(
        self,
        player: "AudioPlayer",
        cache_manager: "CacheManager",
        detector: "SpeechDetector",
        playback_event: asyncio.Event,
    ):
        self._player = player
        self._cache = cache_manager
        self._detector = detector
        self._playback_event = playback_event
        
        # Response state
        self._pending_response_complete = False
        self._tts_first_chunk = True
        
        # Debug counters
        self._stream_chunk_counts: dict[str, int] = {}
    
    async def handle(self, msg: ServerMessage) -> None:
        """Dispatch message to appropriate handler."""
        handlers = {
            MessageType.AUDIO_STREAM_START: self._handle_stream_start,
            MessageType.AUDIO_STREAM_CHUNK: self._handle_stream_chunk,
            MessageType.AUDIO_STREAM_END: self._handle_stream_end,
            MessageType.AUDIO_SENTENCE: self._handle_sentence,
            MessageType.AUDIO_LOADING: self._handle_loading,
            MessageType.AUDIO_PLAYBACK: self._handle_playback,
            MessageType.AUDIO_CACHE_PLAY: self._handle_cache_play,
            MessageType.AUDIO_CACHE_STORE: self._handle_cache_store,
            MessageType.RESPONSE_COMPLETE: self._handle_response_complete,
            MessageType.STATUS: self._handle_status,
        }
        
        handler = handlers.get(msg.type)
        if handler:
            await handler(msg)
    
    # =========================================================================
    # Stream handlers (loading audio uses streaming)
    # =========================================================================
    
    async def _handle_stream_start(self, msg: ServerMessage) -> None:
        start = msg.stream_start
        if start is None:
            return
        
        self._playback_event.set()
        self._player.stop_current()
        
        if start.stream == "tts":
            self._tts_first_chunk = True
        
        ok = self._player.start_stream(
            stream_kind=start.stream,
            sample_rate=start.sample_rate,
            channels=start.channels,
            sample_format=start.sample_format,
            tempo=start.tempo,
        )
        
        if ok:
            self._stream_chunk_counts[start.stream] = 0
        else:
            print(f"[WARN] Failed to start stream {start.stream}")
    
    async def _handle_stream_chunk(self, msg: ServerMessage) -> None:
        chunk = msg.stream_chunk
        if chunk is None:
            return
        
        # TTFA measurement for TTS streams
        if self._tts_first_chunk and chunk.stream == "tts":
            self._log_ttfa()
            self._tts_first_chunk = False
        
        # Periodic logging
        count = self._stream_chunk_counts.get(chunk.stream, 0) + 1
        self._stream_chunk_counts[chunk.stream] = count
        if count % 25 == 0:
            print(f"[RECV] stream={chunk.stream} chunks={count}")
        
        self._player.write_stream(chunk.data)
    
    async def _handle_stream_end(self, msg: ServerMessage) -> None:
        if msg.stream:
            print(f"[RECV] stream_end: {msg.stream}")
        self._player.end_stream()
    
    # =========================================================================
    # Sentence handler (TTS uses complete WAV files)
    # =========================================================================
    
    async def _handle_sentence(self, msg: ServerMessage) -> None:
        sentence = msg.audio_sentence
        if sentence is None:
            return
        
        self._playback_event.set()
        self._player.stop_current()
        
        # TTFA on first sentence
        if sentence.sentence_index == 1:
            self._log_ttfa()
        
        self._log_sentence(sentence)
        
        # Play in executor (blocking)
        loop = asyncio.get_running_loop()
        await loop.run_in_executor(
            None,
            self._player.play_wav_sentence,
            sentence.data,
            sentence.sentence_index,
            sentence.total_sentences,
            sentence.duration_ms,
            sentence.sample_rate,
            sentence.tempo_applied,
        )
        
        # Resume mic after final sentence if response is complete
        if sentence.sentence_index == sentence.total_sentences and self._pending_response_complete:
            print("[RECV] Final sentence played - resuming mic capture")
            self._playback_event.clear()
            self._pending_response_complete = False
    
    # =========================================================================
    # Legacy/loading audio handlers
    # =========================================================================
    
    async def _handle_loading(self, msg: ServerMessage) -> None:
        """Handle loading audio (deprecated, use cache)."""
        audio = msg.audio
        if audio is None:
            return
        
        print(f"[RECV] Loading audio ({len(audio.data)} bytes)")
        self._playback_event.set()
        self._player.play_wav_async(audio.data)
    
    async def _handle_playback(self, msg: ServerMessage) -> None:
        """Handle audio playback (legacy chunked TTS)."""
        audio = msg.audio
        if audio is None:
            return
        
        self._player.stop_current()
        
        if audio.chunk == 1:
            self._log_ttfa()
        
        print(f"[RECV] Audio chunk {audio.chunk}/{audio.total_chunks} ({len(audio.data)}B)")
        self._playback_event.set()
        
        loop = asyncio.get_running_loop()
        await loop.run_in_executor(None, self._player.play_wav, audio.data)
    
    # =========================================================================
    # Cache handlers
    # =========================================================================
    
    async def _handle_cache_play(self, msg: ServerMessage) -> None:
        cache_play = msg.cache_play
        if cache_play is None:
            return
        
        print(f"[CACHE] Play request: {cache_play.cache_key} (loop={cache_play.loop})")
        
        cached_data = self._cache.get(cache_play.cache_key)
        if cached_data:
            print(f"[CACHE] Hit: {cache_play.cache_key} ({len(cached_data)}B)")
            self._playback_event.set()
            
            if cache_play.loop:
                self._player.play_wav_async(cached_data)
            else:
                loop = asyncio.get_running_loop()
                await loop.run_in_executor(None, self._player.play_wav, cached_data)
        else:
            print(f"[CACHE] Miss: {cache_play.cache_key}")
    
    async def _handle_cache_store(self, msg: ServerMessage) -> None:
        cache_store = msg.cache_store
        if cache_store is None:
            return
        
        if self._cache.has(cache_store.cache_key):
            print(f"[CACHE] Already stored: {cache_store.cache_key}")
            return
        
        self._cache.store(
            cache_store.cache_key,
            cache_store.data,
            cache_store.sample_rate,
            cache_store.channels,
            cache_store.duration_ms,
        )
        
        print(f"[CACHE] Stored: {cache_store.cache_key}")
        self._playback_event.set()
        self._player.play_wav_async(cache_store.data)
    
    # =========================================================================
    # Response lifecycle
    # =========================================================================
    
    async def _handle_response_complete(self, msg: ServerMessage) -> None:
        self._pending_response_complete = True
        self._player.stop_current()
        
        if self._playback_event.is_set():
            print("[RECV] Response complete - resuming mic")
            self._playback_event.clear()
    
    async def _handle_status(self, msg: ServerMessage) -> None:
        """Handle session status updates from server."""
        status = msg.status
        if status is None:
            return
        
        # Just log for now - can add visual/audio feedback later
        state = status.state
        if state == "active":
            print("[STATUS] Session activated - listening")
        elif state == "inactive":
            print("[STATUS] Inactive - waiting for wake word")
        elif state == "deactivated":
            print("[STATUS] Session deactivated - goodbye")
    
    # =========================================================================
    # Helpers
    # =========================================================================
    
    def _log_ttfa(self) -> None:
        """Log time-to-first-audio if we have a speech end timestamp."""
        if self._detector.last_speech_end_time:
            ttfa = (datetime.now() - self._detector.last_speech_end_time).total_seconds() * 1000
            print(f"\n⚡ TIME TO FIRST AUDIO: {ttfa:.0f}ms")
            self._detector.clear_speech_end_time()
    
    def _log_sentence(self, sentence: AudioSentenceMessage) -> None:
        print(
            f"[RECV] Sentence {sentence.sentence_index}/{sentence.total_sentences}: "
            f"{sentence.duration_ms}ms @ {sentence.sample_rate}Hz "
            f"(tempo={sentence.tempo_applied:.3f}x, size={len(sentence.data)}B)"
        )
    
    def reset(self) -> None:
        """Reset state for new connection."""
        self._pending_response_complete = False
        self._tts_first_chunk = True
        self._stream_chunk_counts.clear()
