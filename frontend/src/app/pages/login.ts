import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { toApiError } from '../core/loader';
import { ThemeService } from '../core/theme.service';

type Mode = 'login' | 'register' | 'confirm';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="wrap">
      <section class="card box">
        <h1>StatusHub</h1>
        <p class="muted">Surveillance de disponibilité et pages de statut.</p>

        @if (mode() !== 'confirm') {
          <div class="row tabs" role="tablist">
            <button type="button" class="btn small" [class.active]="mode() === 'login'" (click)="mode.set('login')">Connexion</button>
            <button type="button" class="btn small" [class.active]="mode() === 'register'" (click)="mode.set('register')">Inscription</button>
          </div>
        }

        <form class="form" (ngSubmit)="submit()" #f="ngForm">
          <label>Email
            <input type="email" name="email" [(ngModel)]="email" required autocomplete="email" />
          </label>
          @if (mode() === 'confirm') {
            <label>Code reçu par email
              <input name="code" [(ngModel)]="code" required autocomplete="one-time-code" />
            </label>
          } @else {
            <label>Mot de passe
              <input type="password" name="password" [(ngModel)]="password" [required]="!auth.devMode" minlength="8"
                [autocomplete]="mode() === 'login' ? 'current-password' : 'new-password'" />
              @if (auth.devMode) { <span class="muted small">Mode local : le mot de passe n'est pas vérifié.</span> }
            </label>
          }

          @if (error()) { <div class="alert-box error" role="alert">{{ error() }}</div> }
          @if (info()) { <div class="alert-box success" role="status">{{ info() }}</div> }

          <button class="btn primary" type="submit" [disabled]="busy() || f.invalid">
            {{ mode() === 'login' ? 'Se connecter' : mode() === 'register' ? 'Créer mon compte' : 'Confirmer' }}
          </button>
        </form>

        @if (auth.googleUrl; as g) {
          <p><a class="btn" [href]="g">Continuer avec Google</a></p>
        }
      </section>
      <button type="button" class="btn small theme" (click)="theme.toggle()">Thème : {{ theme.theme() === 'dark' ? 'sombre' : 'clair' }}</button>
    </main>`,
  styles: `
    .wrap { min-height: 100vh; display: grid; place-items: center; padding: 1rem; gap: 1rem; align-content: center; }
    .box { width: min(420px, 100%); }
    .tabs { margin: 1rem 0; }
    .theme { justify-self: center; }
  `,
})
export class LoginPage implements OnInit {
  protected readonly auth = inject(AuthService);
  protected readonly theme = inject(ThemeService);
  private readonly router = inject(Router);

  protected readonly mode = signal<Mode>('login');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly info = signal<string | null>(null);
  protected email = '';
  protected password = '';
  protected code = '';

  ngOnInit(): void {
    // Return from the Cognito hosted UI (Google): the ID token is in the URL fragment.
    const token = new URLSearchParams(window.location.hash.replace(/^#/, '')).get('id_token');
    if (token) {
      this.auth.setToken(token);
      history.replaceState(null, '', window.location.pathname);
      void this.router.navigate(['/']);
    } else if (this.auth.isAuthenticated()) {
      void this.router.navigate(['/']);
    }
  }

  async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.info.set(null);
    try {
      if (this.mode() === 'login') {
        await this.auth.login(this.email, this.password);
        await this.router.navigate(['/']);
      } else if (this.mode() === 'register') {
        const r = await this.auth.register(this.email, this.password);
        if (r === 'signed-in') await this.router.navigate(['/']);
        else {
          this.mode.set('confirm');
          this.info.set('Un code de confirmation vous a été envoyé par email.');
        }
      } else {
        await this.auth.confirm(this.email, this.code);
        this.mode.set('login');
        this.info.set('Compte confirmé. Vous pouvez vous connecter.');
      }
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.busy.set(false);
    }
  }

  private message(e: unknown): string {
    const body = (e as { error?: { message?: string; __type?: string } })?.error;
    return body?.message ?? toApiError(e).message;
  }
}
