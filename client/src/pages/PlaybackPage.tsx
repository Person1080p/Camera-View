import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, errorMessage, type Segment, type StreamInfo } from '../api';
import { HlsPlayer } from '../components/HlsPlayer';
import { Timeline } from '../components/Timeline';
import { useChannels, useDevices, useResource, useStoredState } from '../hooks';
import {
  addSeconds, atSecondOfDay, clock, clockFromSeconds, datePart, diffSeconds, formatDuration,
  parseClock, secondOfDay, SECONDS_PER_DAY, today,
} from '../time';

interface Session {
  stream: StreamInfo;
  /** Device-local time the stream starts at; position = start + video.currentTime. */
  start: string;
}

const MAX_EXPORT_MINUTES = 120;

export function PlaybackPage() {
  const [params, setParams] = useSearchParams();
  const devices = useDevices();
  const deviceId = Number(params.get('device')) || devices.data?.[0]?.id || null;
  const device = devices.data?.find((d) => d.id === deviceId) ?? null;
  const channels = useChannels(deviceId);
  const channelId = Number(params.get('channel')) || channels.data?.find((c) => c.online)?.id || channels.data?.[0]?.id || null;
  const channel = channels.data?.find((c) => c.id === channelId) ?? null;
  const date = params.get('date') || today();

  const recordings = useResource<{ date: string; segments: Segment[] }>(
    deviceId && channelId ? `/api/devices/${deviceId}/channels/${channelId}/recordings?date=${date}` : null,
  );
  const segments = recordings.data?.segments ?? [];
  const recordedSeconds = segments.reduce((sum, s) => sum + diffSeconds(s.end, s.start), 0);

  const [quality, setQuality] = useStoredState('playback.quality', 'sd');
  const [session, setSession] = useState<Session | null>(null);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [streamEnded, setStreamEnded] = useState(false);
  const [position, setPosition] = useState<string | null>(null);
  const [gotoText, setGotoText] = useState('');
  const [markIn, setMarkIn] = useState<number | null>(null);
  const [markOut, setMarkOut] = useState<number | null>(null);
  const [codec, setCodec] = useState<'h264' | 'h265'>('h264');
  const [notice, setNotice] = useState<string | null>(null);

  const videoRef = useRef<HTMLVideoElement | null>(null);
  const sessionId = useRef<string | null>(null);
  const requestSeq = useRef(0);

  const setParam = (patch: Record<string, string | null>) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(patch)) {
      if (v === null) next.delete(k);
      else next.set(k, v);
    }
    setParams(next, { replace: true });
  };

  // The recorder serves few simultaneous playback streams, so release ours as soon as it's not needed.
  const stopPlayback = useCallback(() => {
    requestSeq.current++;
    const id = sessionId.current;
    sessionId.current = null;
    setSession(null);
    setStarting(false);
    if (id) api.del(`/api/streams/${id}`).catch(() => undefined);
  }, []);

  useEffect(() => stopPlayback, [stopPlayback]);

  useEffect(() => {
    stopPlayback();
    setPosition(null);
    setError(null);
    setMarkIn(null);
    setMarkOut(null);
  }, [deviceId, channelId, date, stopPlayback]);

  const startAt = async (iso: string) => {
    if (!deviceId || !channelId) return;
    stopPlayback();
    const seq = ++requestSeq.current;
    setError(null);
    setStreamEnded(false);
    setNotice(null);
    setStarting(true);
    setPosition(iso);
    try {
      const stream = await api.post<StreamInfo>('/api/streams/playback', { deviceId, channel: channelId, start: iso, quality });
      if (seq !== requestSeq.current) {
        // A newer request (or a stop) superseded this one while it was starting.
        api.del(`/api/streams/${stream.id}`).catch(() => undefined);
        return;
      }
      sessionId.current = stream.id;
      setSession({ stream, start: iso });
    } catch (e) {
      if (seq === requestSeq.current) setError(errorMessage(e));
    } finally {
      if (seq === requestSeq.current) setStarting(false);
    }
  };

  const spans = segments.map((s) => [secondOfDay(s.start), secondOfDay(s.end) <= secondOfDay(s.start) ? SECONDS_PER_DAY : secondOfDay(s.end)] as const);

  /** Clicking in a gap jumps forward to the next recording. */
  const snapToRecording = (sec: number): number | null => {
    const inside = spans.find(([a, b]) => sec >= a && sec < b);
    if (inside) return sec;
    const next = spans.find(([a]) => a > sec);
    return next ? next[0] : null;
  };

  const seekToSecond = (sec: number) => {
    const target = snapToRecording(Math.min(Math.max(sec, 0), SECONDS_PER_DAY - 1));
    if (target === null) {
      setError(`No recording at or after ${clockFromSeconds(sec)} on ${date}.`);
      return;
    }
    const iso = atSecondOfDay(date, target);
    const v = videoRef.current;
    if (session && v && v.seekable.length) {
      // Seek within what has already been streamed instead of asking the recorder again.
      const offset = diffSeconds(iso, session.start);
      const end = v.seekable.end(v.seekable.length - 1);
      if (offset >= 0 && offset <= end - 1) {
        v.currentTime = offset;
        return;
      }
    }
    void startAt(iso);
  };

  const positionSec = position && datePart(position) === date ? secondOfDay(position) : null;

  const jump = (delta: number) => {
    if (positionSec !== null) seekToSecond(positionSec + delta);
  };

  const goto = () => {
    const sec = parseClock(gotoText);
    if (sec === null) setError('Enter a time like 14:30 or 14:30:15');
    else seekToSecond(sec);
  };

  const exportDuration = markIn !== null && markOut !== null ? markOut - markIn : 0;

  const createExport = async () => {
    setError(null);
    setNotice(null);
    if (markIn === null || markOut === null || markOut <= markIn) {
      setError('Set a start (mark in) before the end (mark out) to export.');
      return;
    }
    if (exportDuration > MAX_EXPORT_MINUTES * 60) {
      setError(`Exports are limited to ${MAX_EXPORT_MINUTES} minutes.`);
      return;
    }
    const stopsPlayback = session !== null && (device?.playbackLimit ?? 1) <= 1;
    if (stopsPlayback && !window.confirm('This recorder serves only one recording stream at a time, so playback will stop while the export runs. Continue?')) {
      return;
    }
    if (stopsPlayback) stopPlayback();
    try {
      await api.post('/api/exports', {
        deviceId,
        channel: channelId,
        start: atSecondOfDay(date, markIn),
        end: atSecondOfDay(date, markOut),
        codec,
      });
      setNotice(`Export queued (${formatDuration(exportDuration)} of video; it takes about that long to record).`);
    } catch (e) {
      setError(errorMessage(e));
    }
  };

  const shiftDate = (days: number) => {
    const d = addSeconds(`${date}T12:00:00`, days * SECONDS_PER_DAY);
    setParam({ date: datePart(d) });
  };

  return (
    <div className="split">
      <aside className="sidebar">
        <label>
          Device
          <select value={deviceId ?? ''} onChange={(e) => setParam({ device: e.target.value, channel: null })}>
            {(devices.data ?? []).map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Channel
          <select value={channelId ?? ''} onChange={(e) => setParam({ channel: e.target.value })}>
            {(channels.data ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {c.id}. {c.name}
                {c.online ? '' : ' (no signal)'}
              </option>
            ))}
          </select>
        </label>
        <label>
          Date
          <div className="row tight">
            <button onClick={() => shiftDate(-1)} title="Previous day">
              ‹
            </button>
            <input type="date" value={date} onChange={(e) => e.target.value && setParam({ date: e.target.value })} />
            <button onClick={() => shiftDate(1)} title="Next day">
              ›
            </button>
          </div>
        </label>
        <label>
          Quality
          <select value={quality} onChange={(e) => setQuality(e.target.value)}>
            <option value="sd">Standard (480p, faster)</option>
            <option value="hd">High (720p)</option>
          </select>
        </label>
        <div className="muted small">
          {recordings.loading
            ? 'Searching recordings…'
            : recordings.error
              ? recordings.error
              : `${segments.length} recording span${segments.length === 1 ? '' : 's'}, ${formatDuration(recordedSeconds)} total`}
        </div>

        <div className="panel">
          <h3>Export to MP4</h3>
          <div className="mark-row">
            <label>
              Start
              <input
                type="time"
                step={1}
                value={markIn === null ? '' : clockFromSeconds(markIn)}
                onChange={(e) => setMarkIn(parseClock(e.target.value))}
              />
            </label>
            <button disabled={positionSec === null} onClick={() => setMarkIn(positionSec)} title="Use current playback position">
              Mark in
            </button>
          </div>
          <div className="mark-row">
            <label>
              End
              <input
                type="time"
                step={1}
                value={markOut === null ? '' : clockFromSeconds(markOut)}
                onChange={(e) => setMarkOut(parseClock(e.target.value))}
              />
            </label>
            <button disabled={positionSec === null} onClick={() => setMarkOut(positionSec)} title="Use current playback position">
              Mark out
            </button>
          </div>
          <label>
            Format
            <select value={codec} onChange={(e) => setCodec(e.target.value as 'h264' | 'h265')}>
              <option value="h264">H.264 (plays everywhere, up to 1080p)</option>
              <option value="h265">H.265 (original quality, smaller file)</option>
            </select>
          </label>
          <div className="muted small">{exportDuration > 0 ? `Clip length: ${formatDuration(exportDuration)}` : 'Pick a start and end time.'}</div>
          <button className="primary" disabled={exportDuration <= 0} onClick={createExport}>
            Export MP4
          </button>
          {notice && (
            <div className="alert ok small">
              {notice} <Link to="/exports">View exports</Link>
            </div>
          )}
        </div>
      </aside>

      <section className="main-pane">
        <div className="toolbar">
          <strong className="ellipsis">{channel ? `${channel.id}. ${channel.name}` : 'Playback'}</strong>
          <span className="clock">{position ? `${clock(position)}` : '--:--:--'}</span>
          <div className="spacer" />
          <div className="seg-group">
            <button disabled={positionSec === null} onClick={() => jump(-60)}>
              −1m
            </button>
            <button disabled={positionSec === null} onClick={() => jump(-10)}>
              −10s
            </button>
            <button disabled={positionSec === null} onClick={() => jump(10)}>
              +10s
            </button>
            <button disabled={positionSec === null} onClick={() => jump(60)}>
              +1m
            </button>
          </div>
          <form
            className="row tight"
            onSubmit={(e) => {
              e.preventDefault();
              goto();
            }}
          >
            <input className="time-input" placeholder="HH:MM:SS" value={gotoText} onChange={(e) => setGotoText(e.target.value)} />
            <button>Go</button>
          </form>
          {session && <button onClick={stopPlayback}>Stop</button>}
        </div>

        <div className="player">
          {session ? (
            <HlsPlayer
              key={session.stream.id}
              src={session.stream.playlist}
              controls
              videoRef={videoRef}
              className="player-video"
              onTimeUpdate={(t) => setPosition(addSeconds(session.start, t))}
              onFatalError={() => setStreamEnded(true)}
            />
          ) : (
            <div className="player-empty">
              {starting
                ? 'Starting playback… the recorder takes a few seconds to respond.'
                : segments.length
                  ? 'Click the timeline below to start playback.'
                  : recordings.loading
                    ? ''
                    : 'No recordings on this day.'}
            </div>
          )}
          {streamEnded && session && (
            <div className="player-banner">
              The playback stream stopped.{' '}
              <button onClick={() => position && startAt(position)}>Resume from {position ? clock(position) : 'here'}</button>
            </div>
          )}
        </div>

        {error && <div className="alert error">{error}</div>}

        <Timeline segments={segments} position={positionSec} markIn={markIn} markOut={markOut} onSeek={seekToSecond} />
      </section>
    </div>
  );
}
