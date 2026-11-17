export type MonitorStatus = 'Unknown' | 'Up' | 'Down' | 'Paused';

export interface Monitor {
  id: string;
  name: string;
  url: string;
  method: string;
  intervalMinutes: number;
  expectedStatus: number;
  keyword: string | null;
  timeoutSeconds: number;
  active: boolean;
  status: MonitorStatus;
  createdAt: string;
  lastCheckedAt: string | null;
  openIncidentId: string | null;
  availability30d: number | null;
}

export interface MonitorInput {
  name: string;
  url: string;
  method: string;
  intervalMinutes: number;
  expectedStatus: number;
  keyword: string | null;
  timeoutSeconds: number;
}

export interface Stats {
  availability24h: number | null;
  availability7d: number | null;
  availability30d: number | null;
  p50Ms24h: number | null;
  p95Ms24h: number | null;
  checks24h: number;
}

export interface LatencyPoint {
  t: string;
  p50: number;
  p95: number;
  total: number;
  failures: number;
}

export interface Day {
  date: string;
  availability: number | null;
  total: number;
}

export interface Incident {
  id: string;
  monitorId: string;
  monitorName: string | null;
  openedAt: string;
  resolvedAt: string | null;
  durationSeconds: number | null;
  cause: string;
}

export interface Alert {
  id: string;
  type: 'email' | 'webhook';
  target: string;
  hasSecret: boolean;
  active: boolean;
  secret?: string | null;
}

export interface AlertInput {
  type: 'email' | 'webhook';
  target: string;
  active?: boolean;
}

export interface Me {
  userId: string;
  email: string;
  slug: string;
}

export type DemoMode = 'ok' | 'slow' | 'down';

export interface DemoState {
  mode: DemoMode;
  targetUrl: string;
}

export type OverallStatus = 'operational' | 'partial_outage' | 'major_outage' | 'unknown';

export interface StatusService {
  name: string;
  status: MonitorStatus;
  availability90d: number | null;
  days: Day[];
}

export interface StatusPage {
  slug: string;
  overall: OverallStatus;
  generatedAt: string;
  services: StatusService[];
  incidents: Incident[];
}
