import { NavLink, Outlet } from 'react-router-dom';
import { useAuth, useUser } from '../auth';

export function Layout() {
  const user = useUser();
  const { logout } = useAuth();

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <path fill="currentColor" d="M17 10.5V7a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-3.5l4 4v-11z" />
          </svg>
          Camera Viewer
        </div>
        <nav className="nav">
          <NavLink to="/live">Live</NavLink>
          <NavLink to="/playback">Playback</NavLink>
          <NavLink to="/exports">Exports</NavLink>
          {user.role === 'Admin' && (
            <>
              <NavLink to="/admin/nvr">NVR Admin</NavLink>
              <NavLink to="/admin/devices">Devices</NavLink>
              <NavLink to="/admin/users">Users</NavLink>
              <NavLink to="/admin/tailscale">Tailscale</NavLink>
            </>
          )}
        </nav>
        <div className="userbox">
          <NavLink to="/account" className="muted">
            {user.username}
          </NavLink>
          <button className="ghost" onClick={logout}>
            Sign out
          </button>
        </div>
      </header>
      <main className="content">
        <Outlet />
      </main>
    </div>
  );
}
