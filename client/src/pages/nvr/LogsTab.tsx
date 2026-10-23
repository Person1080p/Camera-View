import { useState } from 'react';
import { api, type Device, type LogEntry } from '../../api';
import { useAction, useChannels } from '../../hooks';
import { addSeconds, today } from '../../time';
import { prettyEvent } from './format';

const TYPES = ['', 'Alarm', 'Exception', 'Operation', 'Information'];
const TYPE_BADGE: Record<string, string> = {
  Alarm: 'badge-queued',
  Exception: 'badge-failed',
  Operation: 'badge-running',
  Information: '',
};

export function LogsTab({ device }: { device: Device }) {
  const channels = useChannels(device.id);
  const [from, setFrom] = useState(`${addSeconds(`${today()}T00:00:00`, -86400).slice(0, 10)}T00:00`);
  const [to, setTo] = useState(`${today()}T23:59`);
  const [type, setType] = useState('');
  const [page, setPage] = useState(0);
  const [result, setResult] = useState<{ items: LogEntry[]; hasMore: boolean } | null>(null);
  const search = useAction();

  const load = (p: number) =>
    search.run(async () => {
      const q = new URLSearchParams({ from, to, page: String(p), ...(type ? { type } : {}) });
      setResult(await api.get(`/api/devices/${device.id}/admin/logs?${q}`));
      setPage(p);
    });

  const channelName = (id: number | null) => (id === null ? '' : `${id}. ${channels.data?.find((c) => c.id === id)?.name ?? ''}`);

  return (
    <div className="stack">
      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault();
          void load(0);
        }}
      >
        <label className="w-medium">
          From <span className="muted small">(NVR time)</span>
          <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} required />
        </label>
        <label className="w-medium">
          To
          <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} required />
        </label>
        <label className="w-medium">
          Type
          <select value={type} onChange={(e) => setType(e.target.value)}>
            {TYPES.map((t) => (
              <option key={t} value={t}>
                {t || 'All events'}
              </option>
            ))}
          </select>
        </label>
        <button className="primary" disabled={search.busy}>
          {search.busy ? 'Searching…' : 'Search'}
        </button>
      </form>
      {search.error && <div className="alert error">{search.error}</div>}

      {result && (
        <>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Time</th>
                  <th>Type</th>
                  <th>Event</th>
                  <th>Camera</th>
                  <th>User</th>
                  <th>From</th>
                </tr>
              </thead>
              <tbody>
                {result.items.map((l, i) => (
                  <tr key={`${l.time}-${i}`}>
                    <td className="mono small nowrap">{l.time.replace('T', ' ')}</td>
                    <td>
                      <span className={`badge ${TYPE_BADGE[l.major] ?? ''}`}>{l.major}</span>
                    </td>
                    <td>
                      {prettyEvent(l.minor)}
                      {l.detail && <span className="muted small"> ({l.detail})</span>}
                    </td>
                    <td className="small">{channelName(l.channel)}</td>
                    <td>{l.user ?? ''}</td>
                    <td className="mono small">{l.ipAddress ?? ''}</td>
                  </tr>
                ))}
                {result.items.length === 0 && (
                  <tr>
                    <td colSpan={6} className="muted">
                      No log entries in this range.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          <div className="pager">
            <button disabled={page === 0 || search.busy} onClick={() => void load(page - 1)}>
              ‹ Newer page
            </button>
            <span className="muted small">Page {page + 1}</span>
            <button disabled={!result.hasMore || search.busy} onClick={() => void load(page + 1)}>
              Next page ›
            </button>
          </div>
        </>
      )}
    </div>
  );
}
