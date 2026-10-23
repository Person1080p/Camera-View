export function formatUptime(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return d > 0 ? `${d}d ${h}h ${m}m` : h > 0 ? `${h}h ${m}m` : `${m}m`;
}

/** Signed drift in words, e.g. "10m 55s behind". */
export function formatDrift(seconds: number): string {
  const abs = Math.abs(Math.round(seconds));
  const h = Math.floor(abs / 3600);
  const m = Math.floor((abs % 3600) / 60);
  const s = abs % 60;
  const text = h > 0 ? `${h}h ${m}m` : m > 0 ? `${m}m ${s}s` : `${s}s`;
  return `${text} ${seconds < 0 ? 'behind' : 'ahead of'}`;
}

/** "2026-10-23T16:17:20-05:00" → "-05:00" */
export const offsetOf = (iso: string) => /([+-]\d{2}:\d{2}|Z)$/.exec(iso)?.[1] ?? '';

export const clockOf = (iso: string) => iso.slice(0, 19).replace('T', ' ');

/** Devices report frame rate in 1/100 fps; 0 means the camera's full rate. */
export function frameRateLabel(value: number): string {
  if (value === 0) return 'Full';
  if (value >= 100) return `${value / 100} fps`;
  return `1/${Math.round(100 / value)} fps`;
}

/** "remotePlayByFile" → "Remote play by file" */
export function prettyEvent(name: string): string {
  const words = name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

export const gigabytes = (mb: number) => `${(mb / 1024).toFixed(mb >= 10240 ? 0 : 1)} GB`;
