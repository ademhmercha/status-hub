import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ConfigService } from './config';

const STORAGE_KEY = 'statushub.token';

interface TokenClaims { email?: string; exp?: number }

export function decodeToken(token: string): TokenClaims {
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(atob(payload)) as TokenClaims;
  } catch {
    return {};
  }
}

export type RegisterResult = 'signed-in' | 'confirmation-required';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly cfg = inject(ConfigService);
  private readonly router = inject(Router);

  private readonly tokenSig = signal<string | null>(this.readStored());
  readonly token = this.tokenSig.asReadonly();
  readonly email = computed(() => {
    const t = this.tokenSig();
    return t ? (decodeToken(t).email ?? null) : null;
  });
  readonly isAuthenticated = computed(() => {
    const t = this.tokenSig();
    if (!t) return false;
    const exp = decodeToken(t).exp;
    return exp === undefined || exp * 1000 > Date.now();
  });

  get devMode(): boolean {
    return this.cfg.config.auth.mode === 'dev';
  }

  get googleUrl(): string | null {
    const a = this.cfg.config.auth;
    if (a.mode !== 'cognito' || !a.domain || !a.clientId) return null;
    const redirect = encodeURIComponent(`${window.location.origin}/login`);
    return `https://${a.domain}/oauth2/authorize?identity_provider=Google&response_type=token&scope=openid+email+profile&client_id=${a.clientId}&redirect_uri=${redirect}`;
  }

  async login(email: string, password: string): Promise<void> {
    if (this.devMode) {
      const res = await firstValueFrom(this.http.post<{ token: string }>(`${this.cfg.config.apiBaseUrl}/dev/token`, { email }));
      this.setToken(res.token);
      return;
    }
    const res = await firstValueFrom(this.cognito<{ AuthenticationResult?: { IdToken: string } }>('InitiateAuth', {
      AuthFlow: 'USER_PASSWORD_AUTH',
      ClientId: this.cfg.config.auth.clientId,
      AuthParameters: { USERNAME: email, PASSWORD: password },
    }));
    if (!res.AuthenticationResult) throw new Error('Connexion impossible.');
    this.setToken(res.AuthenticationResult.IdToken);
  }

  async register(email: string, password: string): Promise<RegisterResult> {
    if (this.devMode) {
      await this.login(email, password);
      return 'signed-in';
    }
    await firstValueFrom(this.cognito('SignUp', {
      ClientId: this.cfg.config.auth.clientId,
      Username: email,
      Password: password,
      UserAttributes: [{ Name: 'email', Value: email }],
    }));
    return 'confirmation-required';
  }

  async confirm(email: string, code: string): Promise<void> {
    await firstValueFrom(this.cognito('ConfirmSignUp', {
      ClientId: this.cfg.config.auth.clientId, Username: email, ConfirmationCode: code,
    }));
  }

  setToken(token: string): void {
    this.tokenSig.set(token);
    try { sessionStorage.setItem(STORAGE_KEY, token); } catch { /* storage unavailable */ }
  }

  logout(redirect = true): void {
    this.tokenSig.set(null);
    try { sessionStorage.removeItem(STORAGE_KEY); } catch { /* storage unavailable */ }
    if (redirect) void this.router.navigate(['/login']);
  }

  private cognito<T>(target: string, body: unknown) {
    const region = this.cfg.config.auth.region ?? 'eu-north-1';
    return this.http.post<T>(`https://cognito-idp.${region}.amazonaws.com/`, body, {
      headers: new HttpHeaders({
        'Content-Type': 'application/x-amz-json-1.1',
        'X-Amz-Target': `AWSCognitoIdentityProviderService.${target}`,
      }),
    });
  }

  private readStored(): string | null {
    try { return sessionStorage.getItem(STORAGE_KEY); } catch { return null; }
  }
}
