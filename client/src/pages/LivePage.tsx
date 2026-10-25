import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { snapshotUrl, type Channel } from '../api';
import { useUser } from '../auth';
import { LiveTile } from '../components/LiveTile';
import { SnapshotTile } from '../components/SnapshotTile';
import { useChannels, useDevices, useStoredState } from '../hooks';
import { today } from '../time';

const LAYOUTS = [1, 4, 9, 16];

type ViewMode = 'sd' | 'hd' | 'stills';

const VIEW_MODES: { mode: ViewMode; label: string; title: string }[] = [
  { mode: 'sd', label: 'SD', title: "Video from the camera's sub stream, relayed as is" },
  { mode: 'hd', label: 'HD', title: 'Video from the main stream, converted by the server (more CPU and bandwidth per camera)' },
  { mode: 'stills', label: 'Stills', title: 'A still picture per camera, updated only when you press Refresh. For slow connections' },
];

function ViewModeToggle({ value, onChange }: { value: string; onChange: (m: ViewMode) => void }) {
  return (
    <div className="seg-group">
      {VIEW_MODES.map((m) => (
        <button key={m.mode} className={value === m.mode ? 'active' : ''} onClick={() => onChange(m.mode)} title={m.title}>
          {m.label}
        </button>
      ))}
    </div>
  );
}

function RefreshButton({ onClick }: { onClick: () => void }) {
  return (
    <button onClick={onClick} title="Fetch new pictures">
      ↻ Refresh
    </button>
  );
}

export function LivePage() {
  const user = useUser();
  const navigate = useNavigate();
  const devices = useDevices();
  const [storedDevice, setStoredDevice] = useStoredState('live.device', '');
  const [layout, setLayout] = useStoredState('live.layout', '4');
  const [hideOffline, setHideOffline] = useStoredState('live.hideOffline', '1');
  const [page, setPage] = useState(0);
  const [focus, setFocus] = useState<Channel | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  const deviceList = devices.data ?? [];
  const deviceId = deviceList.find((d) => String(d.id) === storedDevice)?.id ?? deviceList[0]?.id ?? null;
  // Remembered per device, so a site on a slow link can stay on stills while local ones play video.
  const [focusMode, setFocusMode] = useStoredState(`live.focusMode.${deviceId}`, 'hd');
  const [gridMode, setGridMode] = useStoredState(`live.gridMode.${deviceId}`, 'sd');
  const refresh = () => setRefreshKey((n) => n + 1);
  const channels = useChannels(deviceId);

  const list = (channels.data ?? []).filter((c) => hideOffline !== '1' || c.online);
  const perPage = Number(layout);
  const side = Math.sqrt(perPage);
  const pages = Math.max(1, Math.ceil(list.length / perPage));
  const shown = list.slice(page * perPage, (page + 1) * perPage);

  useEffect(() => {
    setPage(0);
    setFocus(null);
  }, [deviceId, layout, hideOffline]);

  if (devices.data && deviceList.length === 0) {
    return (
      <div className="empty-state">
        <h2>No cameras yet</h2>
        {user.role === 'Admin' ? (
          <p>
            Add your NVR or camera on the <Link to="/admin/devices">Devices</Link> page.
          </p>
        ) : (
          <p>No devices have been shared with you. Ask an administrator for access.</p>
        )}
      </div>
    );
  }

  return (
    <div className="split">
      <aside className="sidebar">
        <label>
          Device
          <select value={deviceId ?? ''} onChange={(e) => setStoredDevice(e.target.value)}>
            {deviceList.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </select>
        </label>
        <div className="channel-list">
          {channels.loading && !channels.data && <div className="muted small">Loading channels…</div>}
          {channels.error && <div className="alert error small">{channels.error}</div>}
          {(channels.data ?? []).map((c) => (
            <button
              key={c.id}
              className={`channel-item${focus?.id === c.id ? ' active' : ''}`}
              disabled={!c.online}
              onClick={() => setFocus(c)}
            >
              <span className={`dot ${c.online ? 'dot-live' : 'dot-off'}`} />
              <span className="ellipsis">
                {c.id}. {c.name}
              </span>
            </button>
          ))}
        </div>
        <label className="check small">
          <input type="checkbox" checked={hideOffline === '1'} onChange={(e) => setHideOffline(e.target.checked ? '1' : '0')} />
          Hide channels without signal
        </label>
      </aside>

      <section className="main-pane">
        {focus && deviceId ? (
          <>
            <div className="toolbar">
              <button onClick={() => setFocus(null)}>← Grid</button>
              <strong className="ellipsis">
                {focus.id}. {focus.name}
              </strong>
              <div className="spacer" />
              {focusMode === 'stills' && <RefreshButton onClick={refresh} />}
              <ViewModeToggle value={focusMode} onChange={setFocusMode} />
              <a className="button" href={snapshotUrl(deviceId, focus.id, false)} target="_blank" rel="noreferrer">
                Snapshot
              </a>
              <button onClick={() => navigate(`/playback?device=${deviceId}&channel=${focus.id}&date=${today()}`)}>Playback</button>
            </div>
            <div className="focus-view">
              {focusMode === 'stills' ? (
                <SnapshotTile key={focus.id} deviceId={deviceId} channel={focus} refreshKey={refreshKey} large />
              ) : (
                <LiveTile key={`${focus.id}-${focusMode}`} deviceId={deviceId} channel={focus} quality={focusMode as 'sd' | 'hd'} large />
              )}
            </div>
          </>
        ) : (
          <>
            <div className="toolbar">
              <div className="seg-group">
                {LAYOUTS.map((n) => (
                  <button key={n} className={perPage === n ? 'active' : ''} onClick={() => setLayout(String(n))} title={`${n} per page`}>
                    {n === 1 ? '1×1' : `${Math.sqrt(n)}×${Math.sqrt(n)}`}
                  </button>
                ))}
              </div>
              <ViewModeToggle value={gridMode} onChange={setGridMode} />
              {gridMode === 'stills' && <RefreshButton onClick={refresh} />}
              <div className="spacer" />
              {pages > 1 && (
                <div className="pager">
                  <button disabled={page === 0} onClick={() => setPage(page - 1)}>
                    ‹
                  </button>
                  <span className="muted small">
                    Page {page + 1} / {pages}
                  </span>
                  <button disabled={page >= pages - 1} onClick={() => setPage(page + 1)}>
                    ›
                  </button>
                </div>
              )}
            </div>
            {deviceId && list.length > 0 && (
              <div className="grid-fit">
                <div
                  className="grid"
                  style={{ gridTemplateColumns: `repeat(${side}, minmax(0, 1fr))`, gridTemplateRows: `repeat(${side}, minmax(0, 1fr))` }}
                >
                  {shown.map((c) =>
                    gridMode === 'stills' ? (
                      <SnapshotTile key={`${deviceId}-${c.id}`} deviceId={deviceId} channel={c} refreshKey={refreshKey} onOpen={() => c.online && setFocus(c)} />
                    ) : (
                      <LiveTile key={`${deviceId}-${c.id}`} deviceId={deviceId} channel={c} quality={gridMode as 'sd' | 'hd'} onOpen={() => c.online && setFocus(c)} />
                    ),
                  )}
                </div>
              </div>
            )}
            {channels.data && list.length === 0 && <div className="empty-state">No channels with a signal on this device.</div>}
          </>
        )}
      </section>
    </div>
  );
}
