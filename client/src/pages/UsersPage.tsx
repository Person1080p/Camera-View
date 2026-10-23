import { useState, type FormEvent } from 'react';
import { api, errorMessage, type Role, type User } from '../api';
import { useUser } from '../auth';
import { useDevices, useResource } from '../hooks';

interface Form {
  id: number | null;
  username: string;
  password: string;
  role: Role;
  deviceIds: number[];
}

export function UsersPage() {
  const me = useUser();
  const users = useResource<User[]>('/api/users');
  const devices = useDevices();
  const [form, setForm] = useState<Form | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const deviceName = (id: number) => devices.data?.find((d) => d.id === id)?.name ?? `#${id}`;

  const toggleDevice = (id: number) =>
    setForm((f) => f && { ...f, deviceIds: f.deviceIds.includes(id) ? f.deviceIds.filter((x) => x !== id) : [...f.deviceIds, id] });

  const save = async (e: FormEvent) => {
    e.preventDefault();
    if (!form) return;
    setBusy(true);
    setError(null);
    try {
      if (form.id) {
        await api.put(`/api/users/${form.id}`, { role: form.role, deviceIds: form.deviceIds, password: form.password || null });
      } else {
        await api.post('/api/users', { username: form.username, password: form.password, role: form.role, deviceIds: form.deviceIds });
      }
      setForm(null);
      users.reload();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const remove = async (u: User) => {
    if (!window.confirm(`Delete user "${u.username}"? They will be signed out immediately.`)) return;
    try {
      await api.del(`/api/users/${u.id}`);
      users.reload();
    } catch (e) {
      window.alert(errorMessage(e));
    }
  };

  return (
    <div className="page">
      <div className="page-head">
        <h1>Users</h1>
        <button className="primary" onClick={() => { setForm({ id: null, username: '', password: '', role: 'Viewer', deviceIds: [] }); setError(null); }}>
          Add user
        </button>
      </div>
      <p className="muted small">Admins manage devices and users and can see every camera. Viewers only see the devices you tick for them.</p>
      {users.error && <div className="alert error">{users.error}</div>}

      <div className="admin-layout">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Username</th>
                <th>Role</th>
                <th>Devices</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {(users.data ?? []).map((u) => (
                <tr key={u.id} className={form?.id === u.id ? 'selected' : ''}>
                  <td>
                    {u.username}
                    {u.id === me.id && <span className="muted small"> (you)</span>}
                    {u.mustChangePassword && <div className="muted small">Must set a new password at next sign-in</div>}
                  </td>
                  <td>{u.role}</td>
                  <td className="small">{u.role === 'Admin' ? <span className="muted">All devices</span> : u.deviceIds.map(deviceName).join(', ') || <span className="muted">None</span>}</td>
                  <td className="actions">
                    <button onClick={() => { setForm({ id: u.id, username: u.username, password: '', role: u.role, deviceIds: u.deviceIds }); setError(null); }}>
                      Edit
                    </button>
                    {u.id !== me.id && (
                      <button className="ghost" onClick={() => remove(u)}>
                        Delete
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {form && (
          <form className="card" onSubmit={save}>
            <h2>{form.id ? `Edit ${form.username}` : 'Add user'}</h2>
            {!form.id && (
              <label>
                Username
                <input value={form.username} onChange={(e) => setForm({ ...form, username: e.target.value })} autoComplete="off" required />
              </label>
            )}
            <label>
              {form.id ? 'Reset password' : 'Temporary password'} <span className="muted small">(at least 8 characters{form.id ? '; leave blank to keep' : ''})</span>
              <input
                type="password"
                value={form.password}
                onChange={(e) => setForm({ ...form, password: e.target.value })}
                autoComplete="new-password"
                minLength={8}
                required={!form.id}
              />
              <span className="muted small">The user must choose their own password after signing in with it.</span>
            </label>
            <label>
              Role
              <select value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value as Role })} disabled={form.id === me.id}>
                <option value="Viewer">Viewer</option>
                <option value="Admin">Admin</option>
              </select>
            </label>
            <fieldset disabled={form.role === 'Admin'}>
              <legend>Device access {form.role === 'Admin' && <span className="muted small">(admins see all devices)</span>}</legend>
              {(devices.data ?? []).map((d) => (
                <label key={d.id} className="check">
                  <input type="checkbox" checked={form.role === 'Admin' || form.deviceIds.includes(d.id)} onChange={() => toggleDevice(d.id)} />
                  {d.name}
                </label>
              ))}
              {devices.data?.length === 0 && <span className="muted small">No devices have been added.</span>}
            </fieldset>
            {error && <div className="alert error">{error}</div>}
            <div className="row">
              <button className="primary" disabled={busy}>
                Save
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
