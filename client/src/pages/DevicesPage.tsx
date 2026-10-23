import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api, errorMessage, type Device, type DeviceInfo } from '../api';
import { useDevices } from '../hooks';

interface Form {
  id: number | null;
  name: string;
  host: string;
  httpPort: string;
  rtspPort: string;
  username: string;
  password: string;
  playbackLimit: string;
}

const blank: Form = { id: null, name: '', host: '', httpPort: '80', rtspPort: '554', username: 'admin', password: '', playbackLimit: '1' };

export function DevicesPage() {
  const devices = useDevices();
  const [form, setForm] = useState<Form | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const edit = (d: Device) => {
    setForm({
      id: d.id,
      name: d.name,
      host: d.host ?? '',
      httpPort: String(d.httpPort ?? 80),
      rtspPort: String(d.rtspPort ?? 554),
      username: d.username ?? '',
      password: '',
      playbackLimit: String(d.playbackLimit),
    });
    setError(null);
    setTestResult(null);
  };

  const field = (key: keyof Form) => ({
    value: String(form?.[key] ?? ''),
    onChange: (e: { target: { value: string } }) => setForm((f) => (f ? { ...f, [key]: e.target.value } : f)),
  });

  const test = async () => {
    if (!form) return;
    setBusy(true);
    setError(null);
    setTestResult(null);
    try {
      const r = await api.post<{ info: DeviceInfo; channelCount: number }>('/api/devices/test', {
        id: form.id,
        host: form.host,
        httpPort: Number(form.httpPort),
        username: form.username,
        password: form.password || null,
      });
      setTestResult(`Connected: ${r.info.model || r.info.name} (firmware ${r.info.firmware}), ${r.channelCount} channel${r.channelCount === 1 ? '' : 's'}.`);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const save = async (e: FormEvent) => {
    e.preventDefault();
    if (!form) return;
    setBusy(true);
    setError(null);
    const body = {
      name: form.name,
      host: form.host,
      httpPort: Number(form.httpPort),
      rtspPort: Number(form.rtspPort),
      username: form.username,
      password: form.password || null,
      playbackLimit: Number(form.playbackLimit),
    };
    try {
      if (form.id) await api.put(`/api/devices/${form.id}`, body);
      else await api.post('/api/devices', body);
      setForm(null);
      devices.reload();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const remove = async (d: Device) => {
    if (!window.confirm(`Remove "${d.name}"? Users lose access to it. Finished exports stay downloadable.`)) return;
    try {
      await api.del(`/api/devices/${d.id}`);
      if (form?.id === d.id) setForm(null);
      devices.reload();
    } catch (e) {
      window.alert(errorMessage(e));
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <h1>Devices</h1>
        <button className="primary" onClick={() => { setForm({ ...blank }); setError(null); setTestResult(null); }}>
          Add device
        </button>
      </div>
      <p className="muted small">
        Hikvision-compatible NVRs, DVRs and IP cameras. The app talks to the device's web port (ISAPI, usually 80) and RTSP port (usually 554), not
        the iVMS SDK port 8000.
      </p>
      {devices.error && <div className="alert error">{devices.error}</div>}

      <div className="admin-layout">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Address</th>
                <th>Playback streams</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {(devices.data ?? []).map((d) => (
                <tr key={d.id} className={form?.id === d.id ? 'selected' : ''}>
                  <td>{d.name}</td>
                  <td className="mono small">
                    {d.username}@{d.host} · http {d.httpPort} · rtsp {d.rtspPort}
                  </td>
                  <td>{d.playbackLimit}</td>
                  <td className="actions">
                    <Link className="button" to={`/admin/nvr?device=${d.id}`}>
                      Manage
                    </Link>
                    <button onClick={() => edit(d)}>Edit</button>
                    <button className="ghost" onClick={() => remove(d)}>
                      Remove
                    </button>
                  </td>
                </tr>
              ))}
              {devices.data?.length === 0 && (
                <tr>
                  <td colSpan={4} className="muted">
                    No devices yet.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>

        {form && (
          <form className="card" onSubmit={save}>
            <h2>{form.id ? 'Edit device' : 'Add device'}</h2>
            <label>
              Name
              <input {...field('name')} placeholder="Home NVR" required />
            </label>
            <label>
              Host / IP address
              <input {...field('host')} placeholder="192.168.1.64" required />
            </label>
            <div className="row">
              <label>
                HTTP port
                <input type="number" min={1} max={65535} {...field('httpPort')} required />
              </label>
              <label>
                RTSP port
                <input type="number" min={1} max={65535} {...field('rtspPort')} required />
              </label>
            </div>
            <label>
              Username
              <input {...field('username')} autoComplete="off" required />
            </label>
            <label>
              Password {form.id && <span className="muted small">(leave blank to keep the current one)</span>}
              <input type="password" {...field('password')} autoComplete="new-password" required={!form.id} />
            </label>
            <label>
              Simultaneous playback streams
              <input type="number" min={1} max={16} {...field('playbackLimit')} required />
              <span className="muted small">
                How many recordings the device will stream at once (playback + exports). Many NVRs allow only 1 and refuse more with "Not Enough
                Bandwidth".
              </span>
            </label>
            {error && <div className="alert error">{error}</div>}
            {testResult && <div className="alert ok">{testResult}</div>}
            <div className="row">
              <button className="primary" disabled={busy}>
                Save
              </button>
              <button type="button" onClick={test} disabled={busy || !form.host}>
                Test connection
              </button>
              <button type="button" className="ghost" onClick={() => setForm(null)}>
                Cancel
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
