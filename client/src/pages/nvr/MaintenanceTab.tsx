import { useEffect, useState, type FormEvent } from 'react';
import { api, type ClockStatus, type Device } from '../../api';
import { useAction, useResource } from '../../hooks';
import { clockOf, formatDrift, offsetOf } from './format';

// POSIX-style zones as Hikvision devices store them (note the inverted sign: "+5" means UTC-5).
const ZONES: [string, string][] = [
  ['US Eastern (with daylight saving)', 'EST+5:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00'],
  ['US Central (with daylight saving)', 'CST+6:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00'],
  ['US Mountain (with daylight saving)', 'MST+7:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00'],
  ['US Arizona (no daylight saving)', 'MST+7:00:00'],
  ['US Pacific (with daylight saving)', 'PST+8:00:00DST01:00:00,M3.2.0/02:00:00,M11.1.0/02:00:00'],
  ['UTC-5, no daylight saving', 'CST+5:00:00'],
  ['UTC', 'UTC+0:00:00'],
];

export function MaintenanceTab({ device }: { device: Device }) {
  const status = useResource<{ clock: ClockStatus | null }>(`/api/devices/${device.id}/admin/status`);
  const [clock, setClock] = useState<ClockStatus | null>(null);
  const sync = useAction();
  const save = useAction();
  const reboot = useAction();

  const [mode, setMode] = useState('manual');
  const [zone, setZone] = useState('');
  const [ntpHost, setNtpHost] = useState('');
  const [ntpPort, setNtpPort] = useState('123');
  const [ntpInterval, setNtpInterval] = useState('60');
  const [confirmName, setConfirmName] = useState('');

  const applyClock = (c: ClockStatus) => {
    setClock(c);
    setMode(c.timeMode === 'NTP' ? 'NTP' : 'manual');
    setZone(c.timeZone);
    if (c.ntp) {
      setNtpHost(c.ntp.host);
      setNtpPort(String(c.ntp.port));
      setNtpInterval(String(c.ntp.intervalMinutes));
    }
  };

  useEffect(() => {
    if (status.data?.clock) applyClock(status.data.clock);
  }, [status.data]);

  const doSync = () => {
    if (!clock) return;
    const msg =
      `Set ${device.name}'s clock to this server's time?\n\n` +
      `The NVR is currently ${formatDrift(clock.driftSeconds)} this server. Recordings around the change will show a ` +
      `${clock.driftSeconds < 0 ? 'gap' : 'short overlap'} of that length.`;
    if (!window.confirm(msg)) return;
    void sync.run(async () => applyClock(await api.post<ClockStatus>(`/api/devices/${device.id}/admin/clock/sync`)), 'Clock synchronised.');
  };

  const doSave = (e: FormEvent) => {
    e.preventDefault();
    const body = {
      timeMode: mode,
      timeZone: zone,
      ntp: ntpHost.trim() ? { host: ntpHost.trim(), port: Number(ntpPort), intervalMinutes: Number(ntpInterval) } : null,
    };
    void save.run(async () => applyClock(await api.put<ClockStatus>(`/api/devices/${device.id}/admin/clock`, body)), 'Time settings saved. Check the NVR time above.');
  };

  const doReboot = () => {
    if (!window.confirm(`Reboot ${device.name} now? Recording and viewing stop for about 2 minutes.`)) return;
    void reboot.run(async () => {
      await api.post(`/api/devices/${device.id}/admin/reboot`);
      setConfirmName('');
    }, 'Reboot started. The device will be unreachable for a couple of minutes.');
  };

  const presetValue = ZONES.some(([, z]) => z === zone) ? zone : '__custom';

  return (
    <div className="stack narrow-col">
      <section className="card">
        <h3>Clock</h3>
        {status.error && <div className="alert error">{status.error}</div>}
        {clock && (
          <dl className="kv">
            <dt>NVR time</dt>
            <dd className="mono">
              {clockOf(clock.deviceTime)} <span className="muted small">UTC{offsetOf(clock.deviceTime)}</span>
            </dd>
            <dt>This server</dt>
            <dd className="mono">
              {clockOf(clock.serverTime)} <span className="muted small">UTC{offsetOf(clock.serverTime)}</span>
            </dd>
            <dt>Difference</dt>
            <dd className={Math.abs(clock.driftSeconds) >= 60 ? 'warn-text' : ''}>
              {Math.abs(clock.driftSeconds) < 2 ? 'In sync' : `NVR is ${formatDrift(clock.driftSeconds)} this server`}
            </dd>
          </dl>
        )}
        <div className="row">
          <button className="primary" onClick={doSync} disabled={!clock || sync.busy}>
            {sync.busy ? 'Syncing…' : 'Sync NVR clock to this server'}
          </button>
          <button onClick={status.reload}>Refresh</button>
        </div>
        <p className="muted small">Sets the NVR's wall-clock time to match this server and keeps the NVR's time zone setting. Switches the NVR to manual time.</p>
        {sync.error && <div className="alert error">{sync.error}</div>}
        {sync.notice && <div className="alert ok">{sync.notice}</div>}
      </section>

      <form className="card" onSubmit={doSave}>
        <h3>Time settings</h3>
        <label>
          Time zone
          <select value={presetValue} onChange={(e) => e.target.value !== '__custom' && setZone(e.target.value)}>
            {ZONES.map(([label, z]) => (
              <option key={z} value={z}>
                {label}
              </option>
            ))}
            <option value="__custom">Custom (edit below)</option>
          </select>
        </label>
        <label>
          Device time zone string
          <input className="mono" value={zone} onChange={(e) => setZone(e.target.value)} required />
          <span className="muted small">POSIX format as stored on the device. The sign is inverted: "+5:00:00" means UTC−5.</span>
        </label>
        <label>
          Time source
          <select value={mode} onChange={(e) => setMode(e.target.value)}>
            <option value="manual">Manual (set by the sync button)</option>
            <option value="NTP">NTP server (keeps itself in sync)</option>
          </select>
        </label>
        <div className="row">
          <label>
            NTP server
            <input value={ntpHost} onChange={(e) => setNtpHost(e.target.value)} placeholder="pool.ntp.org" required={mode === 'NTP'} />
          </label>
          <label className="w-small">
            Port
            <input type="number" min={1} max={65535} value={ntpPort} onChange={(e) => setNtpPort(e.target.value)} />
          </label>
          <label className="w-small">
            Every (min)
            <input type="number" min={1} max={10080} value={ntpInterval} onChange={(e) => setNtpInterval(e.target.value)} />
          </label>
        </div>
        <p className="muted small">NTP needs the NVR to reach the internet (its DNS and gateway must be correct; see Status → Network).</p>
        {save.error && <div className="alert error">{save.error}</div>}
        {save.notice && <div className="alert ok">{save.notice}</div>}
        <div>
          <button className="primary" disabled={save.busy || !clock}>
            {save.busy ? 'Saving…' : 'Save time settings'}
          </button>
        </div>
      </form>

      <section className="card danger-zone">
        <h3>Restart</h3>
        <p className="muted small">
          Reboots the recorder. Recording, live view and playback stop for about two minutes. To restart a single camera, use the Cameras tab.
        </p>
        <label>
          Type <strong>{device.name}</strong> to confirm
          <input value={confirmName} onChange={(e) => setConfirmName(e.target.value)} />
        </label>
        {reboot.error && <div className="alert error">{reboot.error}</div>}
        {reboot.notice && <div className="alert ok">{reboot.notice}</div>}
        <div>
          <button className="danger" disabled={confirmName !== device.name || reboot.busy} onClick={doReboot}>
            Reboot NVR
          </button>
        </div>
      </section>
    </div>
  );
}
