import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { Alert, Monitor, StatusPage } from '../core/models';
import { Alerts } from './alerts';
import { Dashboard } from './dashboard';
import { DemoPanel } from './demo-panel';
import { LoginPage } from './login';
import { MonitorForm } from './monitor-form';
import { PublicStatus } from './public-status';

const monitor = (over: Partial<Monitor> = {}): Monitor => ({
  id: 'm1', name: 'Mon site', url: 'https://example.com', method: 'GET', intervalMinutes: 1, expectedStatus: 200, keyword: null,
  timeoutSeconds: 10, active: true, status: 'Up', createdAt: '2026-01-01T00:00:00Z', lastCheckedAt: null, openIncidentId: null,
  availability30d: 99.9, ...over,
});

function setup(api: Partial<Record<keyof ApiService, unknown>>, extra: unknown[] = []) {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), { provide: ApiService, useValue: { demo: () => of({ mode: 'ok', targetUrl: 'http://x/demo/target' }), ...api } }, ...extra as never[]],
  });
}

const text = (f: { nativeElement: HTMLElement }) => f.nativeElement.textContent ?? '';

describe('Dashboard', () => {
  it('lists monitors with status and availability', async () => {
    setup({ monitors: () => of([monitor(), monitor({ id: 'm2', name: 'API', status: 'Down', availability30d: 80 })]) });
    const f = TestBed.createComponent(Dashboard);
    f.detectChanges();
    const t = text(f);
    expect(t).toContain('Mon site');
    expect(t).toContain('99.9 %');
    expect(t).toContain('1 en ligne · 1 en panne');
    expect(f.nativeElement.querySelectorAll('tbody tr').length).toBe(2);
    expect(f.nativeElement.querySelector('[aria-label="En panne"]')).toBeTruthy();
  });

  it('shows a helpful empty state', async () => {
    setup({ monitors: () => of([]) });
    const f = TestBed.createComponent(Dashboard);
    f.detectChanges();
    expect(text(f)).toContain("Aucun moniteur pour l'instant");
    expect(text(f)).toContain('Ajouter mon premier moniteur');
  });

  it('shows an error with retry, and recovers', async () => {
    let fail = true;
    setup({ monitors: () => (fail ? throwError(() => new HttpErrorResponse({ status: 0 })) : of([monitor()])) });
    const f = TestBed.createComponent(Dashboard);
    f.detectChanges();
    expect(text(f)).toContain('Impossible de joindre le serveur');
    fail = false;
    (Array.from(f.nativeElement.querySelectorAll('button')) as HTMLButtonElement[]).find((b) => b.textContent?.includes('Réessayer'))!.click();
    f.detectChanges();
    expect(text(f)).toContain('Mon site');
    expect(text(f)).not.toContain('Impossible de joindre');
  });
});

describe('DemoPanel', () => {
  it('switches the demo target state with the buttons', () => {
    const setDemo = vi.fn((mode: string) => of({ mode, targetUrl: 'http://x/demo/target' }));
    setup({ setDemo });
    const f = TestBed.createComponent(DemoPanel);
    f.detectChanges();
    const buttons = Array.from(f.nativeElement.querySelectorAll('button')) as HTMLButtonElement[];
    buttons.find((b) => b.textContent?.includes('Simuler une panne'))!.click();
    expect(setDemo).toHaveBeenCalledWith('down');
    f.detectChanges();
    expect(text(f)).toContain('Cible : en panne');
    buttons.find((b) => b.textContent?.includes('Rétablir'))!.click();
    expect(setDemo).toHaveBeenLastCalledWith('ok');
  });

  it('adds the demo monitor pointing to the demo target, once', () => {
    const createMonitor = vi.fn(() => of(monitor({ id: 'demo' })));
    setup({ createMonitor });
    const f = TestBed.createComponent(DemoPanel);
    f.detectChanges();
    const add = (Array.from(f.nativeElement.querySelectorAll('button')) as HTMLButtonElement[]).find((b) => b.textContent?.includes('Ajouter un moniteur de démo'))!;
    add.click();
    expect(createMonitor).toHaveBeenCalledWith(expect.objectContaining({ url: 'http://x/demo/target', intervalMinutes: 1 }));

    f.componentRef.setInput('monitors', [monitor({ url: 'http://x/demo/target' })]);
    f.detectChanges();
    expect(text(f)).toContain('déjà ajouté');
  });
});

describe('MonitorForm', () => {
  const fill = (f: { nativeElement: HTMLElement }, name: string, value: string) => {
    const el = f.nativeElement.querySelector(`[name="${name}"]`) as HTMLInputElement;
    el.value = value;
    el.dispatchEvent(new Event('input'));
  };

  it('validates required fields client-side without calling the API', async () => {
    const createMonitor = vi.fn();
    setup({ createMonitor });
    const f = TestBed.createComponent(MonitorForm);
    f.detectChanges();
    f.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));
    f.detectChanges();
    expect(text(f)).toContain('Le nom est requis');
    expect(text(f)).toContain('doit commencer par http');
    expect(createMonitor).not.toHaveBeenCalled();
  });

  it('submits a valid monitor and navigates to its page', async () => {
    const createMonitor = vi.fn(() => of(monitor({ id: 'new1' })));
    setup({ createMonitor });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(MonitorForm);
    f.detectChanges();
    await f.whenStable();
    fill(f, 'name', ' Mon API ');
    fill(f, 'url', 'https://example.com/health');
    fill(f, 'keyword', 'ok');
    f.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));
    expect(createMonitor).toHaveBeenCalledWith(expect.objectContaining({ name: 'Mon API', url: 'https://example.com/health', keyword: 'ok', method: 'GET' }));
    expect(navigate).toHaveBeenCalledWith(['/monitors', 'new1']);
  });

  it('shows server-side validation errors next to the field', async () => {
    const err = new HttpErrorResponse({ status: 400, error: { errors: { url: ['Les adresses IP privees ne sont pas autorisees.'] } } });
    setup({ createMonitor: () => throwError(() => err) });
    const f = TestBed.createComponent(MonitorForm);
    f.detectChanges();
    await f.whenStable();
    fill(f, 'name', 'x');
    fill(f, 'url', 'http://10.0.0.1');
    f.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));
    f.detectChanges();
    expect(text(f)).toContain('adresses IP privees');
  });

  it('loads an existing monitor for editing', async () => {
    setup({ monitor: () => of(monitor({ name: 'Existant', keyword: 'bonjour' })) });
    const f = TestBed.createComponent(MonitorForm);
    f.componentRef.setInput('id', 'm1');
    f.detectChanges();
    await f.whenStable();
    expect((f.nativeElement.querySelector('[name="name"]') as HTMLInputElement).value).toBe('Existant');
    expect(text(f)).toContain('Modifier le moniteur');
  });
});

describe('LoginPage', () => {
  it('signs in with the dev token flow then navigates home', async () => {
    const login = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: { devMode: true, googleUrl: null, isAuthenticated: () => false, login, register: vi.fn(), confirm: vi.fn() } }],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(LoginPage);
    f.detectChanges();
    await f.whenStable();
    const email = f.nativeElement.querySelector('[name="email"]') as HTMLInputElement;
    email.value = 'me@x.io';
    email.dispatchEvent(new Event('input'));
    f.detectChanges();
    await f.whenStable();
    await f.componentInstance['submit']();
    expect(login).toHaveBeenCalledWith('me@x.io', '');
    expect(navigate).toHaveBeenCalledWith(['/']);
  });

  it('shows the failure message', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: { devMode: false, googleUrl: null, isAuthenticated: () => false, login: vi.fn().mockRejectedValue(new Error('Mot de passe incorrect')) } }],
    });
    const f = TestBed.createComponent(LoginPage);
    f.detectChanges();
    await f.componentInstance['submit']();
    f.detectChanges();
    expect(text(f)).toContain('Mot de passe incorrect');
  });

  it('offers Google sign-in only when configured', () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: { devMode: false, googleUrl: 'https://auth.example/oauth2/authorize', isAuthenticated: () => false } }],
    });
    const f = TestBed.createComponent(LoginPage);
    f.detectChanges();
    expect(text(f)).toContain('Continuer avec Google');
  });
});

describe('PublicStatus', () => {
  const page = (over: Partial<StatusPage> = {}): StatusPage => ({
    slug: 'acme', overall: 'partial_outage', generatedAt: '2026-01-01T12:00:00Z',
    services: [
      { name: 'Site', status: 'Up', availability90d: 99.98, days: Array.from({ length: 90 }, (_, i) => ({ date: `d${i}`, availability: 100, total: 10 })) },
      { name: 'API', status: 'Down', availability90d: 97, days: [] },
    ],
    incidents: [{ id: 'i1', monitorId: 'm', monitorName: 'API', openedAt: '2026-01-01T11:00:00Z', resolvedAt: null, durationSeconds: null, cause: 'Timeout' }],
    ...over,
  });

  it('renders overall state, a band per service and recent incidents', async () => {
    setup({ statusPage: () => of(page()) });
    const f = TestBed.createComponent(PublicStatus);
    f.componentRef.setInput('slug', 'acme');
    f.detectChanges();
    expect(text(f)).toContain('Panne partielle');
    expect(f.nativeElement.querySelectorAll('section.card').length).toBe(3); // 2 services + incidents
    expect(f.nativeElement.querySelectorAll('.cell').length).toBe(90);
    expect(text(f)).toContain('Timeout');
    expect(text(f)).toContain('En cours');
  });

  it('shows the all-clear banner', async () => {
    setup({ statusPage: () => of(page({ overall: 'operational', incidents: [] })) });
    const f = TestBed.createComponent(PublicStatus);
    f.componentRef.setInput('slug', 'acme');
    f.detectChanges();
    expect(text(f)).toContain('Tous les systèmes sont opérationnels');
    expect(text(f)).toContain('Aucun incident récent');
  });

  it('shows a not-found state for an unknown slug', async () => {
    setup({ statusPage: () => throwError(() => new HttpErrorResponse({ status: 404, error: { title: 'Not found' } })) });
    const f = TestBed.createComponent(PublicStatus);
    f.componentRef.setInput('slug', 'nope');
    f.detectChanges();
    expect(text(f)).toContain('Page introuvable');
  });
});

describe('Alerts', () => {
  const email: Alert = { id: 'a1', type: 'email', target: 'me@x.io', hasSecret: false, active: true };

  it('lists alerts, sends a test and reveals a new webhook secret once', async () => {
    const testAlert = vi.fn(() => of({ sent: true }));
    const createAlert = vi.fn(() => of({ id: 'a2', type: 'webhook', target: 'https://h.io', hasSecret: true, active: true, secret: 'S3CRET' } as Alert));
    setup({ alerts: () => of([email]), testAlert, createAlert });
    const f = TestBed.createComponent(Alerts);
    f.detectChanges();
    expect(text(f)).toContain('me@x.io');

    (Array.from(f.nativeElement.querySelectorAll('button')) as HTMLButtonElement[]).find((b) => b.textContent?.includes('Envoyer un test'))!.click();
    f.detectChanges();
    expect(testAlert).toHaveBeenCalledWith('a1');
    expect(text(f)).toContain('Test envoyé à me@x.io');

    f.componentInstance['type'] = 'webhook';
    f.componentInstance['target'] = 'https://h.io';
    f.componentInstance['add']();
    f.detectChanges();
    expect(createAlert).toHaveBeenCalledWith({ type: 'webhook', target: 'https://h.io' });
    expect(text(f)).toContain('S3CRET');
  });

  it('shows the empty state', async () => {
    setup({ alerts: () => of([]) });
    const f = TestBed.createComponent(Alerts);
    f.detectChanges();
    expect(text(f)).toContain('Aucune alerte configurée');
  });
});
