import { useRef, useState, type ReactNode } from 'react';
import type { BuildReport } from '../lib/types';

export function Section({ title, children, right }: { title: string; children: ReactNode; right?: ReactNode }) {
  return (
    <section className="section">
      <header className="section-head"><h3>{title}</h3>{right}</header>
      <div className="section-body">{children}</div>
    </section>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {children}
      {hint && <span className="field-hint">{hint}</span>}
    </label>
  );
}

export function DropZone({ accept, label, sub, onFiles, multiple = false, compact = false }: {
  accept: string; label: string; sub?: string; onFiles: (files: File[]) => void; multiple?: boolean; compact?: boolean;
}) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  return (
    <div
      className={`drop ${over ? 'drop-over' : ''} ${compact ? 'drop-compact' : ''}`}
      onClick={() => input.current?.click()}
      onDragOver={e => { e.preventDefault(); setOver(true); }}
      onDragLeave={() => setOver(false)}
      onDrop={e => { e.preventDefault(); setOver(false); onFiles([...e.dataTransfer.files]); }}
      role="button"
      tabIndex={0}
    >
      <input ref={input} type="file" accept={accept} multiple={multiple} hidden onChange={e => { onFiles([...(e.target.files ?? [])]); e.target.value = ''; }} />
      <strong>{label}</strong>
      {sub && <span>{sub}</span>}
    </div>
  );
}

export function ReportView({ report, error, fileName, onDownload }: { report?: BuildReport | null; error?: string | null; fileName?: string; onDownload?: () => void }) {
  if (error) return <div className="report report-error"><strong>Build failed</strong><p>{error}</p></div>;
  if (!report) return null;
  return (
    <div className="report">
      <div className="report-head">
        <strong>Ready: {fileName}</strong>
        {onDownload && <button className="btn btn-small" onClick={onDownload}>Download again</button>}
      </div>
      {report.warnings.length > 0 && (
        <ul className="report-warn">{report.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
      )}
      <details open>
        <summary>Checks (every file was read back after building)</summary>
        <ul className="report-checks">{report.checks.map((c, i) => <li key={i}>{c}</li>)}</ul>
      </details>
    </div>
  );
}
