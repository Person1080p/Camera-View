import { useEffect, useMemo, useState } from 'react';
import { api, errorMessage, snapshotUrl, type Channel, type StreamInfo } from '../api';
import { HlsPlayer } from './HlsPlayer';

interface Props {
  deviceId: number;
  channel: Channel;
  quality: 'sd' | 'hd';
  large?: boolean;
  onOpen?: () => void;
}

type Status = 'connecting' | 'playing' | 'error';

/** One live camera. Reconnects on its own if the stream drops. */
export function LiveTile({ deviceId, channel, quality, large, onOpen }: Props) {
  const [stream, setStream] = useState<StreamInfo | null>(null);
  const [status, setStatus] = useState<Status>('connecting');
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  const poster = useMemo(() => (channel.online ? snapshotUrl(deviceId, channel.id) : undefined), [deviceId, channel.id, channel.online]);

  useEffect(() => {
    if (!channel.online) return;
    let cancelled = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    setStream(null);
    setStatus('connecting');
    api
      .post<StreamInfo>('/api/streams/live', { deviceId, channel: channel.id, quality })
      .then((s) => !cancelled && setStream(s))
      .catch((e) => {
        if (cancelled) return;
        setStatus('error');
        setError(errorMessage(e));
        retry = setTimeout(() => setAttempt((a) => a + 1), 8000);
      });
    return () => {
      cancelled = true;
      clearTimeout(retry);
    };
  }, [deviceId, channel.id, channel.online, quality, attempt]);

  const reconnect = () => {
    setStatus('error');
    setError('Stream interrupted, reconnecting…');
    setTimeout(() => setAttempt((a) => a + 1), 2000);
  };

  return (
    <div className={`tile${large ? ' tile-large' : ''}`} onDoubleClick={onOpen}>
      {channel.online ? (
        <HlsPlayer
          src={stream?.playlist ?? null}
          live
          poster={poster}
          className="tile-video"
          onPlaying={() => setStatus('playing')}
          onFatalError={reconnect}
        />
      ) : (
        <div className="tile-empty">No signal</div>
      )}
      <div className="tile-overlay">
        <span className="tile-name">
          <span className={`dot ${channel.online ? (status === 'playing' ? 'dot-live' : 'dot-wait') : 'dot-off'}`} />
          {channel.id}. {channel.name}
        </span>
        {onOpen && (
          <button className="tile-btn" onClick={onOpen} title="Open">
            ⤢
          </button>
        )}
      </div>
      {channel.online && status !== 'playing' && (
        <div className="tile-status">{status === 'connecting' ? 'Connecting…' : error}</div>
      )}
    </div>
  );
}
