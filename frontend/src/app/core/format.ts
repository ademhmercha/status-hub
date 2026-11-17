export function pct(v: number | null | undefined, digits = 2): string {
  if (v === null || v === undefined) return '—';
  return v.toFixed(digits).replace(/\.?0+$/, '') + ' %';
}

export function duration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return 'en cours';
  const s = Math.round(seconds);
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min ${s % 60} s`;
  const h = Math.floor(m / 60);
  return `${h} h ${m % 60} min`;
}

export function relativeTime(iso: string | null | undefined, now: Date = new Date()): string {
  if (!iso) return 'jamais';
  const diff = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000));
  if (diff < 60) return `il y a ${diff} s`;
  if (diff < 3600) return `il y a ${Math.floor(diff / 60)} min`;
  if (diff < 86400) return `il y a ${Math.floor(diff / 3600)} h`;
  return `il y a ${Math.floor(diff / 86400)} j`;
}

/** Colour class for an availability value (null = no data). */
export function availabilityClass(v: number | null | undefined): 'none' | 'ok' | 'warn' | 'bad' {
  if (v === null || v === undefined) return 'none';
  if (v >= 99.5) return 'ok';
  if (v >= 95) return 'warn';
  return 'bad';
}
