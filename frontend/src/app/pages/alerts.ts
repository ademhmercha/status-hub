import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import { ApiService } from '../core/api.service';
import { createLoader, toApiError } from '../core/loader';
import { Alert } from '../core/models';
import { ErrorBanner } from '../shared/error-banner';

@Component({
  selector: 'app-alerts',
  imports: [FormsModule, ErrorBanner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="stack">
      <div>
        <h1>Alertes</h1>
        <p class="muted">Les alertes sont envoyées à l'ouverture et à la résolution d'un incident.
          Les webhooks sont signés (HMAC-SHA256, en-têtes <span class="mono">X-StatusHub-Signature</span> et <span class="mono">X-StatusHub-Timestamp</span>).</p>
      </div>

      @if (loader.error(); as e) { <app-error-banner [message]="e" (retry)="loader.reload()" /> }
      @if (message(); as m) { <div class="alert-box" [class.error]="isError()" [class.success]="!isError()" role="status">{{ m }}</div> }
      @if (secret(); as s) {
        <div class="alert-box success" role="status">
          Secret du webhook (affiché une seule fois) : <span class="mono">{{ s }}</span>
        </div>
      }

      @if (loader.loading() && !loader.data()) {
        <div class="card muted" role="status">Chargement…</div>
      } @else if (loader.data(); as list) {
        @if (list.length === 0) {
          <div class="card muted">Aucune alerte configurée : vous ne serez pas prévenu en cas de panne.</div>
        } @else {
          <div class="card table-wrap"><table>
            <thead><tr><th>Type</th><th>Destination</th><th>État</th><th></th></tr></thead>
            <tbody>
              @for (a of list; track a.id) {
                <tr>
                  <td>{{ a.type === 'email' ? 'Email' : 'Webhook' }}</td>
                  <td class="mono">{{ a.target }}</td>
                  <td><span class="badge" [class.ok]="a.active" [class.none]="!a.active">{{ a.active ? 'Active' : 'Désactivée' }}</span></td>
                  <td class="row">
                    <button type="button" class="btn small" [disabled]="busy()" (click)="test(a)">Envoyer un test</button>
                    <button type="button" class="btn small" [disabled]="busy()" (click)="toggle(a)">{{ a.active ? 'Désactiver' : 'Activer' }}</button>
                    <button type="button" class="btn small danger" [disabled]="busy()" (click)="remove(a)">Supprimer</button>
                  </td>
                </tr>
              }
            </tbody>
          </table></div>
        }
      }

      <form class="card form" (ngSubmit)="add()" #f="ngForm">
        <h2>Ajouter une alerte</h2>
        <label>Type
          <select name="type" [(ngModel)]="type"><option value="email">Email</option><option value="webhook">Webhook</option></select>
        </label>
        <label>{{ type === 'email' ? 'Adresse email' : 'URL du webhook' }}
          <input name="target" [(ngModel)]="target" required [type]="type === 'email' ? 'email' : 'url'"
            [placeholder]="type === 'email' ? 'vous@exemple.com' : 'https://exemple.com/hook'" />
          @if (fieldError()) { <span class="field-error" role="alert">{{ fieldError() }}</span> }
        </label>
        <div><button class="btn primary" type="submit" [disabled]="busy() || f.invalid">Ajouter</button></div>
      </form>
    </div>`,
})
export class Alerts {
  private readonly api = inject(ApiService);
  protected readonly loader = createLoader(() => this.api.alerts());
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly isError = signal(false);
  protected readonly secret = signal<string | null>(null);
  protected readonly fieldError = signal<string | null>(null);
  protected type: 'email' | 'webhook' = 'email';
  protected target = '';

  protected add(): void {
    this.busy.set(true);
    this.fieldError.set(null);
    this.api.createAlert({ type: this.type, target: this.target.trim() }).subscribe({
      next: (a) => {
        this.busy.set(false);
        this.target = '';
        this.secret.set(a.secret ?? null);
        this.say('Alerte ajoutée.');
        this.loader.reload(true);
      },
      error: (e) => {
        const err = toApiError(e);
        this.busy.set(false);
        this.fieldError.set(err.fields['target'] ?? null);
        if (!err.fields['target']) this.say(err.message, true);
      },
    });
  }

  protected test(a: Alert): void {
    this.act(this.api.testAlert(a.id), `Test envoyé à ${a.target}.`);
  }

  protected toggle(a: Alert): void {
    this.act(this.api.updateAlert(a.id, { type: a.type, target: a.target, active: !a.active }), a.active ? 'Alerte désactivée.' : 'Alerte activée.');
  }

  protected remove(a: Alert): void {
    if (confirm(`Supprimer l'alerte ${a.target} ?`)) this.act(this.api.deleteAlert(a.id), 'Alerte supprimée.');
  }

  private act(obs: Observable<unknown>, success: string): void {
    this.busy.set(true);
    obs.subscribe({
      next: () => { this.busy.set(false); this.say(success); this.loader.reload(true); },
      error: (e) => { this.busy.set(false); this.say(toApiError(e).message, true); },
    });
  }

  private say(text: string, error = false): void {
    this.message.set(text);
    this.isError.set(error);
  }
}
