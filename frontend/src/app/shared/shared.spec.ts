import { TestBed } from '@angular/core/testing';
import { Day, LatencyPoint } from '../core/models';
import { AvailabilityBar } from './availability-bar';
import { ErrorBanner } from './error-banner';
import { LatencyChart } from './latency-chart';
import { StatusDot } from './status-dot';

describe('StatusDot', () => {
  it('renders an accessible label per status', () => {
    for (const [status, label] of [['Up', 'En ligne'], ['Down', 'En panne'], ['Paused', 'En pause'], ['Unknown', 'En attente']] as const) {
      const f = TestBed.createComponent(StatusDot);
      f.componentRef.setInput('status', status);
      f.detectChanges();
      const dot = f.nativeElement.querySelector('.dot') as HTMLElement;
      expect(dot.getAttribute('aria-label')).toBe(label);
      expect(dot.className).toContain(status.toLowerCase());
    }
  });

  it('shows the text label on demand', () => {
    const f = TestBed.createComponent(StatusDot);
    f.componentRef.setInput('status', 'Down');
    f.componentRef.setInput('showLabel', true);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('En panne');
  });
});

describe('AvailabilityBar', () => {
  const days: Day[] = [
    { date: '2026-01-01', availability: 100, total: 1440 },
    { date: '2026-01-02', availability: 97, total: 1440 },
    { date: '2026-01-03', availability: 50, total: 1440 },
    { date: '2026-01-04', availability: null, total: 0 },
  ];

  it('renders one coloured cell per day', () => {
    const f = TestBed.createComponent(AvailabilityBar);
    f.componentRef.setInput('days', days);
    f.detectChanges();
    const cells = Array.from(f.nativeElement.querySelectorAll('.cell')) as HTMLElement[];
    expect(cells.map((c) => c.className.replace('cell', '').trim())).toEqual(['ok', 'warn', 'bad', 'none']);
    expect(cells[2].title).toContain('50 %');
    expect(cells[3].title).toContain('pas de données');
  });
});

describe('LatencyChart', () => {
  const pts: LatencyPoint[] = [
    { t: '2026-01-01T10:00:00Z', p50: 100, p95: 200, total: 10, failures: 0 },
    { t: '2026-01-01T10:15:00Z', p50: 120, p95: 400, total: 10, failures: 1 },
    { t: '2026-01-01T10:30:00Z', p50: 90, p95: 180, total: 10, failures: 0 },
  ];

  it('draws p50 and p95 lines', () => {
    const f = TestBed.createComponent(LatencyChart);
    f.componentRef.setInput('points', pts);
    f.detectChanges();
    const el = f.nativeElement as HTMLElement;
    expect(el.querySelector('path.p50')?.getAttribute('d')).toMatch(/^M[\d.]+,[\d.]+ L/);
    expect(el.querySelector('path.p95')).toBeTruthy();
    expect(el.textContent).toContain('p95');
  });

  it('shows an empty message without data, or when every check failed', () => {
    const f = TestBed.createComponent(LatencyChart);
    f.componentRef.setInput('points', []);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Aucune donnée');

    f.componentRef.setInput('points', [{ t: '2026-01-01T10:00:00Z', p50: 0, p95: 0, total: 3, failures: 3 }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Aucune donnée');
  });

  it('handles a single point without dividing by zero', () => {
    const f = TestBed.createComponent(LatencyChart);
    f.componentRef.setInput('points', [pts[0]]);
    f.detectChanges();
    expect(f.nativeElement.querySelector('path.p50')?.getAttribute('d')).not.toContain('NaN');
  });
});

describe('ErrorBanner', () => {
  it('shows the message and emits retry', () => {
    const f = TestBed.createComponent(ErrorBanner);
    f.componentRef.setInput('message', 'Boom');
    let retried = 0;
    f.componentInstance.retry.subscribe(() => retried++);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Boom');
    (f.nativeElement.querySelector('button') as HTMLButtonElement).click();
    expect(retried).toBe(1);
  });
});
