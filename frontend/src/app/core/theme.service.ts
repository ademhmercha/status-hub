import { Injectable, signal } from '@angular/core';

export type Theme = 'light' | 'dark';
const KEY = 'statushub.theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly theme = signal<Theme>(this.initial());

  constructor() {
    this.apply(this.theme());
  }

  toggle(): void {
    const next: Theme = this.theme() === 'dark' ? 'light' : 'dark';
    this.theme.set(next);
    this.apply(next);
    try { localStorage.setItem(KEY, next); } catch { /* storage unavailable */ }
  }

  private apply(t: Theme): void {
    document.documentElement.setAttribute('data-theme', t);
  }

  private initial(): Theme {
    try {
      const saved = localStorage.getItem(KEY);
      if (saved === 'light' || saved === 'dark') return saved;
    } catch { /* storage unavailable */ }
    return typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
}
