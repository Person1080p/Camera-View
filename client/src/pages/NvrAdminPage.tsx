import { Link, useSearchParams } from 'react-router-dom';
import { useDevices } from '../hooks';
import { AccountsTab } from './nvr/AccountsTab';
import { CamerasTab } from './nvr/CamerasTab';
import { LogsTab } from './nvr/LogsTab';
import { MaintenanceTab } from './nvr/MaintenanceTab';
import { NetworkTab } from './nvr/NetworkTab';
import { StatusTab } from './nvr/StatusTab';
import { SystemTab } from './nvr/SystemTab';

const TABS = [
  ['status', 'Status'],
  ['network', 'Network'],
  ['system', 'System'],
  ['maintenance', 'Maintenance'],
  ['cameras', 'Cameras'],
  ['logs', 'Logs'],
  ['accounts', 'Device accounts'],
] as const;

type Tab = (typeof TABS)[number][0];

/** Administration of the NVRs/cameras themselves (not this app). */
export function NvrAdminPage() {
  const [params, setParams] = useSearchParams();
  const devices = useDevices();
  const list = devices.data ?? [];
  const device = list.find((d) => String(d.id) === params.get('device')) ?? list[0] ?? null;
  const tab = (TABS.find(([key]) => key === params.get('tab'))?.[0] ?? 'status') as Tab;

  const go = (patch: Record<string, string>) => setParams({ ...Object.fromEntries(params), ...patch }, { replace: true });

  if (devices.data && list.length === 0) {
    return (
      <div className="empty-state">
        No devices yet. Add one on the <Link to="/admin/devices">Devices</Link> page.
      </div>
    );
  }

  return (
    <div className="page wide">
      <div className="page-head">
        <h1>NVR administration</h1>
        <select className="auto" value={device?.id ?? ''} onChange={(e) => go({ device: e.target.value })}>
          {list.map((d) => (
            <option key={d.id} value={d.id}>
              {d.name} ({d.host})
            </option>
          ))}
        </select>
      </div>
      <nav className="tabs">
        {TABS.map(([key, label]) => (
          <button key={key} className={key === tab ? 'active' : ''} onClick={() => go({ tab: key })}>
            {label}
          </button>
        ))}
      </nav>
      {device && (
        <div key={`${device.id}-${tab}`}>
          {tab === 'status' && <StatusTab device={device} onNavigate={(t) => go({ tab: t })} />}
          {tab === 'network' && <NetworkTab device={device} onNavigate={(t) => go({ tab: t })} />}
          {tab === 'system' && <SystemTab device={device} onNavigate={(t) => go({ tab: t })} />}
          {tab === 'maintenance' && <MaintenanceTab device={device} />}
          {tab === 'cameras' && <CamerasTab device={device} />}
          {tab === 'logs' && <LogsTab device={device} />}
          {tab === 'accounts' && <AccountsTab device={device} />}
        </div>
      )}
    </div>
  );
}
