import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { ApiService } from '../core/api.service';
import { createLoader, toApiError } from '../core/loader';
import { DemoMode, Monitor } from '../core/models';
import { ErrorBanner } from '../shared/error-banner';

/** Drives the built-in demo target so the whole chain can be watched live: incident, alert, recovery. */
@Component({
  selector: 'app-demo-panel',
  imports: [ErrorBanner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card stack" aria-labelledby="demo-title">
      <div class="row between">
        <h2 id="demo-title">Panneau de démonstration</h2>
        @if (state.data(); as d) {
          <span class="badge" [class.ok]="d.mode === 'ok'" [class.warn]="d.mode === 'slow'" [class.bad]="d.mode === 'down'">
            Cible : {{ d.mode === 'ok' ? 'en ligne' : d.mode === 'slow' ? 'lente' : 'en panne' }}
          </span>
        }
      </div>
      <p class="muted small">
        Simulez une panne de la cible de démo : l'incident s'ouvre après 2 échecs consécutifs, l'alerte part par email,
        puis l'incident se ferme après 2 succès une fois la cible rétablie.
      </p>
      @if (error() ?? state.error(); as e) { <app-error-banner [message]="e" (retry)="state.reload()" /> }
      <div class="row">
        <button type="button" class="btn danger" [disabled]="busy()" (click)="set('down')">Simuler une panne</button>
        <button type="button" class="btn" [disabled]="busy()" (click)="set('slow')">Ralentir</button>
        <button type="button" class="btn primary" [disabled]="busy()" (click)="set('ok')">Rétablir</button>
        <button type="button" class="btn" [disabled]="busy() || hasDemoMonitor()" (click)="addMonitor()">
          {{ hasDemoMonitor() ? 'Moniteur de démo déjà ajouté' : 'Ajouter un moniteur de démo' }}
        </button>
      </div>
    </section>`,
})
export class DemoPanel {
  private readonly api = inject(ApiService);
  protected readonly state = createLoader(() => this.api.demo(), 10_000);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  readonly monitors = input<Monitor[]>([]);
  readonly created = output<Monitor>();

  protected hasDemoMonitor(): boolean {
    const url = this.state.data()?.targetUrl;
    return !!url && this.monitors().some((m) => m.url === url);
  }

  protected set(mode: DemoMode): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.setDemo(mode).subscribe({
      next: (d) => {
        this.state.data.set(d);
        this.busy.set(false);
      },
      error: (e) => {
        this.error.set(toApiError(e).message);
        this.busy.set(false);
      },
    });
  }

  protected addMonitor(): void {
    const url = this.state.data()?.targetUrl;
    if (!url) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.createMonitor({
      name: 'Cible de démo', url, method: 'GET', intervalMinutes: 1, expectedStatus: 200, keyword: null, timeoutSeconds: 5,
    }).subscribe({
      next: (m) => {
        this.busy.set(false);
        this.created.emit(m);
      },
      error: (e) => {
        this.error.set(toApiError(e).message);
        this.busy.set(false);
      },
    });
  }
}
