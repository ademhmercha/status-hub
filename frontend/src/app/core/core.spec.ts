import { HttpErrorResponse, HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, Injector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { authGuard } from './auth.guard';
import { authInterceptor } from './auth.interceptor';
import { AuthService, decodeToken } from './auth.service';
import { ConfigService } from './config';
import { availabilityClass, duration, pct, relativeTime } from './format';
import { createLoader, toApiError } from './loader';
import { ThemeService } from './theme.service';

function fakeJwt(payload: object): string {
  const b64 = (o: object) => btoa(JSON.stringify(o)).replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${b64({ alg: 'none' })}.${b64(payload)}.sig`;
}

describe('format', () => {
  it('formats percentages without trailing zeros', () => {
    expect(pct(100)).toBe('100 %');
    expect(pct(99.5)).toBe('99.5 %');
    expect(pct(99.123)).toBe('99.12 %');
    expect(pct(null)).toBe('—');
  });

  it('formats durations', () => {
    expect(duration(null)).toBe('en cours');
    expect(duration(42)).toBe('42 s');
    expect(duration(185)).toBe('3 min 5 s');
    expect(duration(7800)).toBe('2 h 10 min');
  });

  it('formats relative time', () => {
    const now = new Date('2026-01-01T12:00:00Z');
    expect(relativeTime(null, now)).toBe('jamais');
    expect(relativeTime('2026-01-01T11:59:30Z', now)).toBe('il y a 30 s');
    expect(relativeTime('2026-01-01T11:50:00Z', now)).toBe('il y a 10 min');
    expect(relativeTime('2025-12-30T12:00:00Z', now)).toBe('il y a 2 j');
  });

  it('classifies availability', () => {
    expect(availabilityClass(null)).toBe('none');
    expect(availabilityClass(100)).toBe('ok');
    expect(availabilityClass(97)).toBe('warn');
    expect(availabilityClass(80)).toBe('bad');
  });
});

describe('toApiError', () => {
  it('maps ProblemDetails validation errors to fields', () => {
    const e = new HttpErrorResponse({ status: 400, error: { title: 'Bad', errors: { url: ['URL invalide.'] } } });
    const r = toApiError(e);
    expect(r.fields['url']).toBe('URL invalide.');
    expect(r.message).toBe('URL invalide.');
  });

  it('has friendly messages for network failure and rate limiting', () => {
    expect(toApiError(new HttpErrorResponse({ status: 0 })).message).toContain('joindre');
    expect(toApiError(new HttpErrorResponse({ status: 429 })).message).toContain('Trop de requêtes');
  });
});

describe('createLoader', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('loads, then refreshes silently every 30 s and keeps data on failure', () => {
    let calls = 0;
    const injector = TestBed.inject(Injector);
    const loader = runInInjectionContext(injector, () =>
      createLoader(() => {
        calls++;
        return calls === 3 ? throwError(() => new HttpErrorResponse({ status: 500 })) : of(calls);
      }),
    );
    expect(loader.data()).toBe(1);
    expect(loader.loading()).toBe(false);

    vi.advanceTimersByTime(30_000);
    expect(loader.data()).toBe(2);
    expect(loader.loading()).toBe(false); // silent: no loading flash

    vi.advanceTimersByTime(30_000);
    expect(loader.error()).toBe('Erreur 500');
    expect(loader.data()).toBe(2); // last good value kept
  });
});

describe('auth', () => {
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'login', component: class {} as never }]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
  });

  it('decodes email and expiry from a token', () => {
    expect(decodeToken(fakeJwt({ email: 'a@b.c', exp: 5 }))).toEqual({ email: 'a@b.c', exp: 5 });
    expect(decodeToken('garbage')).toEqual({});
  });

  it('is unauthenticated without token or with an expired token', () => {
    const auth = TestBed.inject(AuthService);
    expect(auth.isAuthenticated()).toBe(false);
    auth.setToken(fakeJwt({ email: 'a@b.c', exp: 1 }));
    expect(auth.isAuthenticated()).toBe(false);
    auth.setToken(fakeJwt({ email: 'a@b.c', exp: Math.floor(Date.now() / 1000) + 3600 }));
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.email()).toBe('a@b.c');
  });

  it('guard redirects to /login when signed out and allows when signed in', () => {
    const auth = TestBed.inject(AuthService);
    const router = TestBed.inject(Router);
    const run = () => TestBed.runInInjectionContext(() => authGuard({} as never, {} as never));
    expect(String(run())).toBe(String(router.createUrlTree(['/login'])));
    auth.setToken(fakeJwt({ email: 'a@b.c', exp: Math.floor(Date.now() / 1000) + 3600 }));
    expect(run()).toBe(true);
  });

  it('dev login posts the email and stores the token', async () => {
    const auth = TestBed.inject(AuthService);
    const http = TestBed.inject(HttpTestingController);
    const base = TestBed.inject(ConfigService).config.apiBaseUrl;
    const p = auth.login('me@x.io', 'ignored');
    const req = http.expectOne(`${base}/dev/token`);
    expect(req.request.body).toEqual({ email: 'me@x.io' });
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({ token: fakeJwt({ email: 'me@x.io', exp: Math.floor(Date.now() / 1000) + 60 }) });
    await p;
    expect(auth.email()).toBe('me@x.io');
  });

  it('interceptor adds the bearer to API calls only, not to public status or other hosts', () => {
    const auth = TestBed.inject(AuthService);
    auth.setToken(fakeJwt({ email: 'a@b.c' }));
    const http = TestBed.inject(HttpClient);
    const ctl = TestBed.inject(HttpTestingController);
    const base = TestBed.inject(ConfigService).config.apiBaseUrl;

    http.get(`${base}/api/v1/monitors`).subscribe();
    expect(ctl.expectOne(`${base}/api/v1/monitors`).request.headers.get('Authorization')).toMatch(/^Bearer /);

    http.get(`${base}/api/v1/status/acme`).subscribe();
    expect(ctl.expectOne(`${base}/api/v1/status/acme`).request.headers.has('Authorization')).toBe(false);

    http.post('https://cognito-idp.eu-north-1.amazonaws.com/', {}).subscribe();
    expect(ctl.expectOne('https://cognito-idp.eu-north-1.amazonaws.com/').request.headers.has('Authorization')).toBe(false);
  });

  it('interceptor signs out on 401', () => {
    const auth = TestBed.inject(AuthService);
    auth.setToken(fakeJwt({ email: 'a@b.c' }));
    const http = TestBed.inject(HttpClient);
    const ctl = TestBed.inject(HttpTestingController);
    const base = TestBed.inject(ConfigService).config.apiBaseUrl;
    http.get(`${base}/api/v1/monitors`).subscribe({ error: () => undefined });
    ctl.expectOne(`${base}/api/v1/monitors`).flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(auth.token()).toBeNull();
  });
});

describe('ThemeService', () => {
  it('toggles and persists the theme on <html>', () => {
    localStorage.clear();
    const theme = TestBed.inject(ThemeService);
    const before = theme.theme();
    theme.toggle();
    expect(theme.theme()).not.toBe(before);
    expect(document.documentElement.getAttribute('data-theme')).toBe(theme.theme());
    expect(localStorage.getItem('statushub.theme')).toBe(theme.theme());
  });
});

@Component({ template: '' })
class Dummy {}
void Dummy;
