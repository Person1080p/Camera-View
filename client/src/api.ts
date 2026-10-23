export type Role = 'Admin' | 'Viewer';

export interface User {
  id: number;
  username: string;
  role: Role;
  mustChangePassword: boolean;
  deviceIds: number[];
}

export interface Device {
  id: number;
  name: string;
  playbackLimit: number;
  // Connection details are only sent to admins.
  host?: string | null;
  httpPort?: number | null;
  rtspPort?: number | null;
  username?: string | null;
}

export interface DeviceInfo {
  name: string;
  model: string;
  serialNumber: string;
  firmware: string;
  deviceType: string;
}

export interface Channel {
  id: number;
  name: string;
  online: boolean;
}

/** Times are device-local wall-clock strings: "yyyy-MM-ddTHH:mm:ss". */
export interface Segment {
  start: string;
  end: string;
}

export interface StreamInfo {
  id: string;
  playlist: string;
}

export type ExportStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';

export interface ExportJob {
  id: string;
  userId: number;
  username: string;
  deviceId: number;
  deviceName: string;
  channel: number;
  channelName: string;
  start: string;
  end: string;
  codec: 'h264' | 'h265';
  status: ExportStatus;
  progress: number;
  fileSize: number | null;
  error: string | null;
  message: string | null;
  createdAt: string;
  completedAt: string | null;
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

let unauthorizedHandler: (() => void) | null = null;

/** Called whenever the session cookie is rejected, so the app can return to the login screen. */
export function onUnauthorized(handler: () => void) {
  unauthorizedHandler = handler;
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (res.status === 401 && !url.startsWith('/api/auth/login')) unauthorizedHandler?.();

  if (!res.ok) {
    let message = res.statusText || `Request failed (${res.status})`;
    try {
      const data = await res.json();
      if (data?.error) message = data.error;
    } catch {
      // Non-JSON error body; keep the status text.
    }
    throw new ApiError(res.status, message);
  }

  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body: unknown) => request<T>('PUT', url, body),
  del: (url: string) => request<void>('DELETE', url),
};

export const snapshotUrl = (deviceId: number, channel: number, sub = true) =>
  `/api/devices/${deviceId}/channels/${channel}/snapshot?sub=${sub}&t=${Date.now()}`;

export function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

// ---------- NVR administration ----------

export interface NtpSettings {
  host: string;
  port: number;
  intervalMinutes: number;
}

/** Times are ISO 8601 with offsets; drift compares wall clocks (negative = NVR is behind). */
export interface ClockStatus {
  deviceTime: string;
  timeMode: string;
  timeZone: string;
  serverTime: string;
  driftSeconds: number;
  ntp: NtpSettings | null;
}

export interface NvrStatus {
  info: DeviceInfo | null;
  system: { uptimeSeconds: number; cpuPercent: number | null; memoryUsedMb: number | null; memoryAvailableMb: number | null } | null;
  clock: ClockStatus | null;
  network: {
    ipAddress: string | null;
    subnetMask: string | null;
    gateway: string | null;
    primaryDns: string | null;
    secondaryDns: string | null;
    macAddress: string | null;
    addressing: string | null;
  } | null;
  disks: {
    id: number;
    name: string;
    type: string;
    status: string;
    capacityMb: number;
    freeMb: number;
    property: string;
    temperatureC: number | null;
    powerOnDays: number | null;
    health: string | null;
  }[] | null;
  storageMode: string | null;
  channels: { id: number; name: string; ipAddress: string | null; port: number | null; online: boolean; detail: string | null }[] | null;
  errors: string[];
}

/** maxFrameRate is in 1/100 fps; 0 means full frame rate. */
export interface StreamSettings {
  codec: string;
  resolution: string;
  bitrateMode: string;
  quality: number;
  bitrateKbps: number;
  maxFrameRate: number;
}

export interface StreamConfig {
  settings: StreamSettings;
  options: {
    codecs: string[];
    resolutions: string[];
    bitrateModes: string[];
    qualities: number[];
    frameRates: number[];
    minBitrateKbps: number;
    maxBitrateKbps: number;
  };
}

export interface OverlaySettings {
  showDateTime: boolean;
  dateStyle: string;
  timeStyle: string;
  showWeekday: boolean;
  showChannelName: boolean;
}

export interface CameraSettings {
  channel: number;
  name: string;
  ipAddress: string | null;
  overlay: OverlaySettings | null;
  overlayOptions: { dateStyles: string[]; timeStyles: string[] } | null;
  main: StreamConfig | null;
  sub: StreamConfig | null;
  errors: string[];
}

export interface LogEntry {
  time: string;
  major: string;
  minor: string;
  channel: number | null;
  user: string | null;
  ipAddress: string | null;
  detail: string | null;
}

export interface NvrUser {
  id: number;
  userName: string;
  level: string;
  builtIn: boolean;
}

export interface IpSettings {
  addressing: 'static' | 'dhcp';
  ipAddress: string;
  subnetMask: string;
  gateway: string;
  primaryDns: string;
  secondaryDns: string;
}

export interface PortSettings {
  httpPort: number;
  httpsEnabled: boolean;
  httpsPort: number;
  rtspPort: number;
  serverPort: number;
}

export interface NetworkSettings {
  ip: IpSettings | null;
  macAddress: string | null;
  ports: PortSettings | null;
  remote: {
    upnpEnabled: boolean;
    upnpPorts: { service: string; externalPort: number; enabled: boolean }[];
    sshEnabled: boolean | null;
    ddns: DdnsInfo | null;
    cloud: CloudInfo | null;
  } | null;
  errors: string[];
}

export interface SystemSettings {
  deviceName: string;
  overwriteWhenFull: boolean | null;
}

/** Note explains anything the admin still needs to do after a settings change. */
export interface ChangeResult {
  rebootRequired: boolean;
  note: string | null;
}

/** The DDNS password is write-only on the device and never returned. */
export interface DdnsInfo {
  enabled: boolean;
  provider: string;
  providers: string[];
  server: string;
  port: number;
  domain: string;
  userName: string;
  status: string | null;
}

/** EZVIZ / Hik-Connect; the verification code is write-only on the device. */
export interface CloudInfo {
  enabled: boolean;
  server: string;
  registered: boolean;
}
