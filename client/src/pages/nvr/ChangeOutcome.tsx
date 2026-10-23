import type { ChangeResult } from '../../api';

/** Explains what (if anything) is still needed after a settings change. */
export function ChangeOutcome({ result, onReboot }: { result: ChangeResult | null; onReboot: () => void }) {
  if (!result) return null;
  return (
    <>
      {result.rebootRequired && (
        <div className="alert warn">
          The NVR needs a reboot before this change takes effect.{' '}
          <button className="link" onClick={onReboot}>
            Go to Maintenance
          </button>
        </div>
      )}
      {result.note && <div className="alert ok">{result.note}</div>}
    </>
  );
}
