import Hls from 'hls.js';
import { useEffect, useRef, type MutableRefObject } from 'react';

interface Props {
  src: string | null;
  /** Live streams hug the live edge; playback starts from the first segment. */
  live?: boolean;
  controls?: boolean;
  poster?: string;
  className?: string;
  videoRef?: MutableRefObject<HTMLVideoElement | null>;
  onPlaying?: () => void;
  onTimeUpdate?: (seconds: number) => void;
  onFatalError?: (message: string) => void;
}

export function HlsPlayer({ src, live = false, controls = false, poster, className, videoRef, onPlaying, onTimeUpdate, onFatalError }: Props) {
  const ref = useRef<HTMLVideoElement | null>(null);
  // Keep the latest callbacks without tearing down the player when they change.
  const callbacks = useRef({ onPlaying, onTimeUpdate, onFatalError });
  callbacks.current = { onPlaying, onTimeUpdate, onFatalError };

  useEffect(() => {
    const video = ref.current;
    if (!video || !src) return;

    let hls: Hls | null = null;
    const play = () => video.play().catch(() => undefined);

    if (Hls.isSupported()) {
      hls = new Hls(
        live
          ? { liveSyncDurationCount: 2, liveMaxLatencyDurationCount: 8, maxLiveSyncPlaybackRate: 1.25, backBufferLength: 10 }
          : { startPosition: 0, maxBufferLength: 30, backBufferLength: 900 },
      );
      hls.on(Hls.Events.MANIFEST_PARSED, play);
      hls.on(Hls.Events.ERROR, (_event, data) => {
        if (!data.fatal) return;
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
          hls?.recoverMediaError();
        } else {
          callbacks.current.onFatalError?.(data.details);
        }
      });
      hls.loadSource(src);
      hls.attachMedia(video);
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      // Safari plays HLS natively.
      video.src = src;
      video.onerror = () => callbacks.current.onFatalError?.('Playback error');
      play();
    } else {
      callbacks.current.onFatalError?.('This browser cannot play HLS video');
    }

    return () => {
      hls?.destroy();
      video.onerror = null;
      video.removeAttribute('src');
      video.load();
    };
  }, [src, live]);

  return (
    <video
      ref={(el) => {
        ref.current = el;
        if (videoRef) videoRef.current = el;
      }}
      className={className}
      poster={poster}
      controls={controls}
      muted
      playsInline
      onPlaying={() => callbacks.current.onPlaying?.()}
      onTimeUpdate={(e) => callbacks.current.onTimeUpdate?.(e.currentTarget.currentTime)}
    />
  );
}
