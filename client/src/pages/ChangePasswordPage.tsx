import { useState, type FormEvent } from 'react';
import { api, errorMessage, type User } from '../api';
import { useAuth } from '../auth';

/** Shown on its own (forced) after first login or an admin reset, and as the Account page otherwise. */
export function ChangePasswordPage({ forced = false }: { forced?: boolean }) {
  const { user, setUser, logout } = useAuth();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setDone(false);
    if (next !== confirm) {
      setError('The new passwords do not match');
      return;
    }
    setBusy(true);
    try {
      const updated = await api.post<User>('/api/auth/change-password', { currentPassword: current, newPassword: next });
      setCurrent('');
      setNext('');
      setConfirm('');
      setDone(true);
      setUser(updated);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const form = (
    <form className={`card ${forced ? 'auth-card' : 'narrow'}`} onSubmit={submit}>
      <h1>{forced ? 'Choose a new password' : 'Change password'}</h1>
      {forced && <p className="muted">Signed in as {user?.username}. You need to set your own password before continuing.</p>}
      <label>
        Current password
        <input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" required />
      </label>
      <label>
        New password <span className="muted small">(at least 8 characters)</span>
        <input type="password" value={next} onChange={(e) => setNext(e.target.value)} autoComplete="new-password" minLength={8} required />
      </label>
      <label>
        Confirm new password
        <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" required />
      </label>
      {error && <div className="alert error">{error}</div>}
      {done && !forced && <div className="alert ok">Password changed. Other signed-in sessions have been signed out.</div>}
      <div className="row">
        <button className="primary" disabled={busy}>
          {busy ? 'Saving…' : 'Change password'}
        </button>
        {forced && (
          <button type="button" className="ghost" onClick={logout}>
            Sign out
          </button>
        )}
      </div>
    </form>
  );

  return forced ? <div className="auth-screen">{form}</div> : form;
}
