import { useRef, useState } from 'react';
import type { Segment } from '../api';
import { clockFromSeconds, secondOfDay, SECONDS_PER_DAY } from '../time';

interface Props {
  segments: Segment[];
  /** Seconds of day; all marks are on the selected date. */
  position: number | null;
  markIn: number | null;
  markOut: number | null;
  onSeek: (secondOfDay: number) => void;
}

const ZOOMS = [
  { label: '24h', span: 86400, tick: 7200 },
  { label: '6h', span: 21600, tick: 1800 },
  { label: '1h', span: 3600, tick: 300 },
  { label: '10m', span: 600, tick: 60 },
];

export function Timeline({ segments, position, markIn, markOut, onSeek }: Props) {
  const [zoom, setZoom] = useState(0);
  const [hover, setHover] = useState<number | null>(null);
  const bar = useRef<HTMLDivElement>(null);

  const { span, tick } = ZOOMS[zoom];
  const center = position ?? (markIn !== null ? markIn : SECONDS_PER_DAY / 2);
  const viewStart = Math.min(Math.max(0, center - span / 2), SECONDS_PER_DAY - span);
  const pct = (s: number) => ((s - viewStart) / span) * 100;
  const visible = (a: number, b: number) => b > viewStart && a < viewStart + span;

  const secondAt = (clientX: number) => {
    const rect = bar.current!.getBoundingClientRect();
    const f = Math.min(Math.max((clientX - rect.left) / rect.width, 0), 1);
    return Math.round(viewStart + f * span);
  };

  const ticks: number[] = [];
  for (let t = Math.ceil(viewStart / tick) * tick; t <= viewStart + span; t += tick) ticks.push(t);

  const spans = segments.map((s) => {
    const a = secondOfDay(s.start);
    const endSec = secondOfDay(s.end);
    // A segment ending exactly at midnight of the next day reads as 00:00:00.
    return [a, endSec <= a ? SECONDS_PER_DAY : endSec] as const;
  });

  return (
    <div className="timeline">
      <div className="timeline-head">
        <div className="seg-group">
          {ZOOMS.map((z, i) => (
            <button key={z.label} className={i === zoom ? 'active' : ''} onClick={() => setZoom(i)}>
              {z.label}
            </button>
          ))}
        </div>
        <span className="muted small">
          {hover !== null ? `Click to play from ${clockFromSeconds(hover)}` : 'Blue = recorded. Click the bar to play from that time.'}
        </span>
      </div>
      <div
        ref={bar}
        className="timeline-bar"
        onMouseMove={(e) => setHover(secondAt(e.clientX))}
        onMouseLeave={() => setHover(null)}
        onClick={(e) => onSeek(secondAt(e.clientX))}
      >
        {spans.filter(([a, b]) => visible(a, b)).map(([a, b]) => (
          <div
            key={a}
            className="timeline-seg"
            style={{ left: `${Math.max(0, pct(a))}%`, width: `${Math.min(100, pct(b)) - Math.max(0, pct(a))}%` }}
          />
        ))}
        {markIn !== null && markOut !== null && markOut > markIn && visible(markIn, markOut) && (
          <div
            className="timeline-range"
            style={{ left: `${Math.max(0, pct(markIn))}%`, width: `${Math.min(100, pct(markOut)) - Math.max(0, pct(markIn))}%` }}
          />
        )}
        {markIn !== null && visible(markIn, markIn) && <div className="timeline-mark" style={{ left: `${pct(markIn)}%` }} />}
        {markOut !== null && visible(markOut, markOut) && <div className="timeline-mark" style={{ left: `${pct(markOut)}%` }} />}
        {position !== null && visible(position, position) && <div className="timeline-playhead" style={{ left: `${pct(position)}%` }} />}
        {hover !== null && <div className="timeline-hover" style={{ left: `${pct(hover)}%` }} />}
      </div>
      <div className="timeline-ticks">
        {ticks.map((t) => (
          <span key={t} style={{ left: `${pct(t)}%` }}>
            {clockFromSeconds(t).slice(0, span <= 600 ? 8 : 5)}
          </span>
        ))}
      </div>
    </div>
  );
}
