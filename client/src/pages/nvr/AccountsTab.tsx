import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api, type Device, type NvrUser } from '../../api';
import { useAction, useResource } from '../../hooks';

interface UsersResponse {
  users: NvrUser[];
  appAccount: string;
  levels: string[];
}

const MAX_PASSWORD = 16;

/** The NVR's own login accounts (used by iVMS, the NVR's web page and this app), not Camera Viewer users. */
export function AccountsTab({ device }: { device: Device }) {
  const { data, error, reload } = useResource<UsersResponse>(`/api/devices/${device.id}/admin/users`);
  const [editing, setEditing] = useState<NvrUser | null>(null);
  const [adding, setAdding] = useState(false);
  const remove = useAction();

  const isApp = (u: NvrUser) => u.userName.toLowerCase() === data?.appAccount.toLowerCase();

  return (
    <div className="stack">
      <div className="toolbar">
        <p className="muted small grow">
          These are the recorder's own accounts, used by iVMS, the NVR's web page and Camera Viewer itself. Camera Viewer's users are managed on the{' '}
          <Link to="/admin/users">Users</Link> page.
        </p>
        <button className="primary" onClick={() => { setAdding(true); setEditing(null); }}>
          Add account
        </button>
      </div>
      {error && <div className="alert error">{error}</div>}
      {remove.error && <div className="alert error">{remove.error}</div>}

      <div className="admin-layout">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Account</th>
                <th>Level</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {(data?.users ?? []).map((u) => (
                <tr key={u.id} className={editing?.id === u.id ? 'selected' : ''}>
                  <td>
                    {u.userName}
                    {u.builtIn && <span className="badge small-badge">built-in</span>}
                    {isApp(u) && <span className="badge small-badge badge-running">used by Camera Viewer</span>}
                  </td>
                  <td>{u.level}</td>
                  <td className="actions">
                    <button onClick={() => { setEditing(u); setAdding(false); }}>Edit</button>
                    {!u.builtIn && !isApp(u) && (
                      <button
                        className="ghost"
                        disabled={remove.busy}
                        onClick={() =>
                          window.confirm(`Delete the device account "${u.userName}"? Anything logging in with it (such as an iVMS client) will stop working.`) &&
                          void remove.run(async () => {
                            await api.del(`/api/devices/${device.id}/admin/users/${u.id}`);
                            reload();
                          })
                        }
                      >
                        Delete
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        {adding && data && (
          <AddAccount
            device={device}
            onDone={() => {
              setAdding(false);
              reload();
            }}
          />
        )}
        {editing && data && (
          <EditAccount
            key={editing.id}
            device={device}
            user={editing}
            isAppAccount={isApp(editing)}
            onDone={() => {
              setEditing(null);
              reload();
            }}
          />
        )}
      </div>
    </div>
  );
}

function AddAccount({ device, onDone }: { device: Device; onDone: () => void }) {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [level, setLevel] = useState('Viewer');
  const save = useAction();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (await save.run(() => api.post(`/api/devices/${device.id}/admin/users`, { userName, password, level }))) onDone();
  };

  return (
    <form className="card" onSubmit={submit}>
      <h3>Add device account</h3>
      <label>
        User name
        <input value={userName} onChange={(e) => setUserName(e.target.value)} pattern="[A-Za-z0-9._\-]{1,32}" autoComplete="off" required />
      </label>
      <label>
        Password <span className="muted small">(up to {MAX_PASSWORD} characters)</span>
        <input type="password" value={password} maxLength={MAX_PASSWORD} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" required />
      </label>
      <label>
        Level
        <select value={level} onChange={(e) => setLevel(e.target.value)}>
          <option value="Viewer">Viewer (live view and playback)</option>
          <option value="Operator">Operator (also some configuration)</option>
        </select>
      </label>
      {save.error && <div className="alert error">{save.error}</div>}
      <div className="row">
        <button className="primary" disabled={save.busy}>
          Create
        </button>
        <button type="button" className="ghost" onClick={onDone}>
          Cancel
        </button>
      </div>
    </form>
  );
}

function EditAccount({ device, user, isAppAccount, onDone }: { device: Device; user: NvrUser; isAppAccount: boolean; onDone: () => void }) {
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [level, setLevel] = useState(user.level);
  const save = useAction();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (password !== confirm) return;
    if (
      isAppAccount &&
      password &&
      !window.confirm(
        `"${user.userName}" is the account Camera Viewer uses for ${device.name}. Its stored password will be updated automatically, ` +
          'but iVMS and anything else using it must be updated by hand. Continue?',
      )
    )
      return;
    if (await save.run(() => api.put(`/api/devices/${device.id}/admin/users/${user.id}`, { password: password || null, level }))) onDone();
  };

  return (
    <form className="card" onSubmit={submit}>
      <h3>Edit {user.userName}</h3>
      <label>
        New password <span className="muted small">(leave blank to keep; up to {MAX_PASSWORD} characters)</span>
        <input type="password" value={password} maxLength={MAX_PASSWORD} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
      </label>
      {password && (
        <label>
          Confirm new password
          <input type="password" value={confirm} maxLength={MAX_PASSWORD} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" />
          {confirm && confirm !== password && <span className="error-text small">Passwords don't match</span>}
        </label>
      )}
      <label>
        Level
        <select value={level} disabled={user.builtIn} onChange={(e) => setLevel(e.target.value)}>
          {user.builtIn && <option value={user.level}>{user.level}</option>}
          <option value="Viewer">Viewer</option>
          <option value="Operator">Operator</option>
        </select>
      </label>
      {save.error && <div className="alert error">{save.error}</div>}
      <div className="row">
        <button className="primary" disabled={save.busy || (!!password && password !== confirm)}>
          Save
        </button>
        <button type="button" className="ghost" onClick={onDone}>
          Cancel
        </button>
      </div>
    </form>
  );
}
