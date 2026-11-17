import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { LatencyPoint } from '../core/models';

const W = 640;
const H = 220;
const PAD = { l: 44, r: 10, t: 10, b: 24 };

/** Dependency-free SVG chart of p50 / p95 latency. */
@Component({
  selector: 'app-latency-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (model(); as m) {
      <svg [attr.viewBox]="'0 0 ' + w + ' ' + h" role="img" aria-label="Latence p50 et p95" preserveAspectRatio="none">
        @for (t of m.yTicks; track t.label) {
          <line [attr.x1]="pad.l" [attr.x2]="w - pad.r" [attr.y1]="t.y" [attr.y2]="t.y" class="grid" />
          <text [attr.x]="pad.l - 6" [attr.y]="t.y + 4" class="axis" text-anchor="end">{{ t.label }}</text>
        }
        @for (t of m.xTicks; track t.x) {
          <text [attr.x]="t.x" [attr.y]="h - 6" class="axis" text-anchor="middle">{{ t.label }}</text>
        }
        <path class="line p95" [attr.d]="m.p95" />
        <path class="line p50" [attr.d]="m.p50" />
      </svg>
      <div class="legend"><span class="k p50"></span> p50 <span class="k p95"></span> p95</div>
    } @else {
      <p class="empty muted">Aucune donnée de latence pour cette période.</p>
    }`,
  styles: `
    svg { width: 100%; height: 220px; display: block; }
    .grid { stroke: var(--border); stroke-width: 1; }
    .axis { fill: var(--muted); font-size: 11px; }
    .line { fill: none; stroke-width: 2; vector-effect: non-scaling-stroke; stroke-linejoin: round; }
    .p50 { stroke: var(--primary); }
    .p95 { stroke: var(--warn); }
    .legend { display: flex; gap: .5rem; align-items: center; font-size: .82rem; color: var(--muted); }
    .k { width: .9rem; height: 3px; display: inline-block; border-radius: 2px; margin-left: .6rem; }
    .k.p50 { background: var(--primary); }
    .k.p95 { background: var(--warn); }
    .empty { padding: 2rem 0; text-align: center; }
  `,
})
export class LatencyChart {
  readonly points = input.required<LatencyPoint[]>();
  readonly range = input<'24h' | '7d' | '30d'>('24h');

  protected readonly w = W;
  protected readonly h = H;
  protected readonly pad = PAD;

  protected readonly model = computed(() => {
    const pts = this.points().filter((p) => p.total - p.failures > 0);
    if (pts.length === 0) return null;
    const times = pts.map((p) => new Date(p.t).getTime());
    const t0 = Math.min(...times);
    const t1 = Math.max(...times);
    const top = Math.max(50, ...pts.map((p) => p.p95));
    const max = top * 1.1;
    const x = (t: number) => PAD.l + (t1 === t0 ? (W - PAD.l - PAD.r) / 2 : ((t - t0) / (t1 - t0)) * (W - PAD.l - PAD.r));
    const y = (v: number) => H - PAD.b - (v / max) * (H - PAD.t - PAD.b);
    const path = (sel: (p: LatencyPoint) => number) =>
      pts.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(times[i]).toFixed(1)},${y(sel(p)).toFixed(1)}`).join(' ');
    const fmt = (t: number) =>
      this.range() === '24h'
        ? new Date(t).toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' })
        : new Date(t).toLocaleDateString('fr-FR', { day: '2-digit', month: '2-digit' });
    return {
      p50: path((p) => p.p50),
      p95: path((p) => p.p95),
      yTicks: [0, top / 2, top].map((v) => ({ y: y(v), label: `${Math.round(v)} ms` })),
      xTicks: [t0, (t0 + t1) / 2, t1].map((t) => ({ x: x(t), label: fmt(t) })),
    };
  });
}
