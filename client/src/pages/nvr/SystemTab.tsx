import { useEffect, useState, type FormEvent } from 'react';
import { api, type ChangeResult, type Device, type SystemSettings } from '../../api';
import { useAction, useResource } from '../../hooks';
import { ChangeOutcome } from './ChangeOutcome';

export function SystemTab({ device, onNavigate }: { device: Device; onNavigate: (tab: string) => void }) {
  const settings = useResource<SystemSettings>(`/api/devices/${device.id}/admin/system`);
  const [s, setS] = useState<SystemSettings | null>(null);
  const [result, setResult] = useState<ChangeResult | null>(null);
  const save = useAction();
  useEffect(() => setS(settings.data), [settings.data]);

  if (!s) return settings.error ? <div className="alert error">{settings.error}</div> : <div className="muted">Loading…</div>;
  const dirty = JSON.stringify(s) !== JSON.stringify(settings.data);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (
      settings.data?.overwriteWhenFull &&
      s.overwriteWhenFull === false &&
      !window.confirm('Turn off overwriting? The disk is usually full on a recorder, so recording will stop as soon as it runs out of space.')
    )
      return;
    void save.run(async () => {
      setResult(await api.put<ChangeResult>(`/api/devices/${device.id}/admin/system`, s));
      settings.reload();
    }, 'System settings saved.');
  };

  return (
    <form className="card narrow-col" onSubmit={submit}>
      <h3>System</h3>
      <label>
        Device name
        <input value={s.deviceName} maxLength={32} onChange={(e) => setS({ ...s, deviceName: e.target.value })} required />
        <span className="muted small">Shown in iVMS and on the NVR's own screen. The name used inside Camera Viewer is set on the Devices page.</span>
      </label>
      {s.overwriteWhenFull !== null && (
        <label className="check">
          <input type="checkbox" checked={s.overwriteWhenFull} onChange={(e) => setS({ ...s, overwriteWhenFull: e.target.checked })} />
          Overwrite the oldest recordings when the disk is full
        </label>
      )}
      {save.error && <div className="alert error">{save.error}</div>}
      {save.notice && <div className="alert ok">{save.notice}</div>}
      <ChangeOutcome result={result} onReboot={() => onNavigate('maintenance')} />
      <div>
        <button className="primary" disabled={!dirty || save.busy}>
          Save
        </button>
      </div>
    </form>
  );
}
