import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { api, errorMessage, type ExportJob } from '../api';
import { useUser } from '../auth';
import { useResource } from '../hooks';
import { clock, datePart, diffSeconds, formatBytes, formatDuration } from '../time';

const isActive = (j: ExportJob) => j.status === 'Queued' || j.status === 'Running';

export function ExportsPage() {
  const user = useUser();
  const { data, error, reload } = useResource<ExportJob[]>('/api/exports');
  const jobs = data ?? [];
  const anyActive = jobs.some(isActive);

  useEffect(() => {
    if (!anyActive) return;
    const timer = setInterval(reload, 2000);
    return () => clearInterval(timer);
  }, [anyActive, reload]);

  const remove = async (job: ExportJob) => {
    const verb = isActive(job) ? 'Cancel' : 'Delete';
    if (!window.confirm(`${verb} this export?`)) return;
    try {
      await api.del(`/api/exports/${job.id}`);
    } catch (e) {
      window.alert(errorMessage(e));
    }
    reload();
  };

  return (
    <div className="page">
      <div className="page-head">
        <h1>Exports</h1>
        <span className="muted small">
          Create exports from the <Link to="/playback">Playback</Link> page. The recorder streams at normal speed, so a 5 minute clip takes about 5 minutes.
        </span>
      </div>
      {error && <div className="alert error">{error}</div>}
      {data && jobs.length === 0 && <div className="empty-state">No exports yet.</div>}
      {jobs.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Camera</th>
                <th>Time range</th>
                <th>Format</th>
                {user.role === 'Admin' && <th>By</th>}
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.id}>
                  <td>
                    <div>{j.channelName}</div>
                    <div className="muted small">{j.deviceName}</div>
                  </td>
                  <td>
                    <div>
                      {datePart(j.start)} {clock(j.start)} – {clock(j.end)}
                    </div>
                    <div className="muted small">{formatDuration(diffSeconds(j.end, j.start))}</div>
                  </td>
                  <td>{j.codec === 'h265' ? 'H.265' : 'H.264'}</td>
                  {user.role === 'Admin' && <td>{j.username}</td>}
                  <td className="status-cell">
                    <span className={`badge badge-${j.status.toLowerCase()}`}>{j.status}</span>
                    {j.status === 'Running' && (
                      <div className="progress">
                        <div style={{ width: `${Math.round(j.progress * 100)}%` }} />
                      </div>
                    )}
                    {j.message && <div className="muted small">{j.message}</div>}
                    {j.error && <div className="error-text small">{j.error}</div>}
                    {j.status === 'Completed' && j.fileSize !== null && <div className="muted small">{formatBytes(j.fileSize)}</div>}
                  </td>
                  <td className="actions">
                    {j.status === 'Completed' && (
                      <a className="button primary" href={`/api/exports/${j.id}/download`} download>
                        Download
                      </a>
                    )}
                    <button className="ghost" onClick={() => remove(j)}>
                      {isActive(j) ? 'Cancel' : 'Delete'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
