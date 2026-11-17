import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { toApiError } from '../core/loader';
import { Monitor, MonitorInput } from '../core/models';

@Component({
  selector: 'app-monitor-form',
  imports: [FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="stack">
      <a routerLink="/" class="small">← Tableau de bord</a>
      <h1>{{ id() ? 'Modifier le moniteur' : 'Nouveau moniteur' }}</h1>

      @if (loadError()) { <div class="alert-box error" role="alert">{{ loadError() }}</div> }

      <form class="form card" (ngSubmit)="submit()" #f="ngForm" novalidate>
        <label>Nom
          <input name="name" [(ngModel)]="model.name" required maxlength="100" placeholder="Mon site" />
          @if (errors()['name']) { <span class="field-error" role="alert">{{ errors()['name'] }}</span> }
        </label>
        <label>URL à surveiller
          <input name="url" type="url" [(ngModel)]="model.url" required placeholder="https://exemple.com/health" />
          <span class="muted small">http ou https uniquement ; les adresses privées et localhost sont refusées.</span>
          @if (errors()['url']) { <span class="field-error" role="alert">{{ errors()['url'] }}</span> }
        </label>
        <div class="form-grid">
          <label>Méthode
            <select name="method" [(ngModel)]="model.method"><option>GET</option><option>HEAD</option></select>
            @if (errors()['method']) { <span class="field-error" role="alert">{{ errors()['method'] }}</span> }
          </label>
          <label>Intervalle
            <select name="interval" [(ngModel)]="model.intervalMinutes">
              @for (i of [1, 2, 3, 4, 5]; track i) { <option [ngValue]="i">{{ i }} min</option> }
            </select>
            @if (errors()['intervalMinutes']) { <span class="field-error" role="alert">{{ errors()['intervalMinutes'] }}</span> }
          </label>
          <label>Code HTTP attendu
            <input name="status" type="number" [(ngModel)]="model.expectedStatus" min="100" max="599" />
            @if (errors()['expectedStatus']) { <span class="field-error" role="alert">{{ errors()['expectedStatus'] }}</span> }
          </label>
          <label>Timeout (secondes)
            <input name="timeout" type="number" [(ngModel)]="model.timeoutSeconds" min="1" max="30" />
            @if (errors()['timeoutSeconds']) { <span class="field-error" role="alert">{{ errors()['timeoutSeconds'] }}</span> }
          </label>
        </div>
        <label>Mot-clé attendu (optionnel)
          <input name="keyword" [(ngModel)]="keyword" maxlength="200" placeholder="ex. OK" />
          @if (errors()['keyword']) { <span class="field-error" role="alert">{{ errors()['keyword'] }}</span> }
        </label>

        @if (formError()) { <div class="alert-box error" role="alert">{{ formError() }}</div> }

        <div class="row">
          <button class="btn primary" type="submit" [disabled]="busy()">{{ id() ? 'Enregistrer' : 'Créer le moniteur' }}</button>
          <a class="btn" routerLink="/">Annuler</a>
        </div>
      </form>
    </div>`,
})
export class MonitorForm {
  /** Present on /monitors/:id/edit. */
  readonly id = input<string>();

  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  protected readonly busy = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly loadError = signal<string | null>(null);

  protected model = { name: '', url: '', method: 'GET', intervalMinutes: 1, expectedStatus: 200, timeoutSeconds: 10 };
  protected keyword = '';

  constructor() {
    effect(() => {
      const id = this.id();
      if (!id) return;
      untracked(() => this.api.monitor(id).subscribe({
        next: (m) => this.fill(m),
        error: (e) => this.loadError.set(toApiError(e).message),
      }));
    });
  }

  private fill(m: Monitor): void {
    this.model = {
      name: m.name, url: m.url, method: m.method, intervalMinutes: m.intervalMinutes,
      expectedStatus: m.expectedStatus, timeoutSeconds: m.timeoutSeconds,
    };
    this.keyword = m.keyword ?? '';
  }

  protected submit(): void {
    const local: Record<string, string> = {};
    if (!this.model.name.trim()) local['name'] = 'Le nom est requis.';
    if (!/^https?:\/\//i.test(this.model.url.trim())) local['url'] = "L'URL doit commencer par http:// ou https://.";
    this.errors.set(local);
    if (Object.keys(local).length > 0) return;

    const body: MonitorInput = { ...this.model, name: this.model.name.trim(), url: this.model.url.trim(), keyword: this.keyword.trim() || null };
    this.busy.set(true);
    this.formError.set(null);
    const id = this.id();
    (id ? this.api.updateMonitor(id, body) : this.api.createMonitor(body)).subscribe({
      next: (m) => {
        this.busy.set(false);
        void this.router.navigate(['/monitors', m.id]);
      },
      error: (e) => {
        const err = toApiError(e);
        this.busy.set(false);
        this.errors.set(err.fields);
        this.formError.set(Object.keys(err.fields).length === 0 ? err.message : null);
      },
    });
  }
}
