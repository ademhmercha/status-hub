import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { createLoader, toApiError } from '../core/loader';
import { ErrorBanner } from '../shared/error-banner';

@Component({
  selector: 'app-settings',
  imports: [FormsModule, ErrorBanner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="stack">
      <h1>Paramètres</h1>
      @if (me.error(); as e) { <app-error-banner [message]="e" (retry)="me.reload()" /> }

      <form class="card form" (ngSubmit)="save()" #f="ngForm">
        <h2>Page de statut publique</h2>
        <p class="muted">Cette page est visible par tous, sans connexion. Elle affiche l'état de vos moniteurs actifs (sans leur URL).</p>
        <label>Identifiant (slug)
          <input name="slug" [(ngModel)]="slug" required minlength="3" maxlength="30" pattern="[a-z0-9]([a-z0-9-]*[a-z0-9])?" />
          @if (error()) { <span class="field-error" role="alert">{{ error() }}</span> }
        </label>
        <p class="small">Adresse : <a [href]="publicUrl()" target="_blank" rel="noopener" class="mono">{{ publicUrl() }}</a></p>
        @if (saved()) { <div class="alert-box success" role="status">Slug enregistré.</div> }
        <div><button class="btn primary" type="submit" [disabled]="busy() || f.invalid">Enregistrer</button></div>
      </form>
    </div>`,
})
export class Settings {
  private readonly api = inject(ApiService);
  protected readonly me = createLoader(() => this.api.me(), null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly saved = signal(false);
  protected slug = '';

  constructor() {
    effect(() => {
      const m = this.me.data();
      if (m && !this.slug) this.slug = m.slug;
    });
  }

  protected publicUrl(): string {
    return `${window.location.origin}/status/${this.slug}`;
  }

  protected save(): void {
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(false);
    this.api.setSlug(this.slug.trim().toLowerCase()).subscribe({
      next: (m) => { this.busy.set(false); this.slug = m.slug; this.saved.set(true); },
      error: (e) => { this.busy.set(false); this.error.set(toApiError(e).message); },
    });
  }
}
