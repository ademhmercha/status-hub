import { DestroyRef, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, Subscription } from 'rxjs';

export const REFRESH_MS = 30_000;

export interface ApiError {
  status: number;
  message: string;
  fields: Record<string, string>;
}

export function toApiError(e: unknown): ApiError {
  if (e instanceof HttpErrorResponse) {
    const body = (e.error ?? {}) as { title?: string; detail?: string; errors?: Record<string, string[]> };
    const fields: Record<string, string> = {};
    for (const [k, v] of Object.entries(body.errors ?? {})) fields[k] = v[0] ?? '';
    const message =
      e.status === 0 ? 'Impossible de joindre le serveur.'
      : e.status === 429 ? 'Trop de requêtes, réessayez dans une minute.'
      : (Object.values(fields)[0] ?? body.detail ?? body.title ?? `Erreur ${e.status}`);
    return { status: e.status, message, fields };
  }
  return { status: -1, message: e instanceof Error ? e.message : 'Erreur inattendue.', fields: {} };
}

/**
 * Loads data and refreshes it silently (default every 30 s). Must be called in an injection context.
 * Keeps the last good data when a refresh fails, so a flaky network does not blank the page.
 * Pass autoStart=false when the fetch depends on route inputs, and call reload() once they are set.
 */
export function createLoader<T>(fetch: () => Observable<T>, refreshMs: number | null = REFRESH_MS, autoStart = true) {
  const destroyRef = inject(DestroyRef);
  const data = signal<T | undefined>(undefined);
  const error = signal<string | null>(null);
  const errorStatus = signal<number | null>(null);
  const loading = signal(true);
  let sub: Subscription | undefined;

  const reload = (silent = false) => {
    if (!silent) loading.set(true);
    sub?.unsubscribe();
    sub = fetch().subscribe({
      next: (v) => {
        data.set(v);
        error.set(null);
        errorStatus.set(null);
        loading.set(false);
      },
      error: (e) => {
        const err = toApiError(e);
        error.set(err.message);
        errorStatus.set(err.status);
        loading.set(false);
      },
    });
  };

  if (autoStart) reload();
  if (refreshMs) {
    const id = setInterval(() => reload(true), refreshMs);
    destroyRef.onDestroy(() => clearInterval(id));
  }
  destroyRef.onDestroy(() => sub?.unsubscribe());
  return { data, error, errorStatus, loading, reload };
}
