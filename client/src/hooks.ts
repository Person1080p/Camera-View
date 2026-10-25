import { useCallback, useEffect, useState } from 'react';
import { api, errorMessage, type Channel, type Device } from './api';

/** Loads a JSON resource, re-fetching whenever `url` changes. A null url skips loading. */
export function useResource<T>(url: string | null) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    if (!url) {
      setData(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    api
      .get<T>(url)
      .then((d) => !cancelled && setData(d))
      .catch((e) => !cancelled && setError(errorMessage(e)))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
  }, [url, version]);

  const reload = useCallback(() => setVersion((v) => v + 1), []);
  return { data, error, loading, reload, setData };
}

export const useDevices = () => useResource<Device[]>('/api/devices');

export const useChannels = (deviceId: number | null) =>
  useResource<Channel[]>(deviceId ? `/api/devices/${deviceId}/channels` : null);

/** A string remembered in localStorage (per browser), e.g. the last selected device. */
export function useStoredState(key: string, initial: string): [string, (v: string) => void] {
  const read = () => {
    try {
      return localStorage.getItem(key) ?? initial;
    } catch {
      return initial;
    }
  };
  const [state, setState] = useState(() => ({ key, value: read() }));
  // The key may change (e.g. per-device settings); the stored value for the new key is read straight away.
  const value = state.key === key ? state.value : read();
  const set = useCallback(
    (v: string) => {
      setState({ key, value: v });
      try {
        localStorage.setItem(key, v);
      } catch {
        // Storage unavailable (private mode); the value just won't persist.
      }
    },
    [key],
  );
  return [value, set];
}

/** Runs one async action at a time, tracking busy state and the resulting error or success notice. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const run = useCallback(async (action: () => Promise<unknown>, success?: string) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
      if (success) setNotice(success);
      return true;
    } catch (e) {
      setError(errorMessage(e));
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  return { run, busy, error, notice };
}
