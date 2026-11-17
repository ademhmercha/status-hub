import { Injectable } from '@angular/core';

export interface AppConfig {
  apiBaseUrl: string;
  auth: {
    mode: 'dev' | 'cognito';
    region?: string;
    clientId?: string;
    /** Cognito hosted UI domain (enables "Continuer avec Google"). */
    domain?: string;
  };
}

export const DEFAULT_CONFIG: AppConfig = { apiBaseUrl: 'http://localhost:5080', auth: { mode: 'dev' } };

/** Runtime configuration (public/config.json), so the same build is deployed to dev and prod. */
@Injectable({ providedIn: 'root' })
export class ConfigService {
  private value: AppConfig = DEFAULT_CONFIG;

  get config(): AppConfig {
    return this.value;
  }

  async load(): Promise<void> {
    try {
      const res = await fetch('config.json', { cache: 'no-store' });
      if (res.ok) this.value = { ...DEFAULT_CONFIG, ...(await res.json()) };
    } catch {
      this.value = DEFAULT_CONFIG;
    }
  }

  set(config: AppConfig): void {
    this.value = config;
  }
}
