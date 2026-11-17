import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.LoginPage), title: 'Connexion · StatusHub' },
  { path: 'status/:slug', loadComponent: () => import('./pages/public-status').then((m) => m.PublicStatus), title: 'État des services' },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./pages/dashboard').then((m) => m.Dashboard), title: 'Tableau de bord · StatusHub' },
      { path: 'monitors/new', loadComponent: () => import('./pages/monitor-form').then((m) => m.MonitorForm), title: 'Nouveau moniteur · StatusHub' },
      { path: 'monitors/:id/edit', loadComponent: () => import('./pages/monitor-form').then((m) => m.MonitorForm), title: 'Modifier le moniteur · StatusHub' },
      { path: 'monitors/:id', loadComponent: () => import('./pages/monitor-detail').then((m) => m.MonitorDetail), title: 'Moniteur · StatusHub' },
      { path: 'alerts', loadComponent: () => import('./pages/alerts').then((m) => m.Alerts), title: 'Alertes · StatusHub' },
      { path: 'settings', loadComponent: () => import('./pages/settings').then((m) => m.Settings), title: 'Paramètres · StatusHub' },
    ],
  },
  { path: '**', redirectTo: '' },
];
