import { useEffect } from 'react';
import type { Device, NvrStatus } from '../../api';
import { useResource } from '../../hooks';
import { clockOf, formatDrift, formatUptime, gigabytes, offsetOf } from './format';

export function StatusTab({ device, onNavigate }: { device: Device; onNavigate: (tab: string) => void }) {
  const { data, error, loading, reload } = useResource<NvrStatus>(`/api/devices/${device.id}/admin/status`);

  useEffect(() => {
    const timer = setInterval(reload, 30000);
    return () => clearInterval(timer);
  }, [reload]);

  if (!data) return <div className="muted">{error ? <div className="alert error">{error}</div> : 'Loading status…'}</div>;

  const { info, system, clock, network, disks, channels } = data;
  const memTotal = (system?.memoryUsedMb ?? 0) + (system?.memoryAvailableMb ?? 0);
  const memPct = memTotal ? ((system?.memoryUsedMb ?? 0) / memTotal) * 100 : 0;
  const clockOff = clock && Math.abs(clock.driftSeconds) >= 60;
  const zoneDiffers = clock && offsetOf(clock.deviceTime) !== offsetOf(clock.serverTime);
  const online = channels?.filter((c) => c.online).length ?? 0;

  return (
    <div className="stack">
      <div className="toolbar">
        <span className="muted small">Refreshes every 30 seconds.</span>
        <div className="spacer" />
        <button onClick={reload} disabled={loading}>
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>
      {data.errors.map((e) => (
        <div key={e} className="alert error small">
          {e}
        </div>
      ))}
      {(clockOff || zoneDiffers) && clock && (
        <div className="alert warn">
          {clockOff && <>The NVR clock is {formatDrift(clock.driftSeconds)} this server, so recording timestamps are off by that much. </>}
          {zoneDiffers && (
            <>
              Its time zone is UTC{offsetOf(clock.deviceTime)} but this server is on UTC{offsetOf(clock.serverTime)} (daylight saving may not be set).{' '}
            </>
          )}
          <button className="link" onClick={() => onNavigate('maintenance')}>
            Fix in Maintenance
          </button>
        </div>
      )}

      <div className="cards">
        <section className="card">
          <h3>Device</h3>
          <dl className="kv">
            <dt>Model</dt>
            <dd>{info?.model ?? '—'}</dd>
            <dt>Name</dt>
            <dd>{info?.name ?? '—'}</dd>
            <dt>Firmware</dt>
            <dd>{info?.firmware ?? '—'}</dd>
            <dt>Serial</dt>
            <dd className="mono small break">{info?.serialNumber ?? '—'}</dd>
          </dl>
        </section>
        <section className="card">
          <h3>System</h3>
          <dl className="kv">
            <dt>Uptime</dt>
            <dd>{system ? formatUptime(system.uptimeSeconds) : '—'}</dd>
            <dt>CPU</dt>
            <dd>{system?.cpuPercent != null ? `${system.cpuPercent}%` : '—'}</dd>
            <dt>Memory</dt>
            <dd>
              {memTotal ? (
                <>
                  {Math.round(system!.memoryUsedMb!)} / {Math.round(memTotal)} MB
                  <div className="meter">
                    <div style={{ width: `${memPct}%` }} />
                  </div>
                </>
              ) : (
                '—'
              )}
            </dd>
            <dt>Cameras</dt>
            <dd>
              {online} online / {channels?.length ?? 0} channels
            </dd>
          </dl>
        </section>
        <section className="card">
          <h3>Clock</h3>
          <dl className="kv">
            <dt>NVR time</dt>
            <dd className="mono">{clock ? clockOf(clock.deviceTime) : '—'}</dd>
            <dt>This server</dt>
            <dd className="mono">{clock ? clockOf(clock.serverTime) : '—'}</dd>
            <dt>Difference</dt>
            <dd className={clockOff ? 'warn-text' : ''}>{clock ? (Math.abs(clock.driftSeconds) < 2 ? 'In sync' : formatDrift(clock.driftSeconds) + ' server') : '—'}</dd>
            <dt>Mode</dt>
            <dd>{clock ? (clock.timeMode === 'NTP' ? `NTP (${clock.ntp?.host || 'no server'})` : 'Manual') : '—'}</dd>
          </dl>
        </section>
        <section className="card">
          <h3>Network</h3>
          <dl className="kv">
            <dt>IP address</dt>
            <dd className="mono">
              {network?.ipAddress ?? '—'} <span className="muted small">{network?.addressing}</span>
            </dd>
            <dt>Subnet / gateway</dt>
            <dd className="mono">
              {network?.subnetMask ?? '—'} · {network?.gateway ?? '—'}
            </dd>
            <dt>DNS</dt>
            <dd className="mono">{[network?.primaryDns, network?.secondaryDns].filter(Boolean).join(', ') || '—'}</dd>
            <dt>MAC</dt>
            <dd className="mono">{network?.macAddress ?? '—'}</dd>
          </dl>
        </section>
      </div>

      <section>
        <h3>Storage</h3>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Disk</th>
                <th>Type</th>
                <th>Capacity</th>
                <th>Free</th>
                <th>Status</th>
                <th>Health (SMART)</th>
              </tr>
            </thead>
            <tbody>
              {(disks ?? []).map((d) => (
                <tr key={d.id}>
                  <td>
                    {d.id} <span className="muted small">{d.name}</span>
                  </td>
                  <td>{d.type}</td>
                  <td>{gigabytes(d.capacityMb)}</td>
                  <td>{gigabytes(d.freeMb)}</td>
                  <td>
                    <span className={`badge ${d.status === 'ok' ? 'badge-completed' : 'badge-failed'}`}>{d.status}</span>
                    <span className="muted small"> {d.property}</span>
                  </td>
                  <td className="small">
                    {d.health ? (
                      <>
                        <span className={d.health === 'functional' || d.health === 'ok' ? '' : 'warn-text'}>{d.health}</span>
                        {d.temperatureC !== null && <span className={d.temperatureC >= 50 ? 'warn-text' : 'muted'}> · {d.temperatureC} °C</span>}
                        {d.powerOnDays !== null && <span className="muted"> · {(d.powerOnDays / 365).toFixed(1)} years powered on</span>}
                      </>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </td>
                </tr>
              ))}
              {disks?.length === 0 && (
                <tr>
                  <td colSpan={6} className="muted">
                    No disks reported.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <p className="muted small">0 GB free is normal on a recorder that has filled up: it overwrites the oldest recordings.</p>
      </section>

      <section>
        <h3>Cameras</h3>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>#</th>
                <th>Name</th>
                <th>Camera address</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {(channels ?? []).map((c) => (
                <tr key={c.id}>
                  <td>{c.id}</td>
                  <td>{c.name}</td>
                  <td className="mono small">
                    {c.ipAddress ?? '—'}
                    {c.port ? `:${c.port}` : ''}
                  </td>
                  <td>
                    <span className={`dot ${c.online ? 'dot-live' : 'dot-off'}`} /> {c.online ? 'Online' : 'Offline'}
                    {c.detail && c.detail !== 'connect' && <span className="muted small"> ({c.detail})</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
