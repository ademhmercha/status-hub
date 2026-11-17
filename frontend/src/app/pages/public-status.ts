import { ChangeDetectionStrategy, Component, effect, inject, input, untracked } from '@angular/core';
import { ApiService } from '../core/api.service';
import { duration, pct } from '../core/format';
import { REFRESH_MS, createLoader } from '../core/loader';
import { OverallStatus } from '../core/models';
import { ThemeService } from '../core/theme.service';
import { AvailabilityBar } from '../shared/availability-bar';
import { ErrorBanner } from '../shared/error-banner';
import { StatusDot } from '../shared/status-dot';

const OVERALL: Record<OverallStatus, { text: string; cls: string }> = {
  operational: { text: 'Tous les systèmes sont opérationnels', cls: 'ok' },
  partial_outage: { text: 'Panne partielle', cls: 'warn' },
  major_outage: { text: 'Panne majeure', cls: 'bad' },
  unknown: { text: 'État en cours de collecte', cls: 'none' },
};

/** Public page (no authentication). */
@Component({
  selector: 'app-public-status',
  imports: [StatusDot, AvailabilityBar, ErrorBanner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="container stack">
      <div class="row between">
        <h1>État des services</h1>
        <button type="button" class="btn small" (click)="theme.toggle()">{{ theme.theme() === 'dark' ? 'Clair' : 'Sombre' }}</button>
      </div>

      @if (loader.error(); as e) {
        @if (notFound()) {
          <div class="card"><h2>Page introuvable</h2><p class="muted">Aucune page de statut ne correspond à « {{ slug() }} ».</p></div>
        } @else {
          <app-error-banner [message]="e" (retry)="loader.reload()" />
        }
      }

      @if (loader.data(); as p) {
        <div class="banner" [class]="'banner ' + overall(p.overall).cls" role="status">{{ overall(p.overall).text }}</div>

        @if (p.services.length === 0) {
          <div class="card muted">Aucun service surveillé pour l'instant.</div>
        }
        @for (s of p.services; track s.name) {
          <section class="card stack">
            <div class="row between">
              <div class="row"><app-status-dot [status]="s.status" /><strong>{{ s.name }}</strong></div>
              <span class="muted small">{{ pct(s.availability90d) }} sur 90 jours</span>
            </div>
            <app-availability-bar [days]="s.days" />
            <div class="row between muted small"><span>il y a 90 jours</span><span>aujourd'hui</span></div>
          </section>
        }

        <section class="card">
          <h2>Incidents récents</h2>
          @if (p.incidents.length === 0) {
            <p class="muted">Aucun incident récent.</p>
          } @else {
            <ul class="incidents">
              @for (i of p.incidents; track i.id) {
                <li>
                  <div class="row between">
                    <strong>{{ i.monitorName }}</strong>
                    <span class="badge" [class.bad]="!i.resolvedAt" [class.ok]="!!i.resolvedAt">{{ i.resolvedAt ? 'Résolu' : 'En cours' }}</span>
                  </div>
                  <div class="muted small">{{ i.cause }} · {{ formatDate(i.openedAt) }} · durée {{ duration(i.durationSeconds) }}</div>
                </li>
              }
            </ul>
          }
        </section>
        <p class="muted small">Mis à jour {{ formatDate(p.generatedAt) }} · actualisation automatique toutes les 30 secondes.</p>
      } @else if (loader.loading()) {
        <div class="card muted" role="status">Chargement…</div>
      }
    </main>`,
  styles: `
    .banner { padding: 1rem 1.2rem; border-radius: 10px; font-weight: 650; font-size: 1.1rem; border: 1px solid var(--border); }
    .banner.ok { background: var(--ok-bg); color: var(--ok); border-color: var(--ok); }
    .banner.warn { background: var(--warn-bg); color: var(--warn); border-color: var(--warn); }
    .banner.bad { background: var(--bad-bg); color: var(--bad); border-color: var(--bad); }
    .banner.none { background: var(--surface-2); color: var(--muted); }
    .incidents { list-style: none; margin: 0; padding: 0; display: grid; gap: .8rem; }
  `,
})
export class PublicStatus {
  readonly slug = input.required<string>();

  private readonly api = inject(ApiService);
  protected readonly theme = inject(ThemeService);
  protected readonly loader = createLoader(() => this.api.statusPage(this.slug()), REFRESH_MS, false);

  protected readonly pct = pct;
  protected readonly duration = duration;

  constructor() {
    effect(() => {
      this.slug();
      untracked(() => this.loader.reload());
    });
  }

  protected overall(o: OverallStatus) { return OVERALL[o]; }
  protected notFound(): boolean { return this.loader.errorStatus() === 404; }
  protected formatDate(iso: string): string { return new Date(iso).toLocaleString('fr-FR'); }
}
