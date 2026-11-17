import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from './config';
import {
  Alert, AlertInput, DemoMode, DemoState, Day, Incident, LatencyPoint, Me, Monitor, MonitorInput, Stats, StatusPage,
} from './models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly cfg = inject(ConfigService);

  private url(path: string): string {
    return `${this.cfg.config.apiBaseUrl}${path}`;
  }

  me(): Observable<Me> { return this.http.get<Me>(this.url('/api/v1/me')); }
  setSlug(slug: string): Observable<Me> { return this.http.put<Me>(this.url('/api/v1/me/slug'), { slug }); }

  monitors(): Observable<Monitor[]> { return this.http.get<Monitor[]>(this.url('/api/v1/monitors')); }
  monitor(id: string): Observable<Monitor> { return this.http.get<Monitor>(this.url(`/api/v1/monitors/${id}`)); }
  createMonitor(m: MonitorInput): Observable<Monitor> { return this.http.post<Monitor>(this.url('/api/v1/monitors'), m); }
  updateMonitor(id: string, m: MonitorInput): Observable<Monitor> { return this.http.put<Monitor>(this.url(`/api/v1/monitors/${id}`), m); }
  deleteMonitor(id: string): Observable<void> { return this.http.delete<void>(this.url(`/api/v1/monitors/${id}`)); }
  pause(id: string): Observable<Monitor> { return this.http.post<Monitor>(this.url(`/api/v1/monitors/${id}/pause`), {}); }
  resume(id: string): Observable<Monitor> { return this.http.post<Monitor>(this.url(`/api/v1/monitors/${id}/resume`), {}); }
  stats(id: string): Observable<Stats> { return this.http.get<Stats>(this.url(`/api/v1/monitors/${id}/stats`)); }
  latency(id: string, range: '24h' | '7d' | '30d'): Observable<LatencyPoint[]> {
    return this.http.get<LatencyPoint[]>(this.url(`/api/v1/monitors/${id}/latency`), { params: new HttpParams().set('range', range) });
  }
  availability(id: string, days = 90): Observable<Day[]> {
    return this.http.get<Day[]>(this.url(`/api/v1/monitors/${id}/availability`), { params: new HttpParams().set('days', days) });
  }
  incidents(id: string): Observable<Incident[]> { return this.http.get<Incident[]>(this.url(`/api/v1/monitors/${id}/incidents`)); }

  alerts(): Observable<Alert[]> { return this.http.get<Alert[]>(this.url('/api/v1/alerts')); }
  createAlert(a: AlertInput): Observable<Alert> { return this.http.post<Alert>(this.url('/api/v1/alerts'), a); }
  updateAlert(id: string, a: AlertInput): Observable<Alert> { return this.http.put<Alert>(this.url(`/api/v1/alerts/${id}`), a); }
  deleteAlert(id: string): Observable<void> { return this.http.delete<void>(this.url(`/api/v1/alerts/${id}`)); }
  testAlert(id: string): Observable<{ sent: boolean }> { return this.http.post<{ sent: boolean }>(this.url(`/api/v1/alerts/${id}/test`), {}); }

  demo(): Observable<DemoState> { return this.http.get<DemoState>(this.url('/api/v1/demo')); }
  setDemo(mode: DemoMode): Observable<DemoState> { return this.http.put<DemoState>(this.url('/api/v1/demo'), { mode }); }

  statusPage(slug: string): Observable<StatusPage> { return this.http.get<StatusPage>(this.url(`/api/v1/status/${encodeURIComponent(slug)}`)); }
}
