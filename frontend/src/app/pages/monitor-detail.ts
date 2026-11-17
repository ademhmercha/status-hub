import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiService } from '../core/api.service';
import { duration, pct, relativeTime } from '../core/format';
import { REFRESH_MS, createLoader, toApiError } from '../core/loader';
import { AvailabilityBar } from '../shared/availability-bar';
import { ErrorBanner } from '../shared/error-banner';
import { LatencyChart } from '../shared/latency-chart';
import { StatusDot } from '../shared/status-dot';

type Range = '24h' | '7d' | '30d';

@Component({
  selector: 'app-monitor-detail',
  imports: [RouterLink, StatusDot, AvailabilityBar, LatencyChart, ErrorBanner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="stack">
      <a routerLink="/" class="small">← Tableau de bord</a>

      @if (loader.error(); as e) { <app-error-banner [message]="e" (retry)="loader.reload()" /> }
      @if (actionError(); as e) { <app-error-banner [message]="e" [retryable]="false" /> }

      @if (loader.data(); as d) {
        <div class="row between">
          <div>
            <div class="row"><h1>{{ d.monitor.name }}</h1><app-status-dot [status]="d.monitor.status" [showLabel]="true" /></div>
            <div class="muted mono">{{ d.monitor.method }} {{ d.monitor.url }}</div>
            <div class="muted small">Toutes les {{ d.monitor.intervalMinutes }} min · code attendu {{ d.monitor.expectedStatus }}
              @if (d.monitor.keyword) { · mot-clé « {{ d.monitor.keyword }} » } · dernier contrôle {{ relative(d.monitor.lastCheckedAt) }}</div>
          </div>
          <div class="row">
            <button type="button" class="btn" [disabled]="busy()" (click)="toggle()">{{ d.monitor.active ? 'Mettre en pause' : 'Reprendre' }}</button>
            <a class="btn" [routerLink]="['/monitors', d.monitor.id, 'edit']">Modifier</a>
            <button type="button" class="btn danger" [disabled]="busy()" (click)="remove()">Supprimer</button>
          </div>
        </div>

        <div class="grid">
          <div class="card stat"><div class="label">Disponibilité 24 h</div><div class="value">{{ pct(d.stats.availability24h) }}</div></div>
          <div class="card stat"><div class="label">Disponibilité 7 j</div><div class="value">{{ pct(d.stats.availability7d) }}</div></div>
          <div class="card stat"><div class="label">Disponibilité 30 j</div><div class="value">{{ pct(d.stats.availability30d) }}</div></div>
          <div class="card stat"><div class="label">Latence p50 / p95 (24 h)</div>
            <div class="value">{{ d.stats.p50Ms24h ?? '—' }} / {{ d.stats.p95Ms24h ?? '—' }} <span class="muted small">ms</span></div></div>
        </div>

        <section class="card">
          <div class="row between">
            <h2>Latence</h2>
            <div class="row">
              @for (r of ranges; track r) {
                <button type="button" class="btn small" [class.active]="range() === r" (click)="range.set(r)">{{ r }}</button>
              }
            </div>
          </div>
          <app-latency-chart [points]="latency.data() ?? []" [range]="range()" />
        </section>

        <section class="card">
          <h2>Disponibilité sur 90 jours</h2>
          <app-availability-bar [days]="d.days" />
          <div class="row between muted small"><span>il y a 90 jours</span><span>aujourd'hui</span></div>
        </section>

        <section class="card">
          <h2>Incidents</h2>
          @if (d.incidents.length === 0) {
            <p class="muted">Aucun incident enregistré.</p>
          } @else {
            <div class="table-wrap"><table>
              <thead><tr><th>Ouvert</th><th>Cause</th><th>Durée</th><th>État</th></tr></thead>
              <tbody>
                @for (i of d.incidents; track i.id) {
                  <tr>
                    <td>{{ formatDate(i.openedAt) }}</td>
                    <td>{{ i.cause }}</td>
                    <td>{{ duration(i.durationSeconds) }}</td>
                    <td><span class="badge" [class.bad]="!i.resolvedAt" [class.ok]="!!i.resolvedAt">{{ i.resolvedAt ? 'Résolu' : 'En cours' }}</span></td>
                  </tr>
                }
              </tbody>
            </table></div>
          }
        </section>
      } @else if (loader.loading()) {
        <div class="card muted" role="status">Chargement…</div>
      }
      <p class="muted small">Actualisation automatique toutes les 30 secondes.</p>
    </div>`,
})
export class MonitorDetail {
  readonly id = input.required<string>();

  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  protected readonly ranges: Range[] = ['24h', '7d', '30d'];
  protected readonly range = signal<Range>('24h');
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);

  protected readonly loader = createLoader(() =>
    forkJoin({
      monitor: this.api.monitor(this.id()),
      stats: this.api.stats(this.id()),
      days: this.api.availability(this.id(), 90),
      incidents: this.api.incidents(this.id()),
    }),
    REFRESH_MS,
    false,
  );
  protected readonly latency = createLoader(() => this.api.latency(this.id(), this.range()), REFRESH_MS, false);

  protected readonly pct = pct;
  protected readonly duration = duration;
  protected readonly isActive = computed(() => this.loader.data()?.monitor.active ?? true);

  constructor() {
    // Route inputs are only available after construction: load once the id is known, and reload the chart on range change.
    effect(() => {
      this.id();
      untracked(() => this.loader.reload());
    });
    effect(() => {
      this.id();
      this.range();
      untracked(() => this.latency.reload());
    });
  }

  protected relative(iso: string | null): string { return relativeTime(iso); }
  protected formatDate(iso: string): string { return new Date(iso).toLocaleString('fr-FR'); }

  protected toggle(): void {
    const m = this.loader.data()?.monitor;
    if (!m) return;
    this.run((m.active ? this.api.pause(m.id) : this.api.resume(m.id)), () => this.loader.reload(true));
  }

  protected remove(): void {
    const m = this.loader.data()?.monitor;
    if (!m || !confirm(`Supprimer « ${m.name} » et arrêter sa surveillance ?`)) return;
    this.run(this.api.deleteMonitor(m.id), () => void this.router.navigate(['/']));
  }

  private run(obs: import('rxjs').Observable<unknown>, done: () => void): void {
    this.busy.set(true);
    this.actionError.set(null);
    obs.subscribe({
      next: () => { this.busy.set(false); done(); },
      error: (e) => { this.busy.set(false); this.actionError.set(toApiError(e).message); },
    });
  }
}
