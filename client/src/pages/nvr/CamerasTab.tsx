import { useEffect, useState } from 'react';
import { api, snapshotUrl, type CameraSettings, type Device, type OverlaySettings, type StreamConfig, type StreamSettings } from '../../api';
import { useAction, useChannels, useResource } from '../../hooks';
import { frameRateLabel } from './format';

export function CamerasTab({ device }: { device: Device }) {
  const channels = useChannels(device.id);
  const [selected, setSelected] = useState<number | null>(null);
  const channel = selected ?? channels.data?.find((c) => c.online)?.id ?? channels.data?.[0]?.id ?? null;
  const settings = useResource<CameraSettings>(channel ? `/api/devices/${device.id}/admin/channels/${channel}` : null);

  return (
    <div className="admin-split">
      <aside className="channel-list">
        {channels.error && <div className="alert error small">{channels.error}</div>}
        {(channels.data ?? []).map((c) => (
          <button key={c.id} className={`channel-item${c.id === channel ? ' active' : ''}`} onClick={() => setSelected(c.id)}>
            <span className={`dot ${c.online ? 'dot-live' : 'dot-off'}`} />
            <span className="ellipsis">
              {c.id}. {c.name}
            </span>
          </button>
        ))}
      </aside>
      <div className="stack">
        {settings.loading && !settings.data && <div className="muted">Loading camera settings…</div>}
        {settings.error && <div className="alert error">{settings.error}</div>}
        {settings.data && channel && (
          <CameraEditor
            key={channel}
            device={device}
            settings={settings.data}
            online={channels.data?.find((c) => c.id === channel)?.online ?? false}
            onChanged={() => {
              settings.reload();
              channels.reload();
            }}
          />
        )}
      </div>
    </div>
  );
}

function CameraEditor({ device, settings, online, onChanged }: { device: Device; settings: CameraSettings; online: boolean; onChanged: () => void }) {
  const base = `/api/devices/${device.id}/admin/channels/${settings.channel}`;
  const [name, setName] = useState(settings.name);
  const rename = useAction();
  const restart = useAction();

  return (
    <>
      <section className="card">
        <div className="camera-head">
          {online && <img className="thumb" src={snapshotUrl(device.id, settings.channel)} alt="" />}
          <div className="stack grow">
            <h3>
              Camera {settings.channel}
              {settings.ipAddress && <span className="muted small mono"> · {settings.ipAddress}</span>}
            </h3>
            <form
              className="row"
              onSubmit={(e) => {
                e.preventDefault();
                void rename.run(async () => {
                  await api.put(`${base}/name`, { name });
                  onChanged();
                }, 'Name saved.');
              }}
            >
              <label>
                Name
                <input value={name} maxLength={32} onChange={(e) => setName(e.target.value)} required />
              </label>
              <button className="primary" disabled={rename.busy || name.trim() === settings.name}>
                Rename
              </button>
              <button
                type="button"
                disabled={restart.busy || !online}
                onClick={() =>
                  window.confirm(`Restart camera ${settings.channel} (${settings.name})? It will be offline for about a minute.`) &&
                  void restart.run(() => api.post(`${base}/reboot`), 'Restart command sent.')
                }
              >
                Restart camera
              </button>
            </form>
            {!online && <span className="muted small">This camera is offline, so changes may not be applied until it reconnects.</span>}
            {rename.error && <div className="alert error">{rename.error}</div>}
            {rename.notice && <div className="alert ok">{rename.notice}</div>}
            {restart.error && <div className="alert error">{restart.error}</div>}
            {restart.notice && <div className="alert ok">{restart.notice}</div>}
          </div>
        </div>
      </section>

      {settings.errors.map((e) => (
        <div key={e} className="alert error small">
          {e}
        </div>
      ))}

      {settings.overlay && settings.overlayOptions && (
        <OverlayEditor url={`${base}/overlay`} initial={settings.overlay} options={settings.overlayOptions} />
      )}

      <div className="cards two">
        {settings.main && (
          <StreamEditor
            title="Main stream"
            hint="Used for recording and HD live view."
            url={`${base}/streams/main`}
            config={settings.main}
            confirm="Changing the main stream changes recording quality and disk usage. Continue?"
          />
        )}
        {settings.sub && <StreamEditor title="Sub stream" hint="Used for the live grid (SD)." url={`${base}/streams/sub`} config={settings.sub} />}
      </div>
    </>
  );
}

function OverlayEditor({ url, initial, options }: { url: string; initial: OverlaySettings; options: { dateStyles: string[]; timeStyles: string[] } }) {
  const [s, setS] = useState(initial);
  const save = useAction();
  const dirty = JSON.stringify(s) !== JSON.stringify(initial);
  useEffect(() => setS(initial), [initial]);

  return (
    <section className="card">
      <h3>On-screen display</h3>
      <div className="row wrap-gap">
        <label className="check">
          <input type="checkbox" checked={s.showDateTime} onChange={(e) => setS({ ...s, showDateTime: e.target.checked })} />
          Show date &amp; time
        </label>
        <label className="check">
          <input type="checkbox" checked={s.showWeekday} disabled={!s.showDateTime} onChange={(e) => setS({ ...s, showWeekday: e.target.checked })} />
          Show weekday
        </label>
        <label className="check">
          <input type="checkbox" checked={s.showChannelName} onChange={(e) => setS({ ...s, showChannelName: e.target.checked })} />
          Show camera name
        </label>
      </div>
      <div className="row">
        <label>
          Date format
          <select value={s.dateStyle} onChange={(e) => setS({ ...s, dateStyle: e.target.value })}>
            {options.dateStyles.map((o) => (
              <option key={o} value={o}>
                {o.replace('CHR-', 'Localized ')}
              </option>
            ))}
          </select>
        </label>
        <label>
          Time format
          <select value={s.timeStyle} onChange={(e) => setS({ ...s, timeStyle: e.target.value })}>
            {options.timeStyles.map((o) => (
              <option key={o} value={o}>
                {o === '24hour' ? '24-hour' : '12-hour'}
              </option>
            ))}
          </select>
        </label>
      </div>
      {save.error && <div className="alert error">{save.error}</div>}
      {save.notice && <div className="alert ok">{save.notice}</div>}
      <div>
        <button className="primary" disabled={!dirty || save.busy} onClick={() => void save.run(() => api.put(url, s), 'On-screen display saved.')}>
          Save display
        </button>
      </div>
    </section>
  );
}

function StreamEditor({ title, hint, url, config, confirm }: { title: string; hint: string; url: string; config: StreamConfig; confirm?: string }) {
  const [s, setS] = useState<StreamSettings>(config.settings);
  const save = useAction();
  const o = config.options;
  const dirty = JSON.stringify(s) !== JSON.stringify(config.settings);
  const set = <K extends keyof StreamSettings>(key: K, value: StreamSettings[K]) => setS((prev) => ({ ...prev, [key]: value }));

  return (
    <section className="card">
      <h3>{title}</h3>
      <p className="muted small">{hint}</p>
      <div className="row">
        <label>
          Codec
          <select value={s.codec} onChange={(e) => set('codec', e.target.value)}>
            {o.codecs.map((c) => (
              <option key={c}>{c}</option>
            ))}
          </select>
        </label>
        <label>
          Resolution
          <select value={s.resolution} onChange={(e) => set('resolution', e.target.value)}>
            {o.resolutions.map((r) => (
              <option key={r}>{r}</option>
            ))}
          </select>
        </label>
      </div>
      <div className="row">
        <label>
          Bitrate mode
          <select value={s.bitrateMode} onChange={(e) => set('bitrateMode', e.target.value)}>
            {o.bitrateModes.map((m) => (
              <option key={m} value={m}>
                {m === 'VBR' ? 'Variable (VBR)' : 'Constant (CBR)'}
              </option>
            ))}
          </select>
        </label>
        <label>
          {s.bitrateMode === 'VBR' ? 'Max bitrate (Kbps)' : 'Bitrate (Kbps)'}
          <input
            type="number"
            min={o.minBitrateKbps}
            max={o.maxBitrateKbps}
            value={s.bitrateKbps}
            onChange={(e) => set('bitrateKbps', Number(e.target.value))}
          />
        </label>
      </div>
      <div className="row">
        <label>
          Quality {s.bitrateMode !== 'VBR' && <span className="muted small">(VBR only)</span>}
          <select value={s.quality} disabled={s.bitrateMode !== 'VBR'} onChange={(e) => set('quality', Number(e.target.value))}>
            {o.qualities.map((q) => (
              <option key={q} value={q}>
                {q}
              </option>
            ))}
          </select>
        </label>
        <label>
          Frame rate
          <select value={s.maxFrameRate} onChange={(e) => set('maxFrameRate', Number(e.target.value))}>
            {o.frameRates.map((f) => (
              <option key={f} value={f}>
                {frameRateLabel(f)}
              </option>
            ))}
          </select>
        </label>
      </div>
      {save.error && <div className="alert error">{save.error}</div>}
      {save.notice && <div className="alert ok">{save.notice}</div>}
      <div className="row">
        <button
          className="primary"
          disabled={!dirty || save.busy}
          onClick={() => (!confirm || window.confirm(confirm)) && void save.run(() => api.put(url, s), `${title} saved. Live views reconnect automatically.`)}
        >
          Save {title.toLowerCase()}
        </button>
        {dirty && (
          <button className="ghost" onClick={() => setS(config.settings)}>
            Reset
          </button>
        )}
      </div>
    </section>
  );
}
