// Recording times are the NVR's local wall clock with no timezone attached. These helpers do all
// arithmetic in UTC so the browser's own timezone and DST rules never shift them.

const pad = (n: number) => String(n).padStart(2, '0');

function parse(iso: string): Date {
  const [d, t = '00:00:00'] = iso.split('T');
  const [y, mo, da] = d.split('-').map(Number);
  const [h, mi, s] = t.split(':').map((x) => Math.floor(Number(x)));
  return new Date(Date.UTC(y, mo - 1, da, h, mi, s || 0));
}

function format(date: Date): string {
  return (
    `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}` +
    `T${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}:${pad(date.getUTCSeconds())}`
  );
}

export const SECONDS_PER_DAY = 86400;

/** Today's date in the browser's timezone, which is normally the NVR's as well. */
export function today(): string {
  const now = new Date();
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

export function atSecondOfDay(date: string, seconds: number): string {
  return addSeconds(`${date}T00:00:00`, seconds);
}

export function secondOfDay(iso: string): number {
  const d = parse(iso);
  return d.getUTCHours() * 3600 + d.getUTCMinutes() * 60 + d.getUTCSeconds();
}

export function addSeconds(iso: string, seconds: number): string {
  return format(new Date(parse(iso).getTime() + Math.round(seconds) * 1000));
}

export function diffSeconds(a: string, b: string): number {
  return (parse(a).getTime() - parse(b).getTime()) / 1000;
}

export function datePart(iso: string): string {
  return iso.split('T')[0];
}

export function clock(iso: string): string {
  return iso.split('T')[1]?.slice(0, 8) ?? '';
}

export function clockFromSeconds(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  return `${pad(Math.floor(s / 3600))}:${pad(Math.floor((s % 3600) / 60))}:${pad(s % 60)}`;
}

/** Accepts "HH:mm" or "HH:mm:ss"; returns seconds of day or null. */
export function parseClock(text: string): number | null {
  const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(text.trim());
  if (!m) return null;
  const [h, mi, s] = [Number(m[1]), Number(m[2]), Number(m[3] ?? 0)];
  if (h > 23 || mi > 59 || s > 59) return null;
  return h * 3600 + mi * 60 + s;
}

export function formatDuration(seconds: number): string {
  const s = Math.round(seconds);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${pad(s % 60)}s`;
  return `${Math.floor(m / 60)}h ${pad(m % 60)}m`;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let v = bytes / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v < 10 ? 1 : 0)} ${units[i]}`;
}
