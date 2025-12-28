"""Audio cache management for EDDA voice client."""

import os
import json
import time
from pathlib import Path
from typing import Optional, Dict, Any
from datetime import datetime, timedelta


class CacheManager:
    """
    Manages cached audio files with configurable clearing policies.
    
    Stores audio files with metadata (cache key, timestamps, audio format).
    Supports cache clearing on start, TTL-based expiry, and size limits.
    """
    
    def __init__(self, cache_dir: str, clear_policy: str|float = "never", max_size_mb: int = 100):
        """
        Initialize cache manager.
        
        Args:
            cache_dir: Directory to store cached files
            clear_policy: "on_start", "never", or TTL in hours
            max_size_mb: Maximum cache size in MB (0 = unlimited)
        """
        self.cache_dir = Path(cache_dir).resolve()
        self.clear_policy = clear_policy
        self.max_size_mb = max_size_mb
        self.metadata_file = self.cache_dir / "metadata.json"
        
        # Create cache directory if it doesn't exist
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        
        # Load or initialize metadata
        self.metadata = self._load_metadata()
        
        # Apply clearing policy
        if self.clear_policy == "on_start":
            print("[CACHE] Clearing cache (policy: on_start)")
            self.clear_all()
        elif isinstance(self.clear_policy, (int, float)) and self.clear_policy > 0:
            print(f"[CACHE] Clearing expired items (TTL: {self.clear_policy}h)")
            self._clear_expired(self.clear_policy)
        
        # Enforce size limit
        if self.max_size_mb > 0:
            self._enforce_size_limit()
        
        print(f"[CACHE] Initialized: {self.cache_dir} ({len(self.metadata)} items)")
    
    def has(self, cache_key: str) -> bool:
        """Check if a cache key exists and is valid."""
        if cache_key not in self.metadata:
            return False
        
        file_path = self.cache_dir / f"{cache_key}.wav"
        if not file_path.exists():
            # File missing, remove from metadata
            del self.metadata[cache_key]
            self._save_metadata()
            return False
        
        return True
    
    def get(self, cache_key: str) -> Optional[bytes]:
        """
        Retrieve cached audio data.
        
        Args:
            cache_key: Cache key identifier
            
        Returns:
            Audio bytes if found, None otherwise
        """
        if not self.has(cache_key):
            return None
        
        try:
            file_path = self.cache_dir / f"{cache_key}.wav"
            with open(file_path, 'rb') as f:
                data = f.read()
            
            # Update last accessed time
            self.metadata[cache_key]["last_accessed"] = datetime.now().isoformat()
            self._save_metadata()
            
            return data
        except Exception as e:
            print(f"[CACHE] Error reading {cache_key}: {e}")
            return None
    
    def get_path(self, cache_key: str) -> Optional[Path]:
        """
        Get file path for cached audio.
        
        Args:
            cache_key: Cache key identifier
            
        Returns:
            Path object if found, None otherwise
        """
        if not self.has(cache_key):
            return None
        return self.cache_dir / f"{cache_key}.wav"
    
    def store(self, cache_key: str, audio_data: bytes, sample_rate: int, channels: int, duration_ms: int) -> bool:
        """
        Store audio in cache with metadata.
        
        Args:
            cache_key: Unique cache key
            audio_data: Complete WAV file as bytes
            sample_rate: Audio sample rate (Hz)
            channels: Number of audio channels
            duration_ms: Audio duration in milliseconds
            
        Returns:
            True if stored successfully, False otherwise
        """
        try:
            file_path = self.cache_dir / f"{cache_key}.wav"
            
            # Write audio file
            with open(file_path, 'wb') as f:
                f.write(audio_data)
            
            # Update metadata
            self.metadata[cache_key] = {
                "created": datetime.now().isoformat(),
                "last_accessed": datetime.now().isoformat(),
                "size_bytes": len(audio_data),
                "sample_rate": sample_rate,
                "channels": channels,
                "duration_ms": duration_ms
            }
            self._save_metadata()
            
            print(f"[CACHE] Stored {cache_key}: {len(audio_data)}B, {duration_ms}ms @ {sample_rate}Hz")
            
            # Enforce size limit after adding
            if self.max_size_mb > 0:
                self._enforce_size_limit()
            
            return True
        except Exception as e:
            print(f"[CACHE] Error storing {cache_key}: {e}")
            return False
    
    def clear_all(self):
        """Clear all cached files and metadata."""
        try:
            for file in self.cache_dir.glob("*.wav"):
                file.unlink()
            self.metadata = {}
            self._save_metadata()
            print("[CACHE] Cleared all cache files")
        except Exception as e:
            print(f"[CACHE] Error clearing cache: {e}")
    
    def get_info(self, cache_key: str) -> Optional[Dict[str, Any]]:
        """Get metadata for a cached item."""
        return self.metadata.get(cache_key)
    
    def list_keys(self) -> list[str]:
        """List all cache keys."""
        return list(self.metadata.keys())
    
    def _load_metadata(self) -> Dict[str, Dict[str, Any]]:
        """Load cache metadata from disk."""
        if not self.metadata_file.exists():
            return {}
        
        try:
            with open(self.metadata_file, 'r') as f:
                return json.load(f)
        except Exception as e:
            print(f"[CACHE] Error loading metadata: {e}")
            return {}
    
    def _save_metadata(self):
        """Save cache metadata to disk."""
        try:
            with open(self.metadata_file, 'w') as f:
                json.dump(self.metadata, f, indent=2)
        except Exception as e:
            print(f"[CACHE] Error saving metadata: {e}")
    
    def _clear_expired(self, ttl_hours: float):
        """Remove cache entries older than TTL."""
        cutoff = datetime.now() - timedelta(hours=ttl_hours)
        expired_keys = []
        
        for key, meta in self.metadata.items():
            try:
                created = datetime.fromisoformat(meta["created"])
                if created < cutoff:
                    expired_keys.append(key)
            except Exception:
                # Invalid timestamp, consider expired
                expired_keys.append(key)
        
        for key in expired_keys:
            file_path = self.cache_dir / f"{key}.wav"
            if file_path.exists():
                file_path.unlink()
            del self.metadata[key]
        
        if expired_keys:
            self._save_metadata()
            print(f"[CACHE] Removed {len(expired_keys)} expired items")
    
    def _enforce_size_limit(self):
        """Remove oldest items if cache exceeds size limit."""
        max_bytes = self.max_size_mb * 1024 * 1024
        total_size = sum(meta.get("size_bytes", 0) for meta in self.metadata.values())
        
        if total_size <= max_bytes:
            return
        
        # Sort by last accessed time (oldest first)
        items = sorted(
            self.metadata.items(),
            key=lambda x: x[1].get("last_accessed", ""),
        )
        
        removed = []
        for key, meta in items:
            if total_size <= max_bytes:
                break
            
            size = meta.get("size_bytes", 0)
            file_path = self.cache_dir / f"{key}.wav"
            if file_path.exists():
                file_path.unlink()
            del self.metadata[key]
            total_size -= size
            removed.append(key)
        
        if removed:
            self._save_metadata()
            print(f"[CACHE] Removed {len(removed)} items to enforce size limit")
