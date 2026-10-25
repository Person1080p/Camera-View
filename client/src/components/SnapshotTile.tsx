import { useEffect, useState } from 'react';
import { api, errorMessage, snapshotUrl, type Channel } from '../api';

interface Props {
  deviceId: number;
  channel: Channel;
  /** Changing this fetches a new picture. */
  refreshKey: number;
  large?: boolean;
  onOpen?: () => void;
}

type Status = 'loading' | 'loaded' | 'error';

/**
 * One camera as a still picture, fetched only when asked, for sites whose link can't carry video. Grid tiles use
 * the sub stream's small picture; the large view uses the main stream's. The previous picture stays up while a
 * new one loads.
 */
export function SnapshotTile({ deviceId, channel, refreshKey, large, onOpen }: Props) {
  const [image, setImage] = useState<string | null>(null);
  const [takenAt, setTakenAt] = useState<Date | null>(null);
  const [status, setStatus] = useState<Status>('loading');
  const [error, setError] = useState('');
  const [own, setOwn] = useState(0);

  useEffect(() => {
    if (!channel.online) return;
    const abort = new AbortController();
    setStatus('loading');
    api
      .blob(snapshotUrl(deviceId, channel.id, !large), abort.signal)
      .then((blob) => {
        setImage(URL.createObjectURL(blob));
        setTakenAt(new Date());
        setStatus('loaded');
      })
      .catch((e) => {
        if (abort.signal.aborted) return;
        setStatus('error');
        setError(errorMessage(e));
      });
    return () => abort.abort();
  }, [deviceId, channel.id, channel.online, large, refreshKey, own]);

  useEffect(() => () => void (image && URL.revokeObjectURL(image)), [image]);

  return (
    <div className={`tile${large ? ' tile-large' : ''}`} onDoubleClick={onOpen}>
      {!channel.online ? (
        <div className="tile-empty">No signal</div>
      ) : image ? (
        <img src={image} alt="" className="tile-video" />
      ) : (
        <div className="tile-empty" />
      )}
      <div className="tile-overlay">
        <span className="tile-name">
          <span className={`dot ${!channel.online ? 'dot-off' : status === 'loaded' ? 'dot-live' : 'dot-wait'}`} />
          {channel.id}. {channel.name}
        </span>
        {channel.online && (
          <span className="tile-actions">
            <button className="tile-btn" onClick={() => setOwn((n) => n + 1)} disabled={status === 'loading'} title="Refresh this camera">
              ↻
            </button>
            {onOpen && (
              <button className="tile-btn" onClick={onOpen} title="Open">
                ⤢
              </button>
            )}
          </span>
        )}
      </div>
      {channel.online && (
        <div className="tile-status">
          {status === 'loading' ? (image ? 'Updating…' : 'Loading picture…') : status === 'error' ? error : `Picture from ${takenAt?.toLocaleTimeString()}`}
        </div>
      )}
    </div>
  );
}
