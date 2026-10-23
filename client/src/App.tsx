import { Navigate, Route, Routes } from 'react-router-dom';
import { useAuth } from './auth';
import { Layout } from './components/Layout';
import { ChangePasswordPage } from './pages/ChangePasswordPage';
import { DevicesPage } from './pages/DevicesPage';
import { ExportsPage } from './pages/ExportsPage';
import { LivePage } from './pages/LivePage';
import { LoginPage } from './pages/LoginPage';
import { NvrAdminPage } from './pages/NvrAdminPage';
import { TailscalePage } from './pages/TailscalePage';
import { PlaybackPage } from './pages/PlaybackPage';
import { UsersPage } from './pages/UsersPage';

export default function App() {
  const { user, loading } = useAuth();

  if (loading) return <div className="auth-screen muted">Loading…</div>;
  if (!user) return <LoginPage />;
  if (user.mustChangePassword) return <ChangePasswordPage forced />;

  const admin = user.role === 'Admin';
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/live" element={<LivePage />} />
        <Route path="/playback" element={<PlaybackPage />} />
        <Route path="/exports" element={<ExportsPage />} />
        <Route path="/account" element={<ChangePasswordPage />} />
        {admin && <Route path="/admin/devices" element={<DevicesPage />} />}
        {admin && <Route path="/admin/users" element={<UsersPage />} />}
        {admin && <Route path="/admin/nvr" element={<NvrAdminPage />} />}
        {admin && <Route path="/admin/tailscale" element={<TailscalePage />} />}
        <Route path="*" element={<Navigate to="/live" replace />} />
      </Route>
    </Routes>
  );
}
