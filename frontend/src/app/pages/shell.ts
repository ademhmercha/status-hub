import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { ThemeService } from '../core/theme.service';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="top">
      <div class="container row between">
        <div class="row">
          <a routerLink="/" class="brand">StatusHub</a>
          <nav class="row" aria-label="Navigation principale">
            <a routerLink="/" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">Tableau de bord</a>
            <a routerLink="/alerts" routerLinkActive="on">Alertes</a>
            <a routerLink="/settings" routerLinkActive="on">Paramètres</a>
          </nav>
        </div>
        <div class="row">
          <span class="muted small who">{{ auth.email() }}</span>
          <button type="button" class="btn small" (click)="theme.toggle()" [attr.aria-label]="'Basculer le thème'">
            {{ theme.theme() === 'dark' ? 'Clair' : 'Sombre' }}
          </button>
          <button type="button" class="btn small" (click)="auth.logout()">Déconnexion</button>
        </div>
      </div>
    </header>
    <main class="container"><router-outlet /></main>`,
  styles: `
    .top { background: var(--surface); border-bottom: 1px solid var(--border); }
    .top .container { padding-top: .7rem; padding-bottom: .7rem; }
    .brand { font-weight: 700; color: var(--text); margin-right: .75rem; }
    nav a { color: var(--muted); padding: .2rem .4rem; border-radius: 6px; }
    nav a.on { color: var(--text); background: var(--surface-2); text-decoration: none; }
    @media (max-width: 640px) { .who { display: none; } }
  `,
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly theme = inject(ThemeService);
}
