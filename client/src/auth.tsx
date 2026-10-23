import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, ApiError, onUnauthorized, type User } from './api';

interface AuthState {
  user: User | null;
  loading: boolean;
  login(username: string, password: string, rememberMe: boolean): Promise<void>;
  logout(): Promise<void>;
  setUser(user: User): void;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    onUnauthorized(() => setUser(null));
    api
      .get<User>('/api/auth/me')
      .then(setUser)
      .catch((err) => {
        if (!(err instanceof ApiError && err.status === 401)) console.error(err);
      })
      .finally(() => setLoading(false));
  }, []);

  const login = useCallback(async (username: string, password: string, rememberMe: boolean) => {
    setUser(await api.post<User>('/api/auth/login', { username, password, rememberMe }));
  }, []);

  const logout = useCallback(async () => {
    await api.post('/api/auth/logout').catch(() => undefined);
    setUser(null);
  }, []);

  return (
    <AuthContext.Provider value={{ user, loading, login, logout, setUser }}>{children}</AuthContext.Provider>
  );
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}

/** The signed-in user; only call from components rendered behind the login gate. */
export function useUser(): User {
  const { user } = useAuth();
  if (!user) throw new Error('Not signed in');
  return user;
}
