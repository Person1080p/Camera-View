import { useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api, type TailscaleStatus } from '../api';
import { useAction, useResource } from '../hooks';

const STATE_LABEL: Record<string, [string, string]> = {
  Unavailable: ['Not installed', 'badge-failed'],
  Disabled: ['Off', ''],
  Starting: ['Starting…', 'badge-queued'],
  NeedsLogin: ['Needs sign-in', 'badge-queued'],
  NeedsMachineAuth: ['Waiting for approval', 'badge-queued'],
  Running: ['Connected', 'badge-completed'],
  Stopped: ['Stopped', 'badge-failed'],
  Error: ['Error', 'badge-failed'],
};

/** The app's own (embedded) Tailscale node, used to reach NVRs at other sites. */
export function TailscalePage() {
  const { data: s, error, reload } = useResource<TailscaleStatus>('/api/tailscale');
  const settings = useAction();
  const login = useAction();
  const other = useAction();
  const [enabled, setEnabled] = useState(false);
  const [hostname, setHostname] = useState('');
  const [authKey, setAuthKey] = useState('');

  useEffect(() => {
    if (!s) return;
    setEnabled(s.enabled);
    setHostname(s.hostname);
  }, [s?.enabled, s?.hostname]); // eslint-disable-line react-hooks/exhaustive-deps

  // Poll quickly while something is in progress (start-up, sign-in), slowly otherwise.
  const settled = !s || ['Running', 'Disabled', 'Unavailable'].includes(s.state);
  useEffect(() => {
    const timer = setInterval(reload, settled ? 15000 : 3000);
    return () => clearInterval(timer);
  }, [settled, reload]);

  if (!s) return <div className="page">{error ? <div className="alert error">{error}</div> : <span className="muted">Loading…</span>}</div>;

  const [label, badge] = STATE_LABEL[s.state] ?? [s.state, ''];
  const dirty = enabled !== s.enabled || hostname !== s.hostname;

  const saveSettings = (e: FormEvent) => {
    e.preventDefault();
    void settings.run(async () => {
      await api.put('/api/tailscale', { enabled, hostname });
      reload();
    });
  };

  return (
    <div className="page">
      <div className="page-head">
        <h1>Tailscale</h1>
        <span className={`badge ${badge}`}>{label}</span>
      </div>
      <p className="muted small narrow-col">
        Camera Viewer runs its own Tailscale node, so it can reach NVRs at other sites over your tailnet without Tailscale being installed on this
        server. Mark those NVRs <em>Connect through Tailscale</em> on the <Link to="/admin/devices">Devices</Link> page.
      </p>

      <div className="stack narrow-col">
        {s.error && <div className="alert error">{s.error}</div>}
        {error && <div className="alert error">{error}</div>}

        {s.available && (
          <form className="card" onSubmit={saveSettings}>
            <h3>Connection</h3>
            <label className="check">
              <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
              Run Camera Viewer's Tailscale node
            </label>
            <label>
              Machine name on the tailnet
              <input value={hostname} onChange={(e) => setHostname(e.target.value)} maxLength={63} required />
            </label>
            {settings.error && <div className="alert error">{settings.error}</div>}
            <div>
              <button className="primary" disabled={!dirty || settings.busy}>
                Save
              </button>
            </div>
          </form>
        )}

        {(s.state === 'NeedsLogin' || s.state === 'NeedsMachineAuth') && (
          <section className="card">
            <h3>Sign in</h3>
            {s.state === 'NeedsMachineAuth' ? (
              <p>
                Signed in, waiting for a tailnet admin to approve <strong>{s.hostname}</strong> in the{' '}
                <a href="https://login.tailscale.com/admin/machines" target="_blank" rel="noreferrer">
                  Tailscale admin console
                </a>
                .
              </p>
            ) : s.authUrl ? (
              <>
                <p>Open the sign-in page, log in to the Tailscale account that owns your sites' routers, and connect this machine.</p>
                <div>
                  <a className="button primary" href={s.authUrl} target="_blank" rel="noreferrer">
                    Open Tailscale sign-in
                  </a>
                </div>
                <span className="muted small">This page updates by itself once you've signed in.</span>
              </>
            ) : (
              <p className="muted">Getting a sign-in link…</p>
            )}
            <form
              className="row"
              onSubmit={(e) => {
                e.preventDefault();
                void login.run(async () => {
                  await api.post('/api/tailscale/login', { authKey });
                  setAuthKey('');
                  reload();
                }, 'Signing in with the auth key…');
              }}
            >
              <label>
                Or use an auth key <span className="muted small">(from the admin console → Settings → Keys)</span>
                <input type="password" value={authKey} onChange={(e) => setAuthKey(e.target.value)} placeholder="tskey-auth-…" autoComplete="off" />
              </label>
              <button disabled={!authKey || login.busy}>Use key</button>
            </form>
            {login.error && <div className="alert error">{login.error}</div>}
            {login.notice && <div className="alert ok">{login.notice}</div>}
          </section>
        )}

        {s.state === 'Running' && (
          <section className="card">
            <h3>This node</h3>
            <dl className="kv">
              <dt>Name</dt>
              <dd className="mono">{s.dnsName ?? s.hostname}</dd>
              <dt>Addresses</dt>
              <dd className="mono small">{s.addresses.join(', ') || '—'}</dd>
              <dt>Tailnet</dt>
              <dd>{s.tailnet ?? '—'}</dd>
              <dt>Subnet routes</dt>
              <dd>
                {s.acceptRoutes ? 'Accepted' : <span className="warn-text">Not accepted: NVRs behind site routers are unreachable</span>}{' '}
                <button
                  className="link"
                  disabled={other.busy}
                  onClick={() =>
                    void other.run(async () => {
                      await api.put('/api/tailscale/routes', { acceptRoutes: !s.acceptRoutes });
                      reload();
                    })
                  }
                >
                  {s.acceptRoutes ? 'Stop accepting' : 'Accept routes'}
                </button>
              </dd>
            </dl>
            <div>
              <button
                className="ghost"
                disabled={other.busy}
                onClick={() =>
                  window.confirm('Sign this node out of Tailscale? NVRs set to connect through Tailscale become unreachable until it signs in again.') &&
                  void other.run(async () => {
                    await api.post('/api/tailscale/logout');
                    reload();
                  })
                }
              >
                Sign out
              </button>
            </div>
            {other.error && <div className="alert error">{other.error}</div>}
          </section>
        )}

        {s.health.length > 0 && (
          <div className="alert warn small">
            {s.health.map((h) => (
              <div key={h}>{h}</div>
            ))}
          </div>
        )}
      </div>

      {s.state === 'Running' && (
        <section>
          <h3>Machines on the tailnet</h3>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Machine</th>
                  <th>Tailscale address</th>
                  <th>Shares subnets</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                {s.peers.map((p) => (
                  <tr key={p.dnsName ?? p.name}>
                    <td>
                      {p.name}
                      {p.os && <span className="muted small"> · {p.os}</span>}
                    </td>
                    <td className="mono small">{p.addresses[0] ?? '—'}</td>
                    <td className="mono small">{p.routes.filter((r) => !r.startsWith('100.') && !r.includes(':')).join(', ') || <span className="muted">—</span>}</td>
                    <td>
                      <span className={`dot ${p.online ? 'dot-live' : 'dot-off'}`} /> {p.online ? 'Online' : 'Offline'}
                    </td>
                  </tr>
                ))}
                {s.peers.length === 0 && (
                  <tr>
                    <td colSpan={4} className="muted">
                      No other machines visible.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          <p className="muted small">
            Add a site's NVR by its address on that site's LAN (inside a shared subnet) and tick <em>Connect through Tailscale</em>.
          </p>
        </section>
      )}
    </div>
  );
}
