import { useEffect, useRef, useCallback } from "react";

interface UseWebSocketOptions {
  url: string;
  onMessage: (data: any) => void;
  onOpen?: () => void;
  onClose?: () => void;
  onError?: (error: Event) => void;
}

export function useWebSocket({
  url,
  onMessage,
  onOpen,
  onClose,
  onError,
}: UseWebSocketOptions) {
  const wsRef = useRef<WebSocket | null>(null);
  const reconnectTimeoutRef = useRef<ReturnType<typeof setTimeout>>();
  const retryCountRef = useRef(0);
  const mountedRef = useRef(true);

  // Store callbacks in refs to avoid recreating connect
  const callbacksRef = useRef({ onMessage, onOpen, onClose, onError });
  callbacksRef.current = { onMessage, onOpen, onClose, onError };

  const connect = useCallback(() => {
    if (!mountedRef.current) return;
    if (wsRef.current?.readyState === WebSocket.OPEN) return;
    if (wsRef.current?.readyState === WebSocket.CONNECTING) return;

    // Clean up existing connection
    if (wsRef.current) {
      wsRef.current.onclose = null;
      wsRef.current.close();
    }

    console.log("WebSocket connecting...");
    const ws = new WebSocket(url);
    wsRef.current = ws;

    ws.onopen = () => {
      if (!mountedRef.current) return;
      console.log("WebSocket connected");
      retryCountRef.current = 0;
      callbacksRef.current.onOpen?.();
    };

    ws.onmessage = (event) => {
      if (!mountedRef.current) return;
      try {
        const data = JSON.parse(event.data);
        callbacksRef.current.onMessage(data);
      } catch (e) {
        console.error("Failed to parse WebSocket message:", e);
      }
    };

    ws.onclose = () => {
      if (!mountedRef.current) return;
      console.log("WebSocket disconnected");
      callbacksRef.current.onClose?.();

      // Auto-reconnect with exponential backoff
      const delay = Math.min(1000 * Math.pow(2, retryCountRef.current), 10000);
      retryCountRef.current++;
      reconnectTimeoutRef.current = setTimeout(connect, delay);
    };

    ws.onerror = (error) => {
      console.error("WebSocket error:", error);
      callbacksRef.current.onError?.(error);
    };
  }, [url]); // Only depends on url now

  useEffect(() => {
    mountedRef.current = true;
    connect();

    return () => {
      mountedRef.current = false;
      if (reconnectTimeoutRef.current) {
        clearTimeout(reconnectTimeoutRef.current);
      }
      if (wsRef.current) {
        wsRef.current.onclose = null;
        wsRef.current.close();
      }
    };
  }, [connect]);

  const send = useCallback((data: any) => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      wsRef.current.send(JSON.stringify(data));
    }
  }, []);

  const reconnect = useCallback(() => {
    if (reconnectTimeoutRef.current) {
      clearTimeout(reconnectTimeoutRef.current);
    }
    retryCountRef.current = 0;
    if (wsRef.current) {
      wsRef.current.onclose = null;
      wsRef.current.close();
    }
    connect();
  }, [connect]);

  return { send, reconnect };
}
