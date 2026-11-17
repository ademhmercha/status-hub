import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { availabilityClass, pct, relativeTime } from '../core/format';
import { createLoader } from '../core/loader';
import { ErrorBanner } from '../shared/error-banner';
import { StatusDot } from '../shared/status-dot';
import { DemoPanel } from './demo-panel';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, StatusDot, ErrorBanner, DemoPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="stack">
      <div class="row between">
        <div>
          <h1>Tableau de bord</h1>
          @if (monitors(); as list) {
            <p class="muted">{{ summary() }}</p>
          }
        </div>
        <a routerLink="/monitors/new" class="btn primary">Ajouter un moniteur</a>
      </div>

      @if (loader.error(); as e) { <app-error-banner [message]="e" (retry)="loader.reload()" /> }

      @if (loader.loading() && !monitors()) {
        <div class="card muted" role="status">Chargement…</div>
      } @else if (monitors(); as list) {
        @if (list.length === 0) {
          <div class="card empty">
            <h2>Aucun moniteur pour l'instant</h2>
            <p class="muted">Ajoutez l'URL d'un site ou d'une API : elle sera testée chaque minute.
              Vous pouvez aussi ajouter le moniteur de démo ci-dessous pour voir le système réagir.</p>
            <a routerLink="/monitors/new" class="btn primary">Ajouter mon premier moniteur</a>
          </div>
        } @else {
          <div class="card table-wrap">
            <table>
              <thead><tr><th>État</th><th>Moniteur</th><th>Disponibilité 30 j</th><th>Dernier contrôle</th></tr></thead>
              <tbody>
                @for (m of list; track m.id) {
                  <tr>
                    <td><app-status-dot [status]="m.status" [showLabel]="true" /></td>
                    <td>
                      <a [routerLink]="['/monitors', m.id]"><strong>{{ m.name }}</strong></a>
                      <div class="muted small mono">{{ m.url }}</div>
                    </td>
                    <td><span class="badge" [class]="'badge ' + avail(m.availability30d)">{{ pct(m.availability30d) }}</span></td>
                    <td class="muted">{{ relative(m.lastCheckedAt) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      }

      <app-demo-panel [monitors]="monitors() ?? []" (created)="onCreated($event.id)" />
      <p class="muted small">Actualisation automatique toutes les 30 secondes.</p>
    </div>`,
})
export class Dashboard {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  protected readonly loader = createLoader(() => this.api.monitors());
  protected readonly monitors = computed(() => this.loader.data());

  protected readonly pct = pct;
  protected readonly avail = availabilityClass;
  protected relative(iso: string | null): string { return relativeTime(iso); }

  protected readonly summary = computed(() => {
    const list = this.monitors() ?? [];
    const down = list.filter((m) => m.status === 'Down').length;
    const up = list.filter((m) => m.status === 'Up').length;
    return list.length === 0 ? '' : `${up} en ligne · ${down} en panne · ${list.length} au total (20 max)`;
  });

  protected onCreated(id: string): void {
    void this.router.navigate(['/monitors', id]);
  }
}
