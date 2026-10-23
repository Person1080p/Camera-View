import { useEffect, useState, type FormEvent } from 'react';
import { api, type ChangeResult, type CloudInfo, type DdnsInfo, type Device, type IpSettings, type NetworkSettings, type PortSettings } from '../../api';
import { useAction, useResource } from '../../hooks';
import { ChangeOutcome } from './ChangeOutcome';

interface Props {
  device: Device;
  onNavigate: (tab: string) => void;
}

export function NetworkTab({ device, onNavigate }: Props) {
  const settings = useResource<NetworkSettings>(`/api/devices/${device.id}/admin/network`);
  const data = settings.data;

  if (!data) return settings.error ? <div className="alert error">{settings.error}</div> : <div className="muted">Loading network settings…</div>;

  return (
    <div className="stack narrow-col">
      {data.errors.map((e) => (
        <div key={e} className="alert error small">
          {e}
        </div>
      ))}
      {data.ip && <IpEditor device={device} initial={data.ip} mac={data.macAddress} onSaved={settings.reload} onNavigate={onNavigate} />}
      {data.ports && <PortsEditor device={device} initial={data.ports} onSaved={settings.reload} onNavigate={onNavigate} />}
      {data.remote && <RemoteAccess device={device} remote={data.remote} onSaved={settings.reload} onNavigate={onNavigate} />}
      {data.remote?.ddns && <DdnsEditor device={device} initial={data.remote.ddns} onSaved={settings.reload} onNavigate={onNavigate} />}
      {data.remote?.cloud && <CloudEditor device={device} initial={data.remote.cloud} onSaved={settings.reload} onNavigate={onNavigate} />}
    </div>
  );
}

// Friendly labels for the DDNS status codes Hikvision devices report.
const DDNS_STATUS: Record<string, string> = {
  updateSuccess: 'Updated successfully',
  connServerfail: 'Cannot reach the DDNS server',
  authFail: 'DDNS login rejected',
  updateFail: 'Update failed',
};

function DdnsEditor({ device, initial, onSaved, onNavigate }: Props & { initial: DdnsInfo; onSaved: () => void }) {
  const fromInfo = (d: DdnsInfo) => ({
    enabled: d.enabled,
    provider: d.provider,
    server: d.server,
    port: d.port,
    domain: d.domain,
    userName: d.userName,
    password: '',
  });
  const [s, setS] = useState(fromInfo(initial));
  const [result, setResult] = useState<ChangeResult | null>(null);
  const save = useAction();
  useEffect(() => setS(fromInfo(initial)), [initial]);
  const dirty = JSON.stringify(s) !== JSON.stringify(fromInfo(initial));
  const set = (key: 'provider' | 'server' | 'domain' | 'userName' | 'password') => (e: { target: { value: string } }) => setS({ ...s, [key]: e.target.value });
  const failing = initial.status && !initial.status.toLowerCase().includes('success');

  const submit = (e: FormEvent) => {
    e.preventDefault();
    void save.run(async () => {
      setResult(await api.put<ChangeResult>(`/api/devices/${device.id}/admin/network/ddns`, s));
      onSaved();
    }, 'DDNS settings saved. The status above updates after the NVR next contacts the DDNS server.');
  };

  return (
    <form className="card" onSubmit={submit}>
      <h3>
        Dynamic DNS <span className="badge">{initial.enabled ? 'On' : 'Off'}</span>
      </h3>
      <p className="muted small">
        Keeps a hostname pointing at your home internet address so the NVR can be reached from outside.
        {initial.status && (
          <>
            {' '}
            Status: <span className={failing ? 'warn-text' : ''}>{DDNS_STATUS[initial.status] ?? initial.status}</span>.
          </>
        )}
      </p>
      <label className="check">
        <input type="checkbox" checked={s.enabled} onChange={(e) => setS({ ...s, enabled: e.target.checked })} />
        Enable Dynamic DNS
      </label>
      <div className="row">
        <label>
          Provider
          <select value={s.provider} onChange={set('provider')}>
            {(initial.providers.length ? initial.providers : [initial.provider]).map((p) => (
              <option key={p} value={p}>
                {p === 'CUSTOM DDNS' ? 'Custom' : p === 'NoIpDns' ? 'No-IP' : p}
              </option>
            ))}
          </select>
        </label>
        <label>
          Server address
          <input value={s.server} onChange={set('server')} placeholder="members.dyndns.org" required={s.enabled} />
        </label>
        <label className="w-small">
          Port
          <input type="number" min={0} max={65535} value={s.port} onChange={(e) => setS({ ...s, port: Number(e.target.value) })} />
        </label>
      </div>
      <label>
        Domain name
        <input className="mono" value={s.domain} onChange={set('domain')} placeholder="mynvr.example.com" required={s.enabled} />
      </label>
      <div className="row">
        <label>
          User name
          <input value={s.userName} onChange={set('userName')} autoComplete="off" />
        </label>
        <label>
          Password <span className="muted small">(leave blank to keep the current one)</span>
          <input type="password" value={s.password} onChange={set('password')} autoComplete="new-password" />
        </label>
      </div>
      <span className="muted small">Port 0 uses the provider's default.</span>
      {save.error && <div className="alert error">{save.error}</div>}
      {save.notice && <div className="alert ok">{save.notice}</div>}
      <ChangeOutcome result={result} onReboot={() => onNavigate('maintenance')} />
      <div className="row">
        <button className="primary" disabled={!dirty || save.busy}>
          Save DDNS
        </button>
        {dirty && (
          <button type="button" className="ghost" onClick={() => setS(fromInfo(initial))}>
            Reset
          </button>
        )}
      </div>
    </form>
  );
}

function CloudEditor({ device, initial, onSaved, onNavigate }: Props & { initial: CloudInfo; onSaved: () => void }) {
  const fromInfo = (c: CloudInfo) => ({ enabled: c.enabled, server: c.server, verificationCode: '' });
  const [s, setS] = useState(fromInfo(initial));
  const [result, setResult] = useState<ChangeResult | null>(null);
  const save = useAction();
  useEffect(() => setS(fromInfo(initial)), [initial]);
  const dirty = JSON.stringify(s) !== JSON.stringify(fromInfo(initial));

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (
      s.enabled &&
      !initial.enabled &&
      !window.confirm(
        `Turn on cloud access for ${device.name}? It connects the NVR to Hikvision's cloud (EZVIZ / Hik-Connect) so it can be viewed from the phone app anywhere.`,
      )
    )
      return;
    void save.run(async () => {
      setResult(await api.put<ChangeResult>(`/api/devices/${device.id}/admin/network/cloud`, s));
      onSaved();
    }, 'Cloud settings saved.');
  };

  return (
    <form className="card" onSubmit={submit}>
      <h3>
        Cloud service (EZVIZ / Hik-Connect){' '}
        <span className={`badge ${initial.enabled ? 'badge-queued' : ''}`}>{initial.enabled ? (initial.registered ? 'Online' : 'On, not registered') : 'Off'}</span>
      </h3>
      <p className="muted small">Lets the Hik-Connect phone app reach the NVR through Hikvision's cloud, without opening router ports.</p>
      <label className="check">
        <input type="checkbox" checked={s.enabled} onChange={(e) => setS({ ...s, enabled: e.target.checked })} />
        Enable cloud access
      </label>
      <div className="row">
        <label>
          Server
          <input value={s.server} onChange={(e) => setS({ ...s, server: e.target.value })} required />
        </label>
        <label>
          Verification code <span className="muted small">(6-12 letters/digits; blank keeps the current one)</span>
          <input type="password" value={s.verificationCode} onChange={(e) => setS({ ...s, verificationCode: e.target.value })} autoComplete="new-password" />
        </label>
      </div>
      <span className="muted small">The verification code is what you enter in the phone app when adding this NVR. The NVR needs one before cloud access can work.</span>
      {save.error && <div className="alert error">{save.error}</div>}
      {save.notice && <div className="alert ok">{save.notice}</div>}
      <ChangeOutcome result={result} onReboot={() => onNavigate('maintenance')} />
      <div className="row">
        <button className="primary" disabled={!dirty || save.busy}>
          Save cloud settings
        </button>
        {dirty && (
          <button type="button" className="ghost" onClick={() => setS(fromInfo(initial))}>
            Reset
          </button>
        )}
      </div>
    </form>
  );
}

function IpEditor({ device, initial, mac, onSaved, onNavigate }: Props & { initial: IpSettings; mac: string | null; onSaved: () => void }) {
  const [s, setS] = useState(initial);
  const [result, setResult] = useState<ChangeResult | null>(null);
  const save = useAction();
  useEffect(() => setS(initial), [initial]);
  const set = (key: keyof IpSettings) => (e: { target: { value: string } }) => setS({ ...s, [key]: e.target.value });
  const dirty = JSON.stringify(s) !== JSON.stringify(initial);
  const isStatic = s.addressing === 'static';

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const moving = isStatic && s.ipAddress !== initial.ipAddress;
    const warning =
      s.addressing === 'dhcp' && initial.addressing !== 'dhcp'
        ? `Switch ${device.name} to DHCP? Your router will choose its address. If it changes, Camera Viewer, iVMS and any port forwarding will lose the NVR until you update them.`
        : moving
          ? `Move ${device.name} from ${initial.ipAddress} to ${s.ipAddress}? Camera Viewer will follow it, but iVMS and any port forwarding must be updated by hand. ` +
            'If the address is wrong for your network, you will need to fix it from the NVR\'s own monitor.'
          : `Apply the new network settings to ${device.name}?`;
    if (!window.confirm(warning)) return;
    void save.run(async () => {
      setResult(await api.put<ChangeResult>(`/api/devices/${device.id}/admin/network/ip`, s));
      onSaved();
    }, 'Network settings saved.');
  };

  return (
    <form className="card" onSubmit={submit}>
      <h3>IP address</h3>
      <label>
        Addressing
        <select value={s.addressing} onChange={set('addressing')}>
          <option value="static">Static (recommended for a recorder)</option>
          <option value="dhcp">DHCP (assigned by your router)</option>
        </select>
      </label>
      <div className="row">
        <label>
          IP address
          <input className="mono" value={s.ipAddress} onChange={set('ipAddress')} disabled={!isStatic} required={isStatic} />
        </label>
        <label>
          Subnet mask
          <input className="mono" value={s.subnetMask} onChange={set('subnetMask')} disabled={!isStatic} required={isStatic} />
        </label>
      </div>
      <label>
        Default gateway
        <input className="mono" value={s.gateway} onChange={set('gateway')} disabled={!isStatic} required={isStatic} />
      </label>
      <div className="row">
        <label>
          Primary DNS
          <input className="mono" value={s.primaryDns} onChange={set('primaryDns')} placeholder="8.8.8.8" />
        </label>
        <label>
          Secondary DNS
          <input className="mono" value={s.secondaryDns} onChange={set('secondaryDns')} placeholder="8.8.4.4" />
        </label>
      </div>
      {mac && <span className="muted small">MAC address: <span className="mono">{mac}</span></span>}
      {save.error && <div className="alert error">{save.error}</div>}
      {save.notice && <div className="alert ok">{save.notice}</div>}
      <ChangeOutcome result={result} onReboot={() => onNavigate('maintenance')} />
      <div className="row">
        <button className="primary" disabled={!dirty || save.busy}>
          {save.busy ? 'Applying…' : 'Save IP settings'}
        </button>
        {dirty && (
          <button type="button" className="ghost" onClick={() => setS(initial)}>
            Reset
          </button>
        )}
      </div>
    </form>
  );
}

function PortsEditor({ device, initial, onSaved, onNavigate }: Props & { initial: PortSettings; onSaved: () => void }) {
  const [s, setS] = useState(initial);
  const [result, setResult] = useState<ChangeResult | null>(null);
  const save = useAction();
  useEffect(() => setS(initial), [initial]);
  const num = (key: keyof PortSettings) => (e: { target: { value: string } }) => setS({ ...s, [key]: Number(e.target.value) });
  const dirty = JSON.stringify(s) !== JSON.stringify(initial);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!window.confirm(`Change ${device.name}'s service ports? iVMS clients and router port forwards using the old ports will need updating.`)) return;
    void save.run(async () => {
      setResult(await api.put<ChangeResult>(`/api/devices/${device.id}/admin/network/ports`, s));
      onSaved();
    }, 'Ports saved.');
  };

  return (
    <form className="card" onSubmit={submit}>
      <h3>Ports</h3>
      <div className="row">
        <label>
          HTTP (web / ISAPI)
          <input type="number" min={1} max={65535} value={s.httpPort} onChange={num('httpPort')} required />
        </label>
        <label>
          RTSP (video)
          <input type="number" min={1} max={65535} value={s.rtspPort} onChange={num('rtspPort')} required />
        </label>
        <label>
          Server (iVMS SDK)
          <input type="number" min={2000} max={65535} value={s.serverPort} onChange={num('serverPort')} required />
        </label>
      </div>
      <div className="row">
        <label className="check">
          <input type="checkbox" checked={s.httpsEnabled} onChange={(e) => setS({ ...s, httpsEnabled: e.target.checked })} />
          Enable HTTPS
        </label>
        <label>
          HTTPS port
          <input type="number" min={1} max={65535} value={s.httpsPort} onChange={num('httpsPort')} disabled={!s.httpsEnabled} />
        </label>
      </div>
      <span className="muted small">Camera Viewer uses the HTTP and RTSP ports and updates its own settings when they change.</span>
      {save.error && <div className="alert error">{save.error}</div>}
      {save.notice && <div className="alert ok">{save.notice}</div>}
      <ChangeOutcome result={result} onReboot={() => onNavigate('maintenance')} />
      <div className="row">
        <button className="primary" disabled={!dirty || save.busy}>
          Save ports
        </button>
        {dirty && (
          <button type="button" className="ghost" onClick={() => setS(initial)}>
            Reset
          </button>
        )}
      </div>
    </form>
  );
}

function RemoteAccess({ device, remote, onSaved, onNavigate }: Props & { remote: NonNullable<NetworkSettings['remote']>; onSaved: () => void }) {
  const [result, setResult] = useState<ChangeResult | null>(null);
  const save = useAction();

  const toggle = (body: { upnpEnabled?: boolean; sshEnabled?: boolean }, message: string, confirmText?: string) => {
    if (confirmText && !window.confirm(confirmText)) return;
    void save.run(async () => {
      setResult(await api.put<ChangeResult>(`/api/devices/${device.id}/admin/network/remote`, body));
      onSaved();
    }, message);
  };

  const mapped = remote.upnpPorts.filter((p) => p.enabled);

  return (
    <section className="card">
      <h3>Remote access</h3>

      <div className="setting-row">
        <div className="grow">
          <strong>UPnP port forwarding</strong> <span className={`badge ${remote.upnpEnabled ? 'badge-queued' : ''}`}>{remote.upnpEnabled ? 'On' : 'Off'}</span>
          <div className="muted small">
            Asks your router to open ports to the internet automatically.
            {remote.upnpEnabled && mapped.length > 0 && (
              <> Currently requesting: {mapped.map((p) => `${p.service} ${p.externalPort}`).join(', ')}.</>
            )}
          </div>
          {remote.upnpEnabled && (
            <div className="warn-text small">
              Exposing an NVR's web, SDK and RTSP ports to the internet is a common way these devices get compromised. Turn this off unless you rely
              on it; Camera Viewer doesn't need it.
            </div>
          )}
        </div>
        <button
          disabled={save.busy}
          onClick={() =>
            toggle(
              { upnpEnabled: !remote.upnpEnabled },
              `UPnP ${remote.upnpEnabled ? 'disabled' : 'enabled'}.`,
              remote.upnpEnabled
                ? 'Turn off UPnP? Remote apps that reach the NVR through ports opened by UPnP (for example iVMS from outside your home) will stop working.'
                : 'Turn on UPnP? The NVR will ask your router to expose its ports to the internet.',
            )
          }
        >
          {remote.upnpEnabled ? 'Turn off' : 'Turn on'}
        </button>
      </div>

      {remote.sshEnabled !== null && (
        <div className="setting-row">
          <div className="grow">
            <strong>SSH</strong> <span className="badge">{remote.sshEnabled ? 'On' : 'Off'}</span>
            <div className="muted small">Maintenance shell access to the recorder. Leave off unless a technician needs it.</div>
          </div>
          <button disabled={save.busy} onClick={() => toggle({ sshEnabled: !remote.sshEnabled }, `SSH ${remote.sshEnabled ? 'disabled' : 'enabled'}.`)}>
            {remote.sshEnabled ? 'Turn off' : 'Turn on'}
          </button>
        </div>
      )}

      {save.error && <div className="alert error">{save.error}</div>}
      {save.notice && <div className="alert ok">{save.notice}</div>}
      <ChangeOutcome result={result} onReboot={() => onNavigate('maintenance')} />
    </section>
  );
}
