import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MonitorStatus } from '../core/models';

const LABELS: Record<MonitorStatus, string> = {
  Up: 'En ligne',
  Down: 'En panne',
  Paused: 'En pause',
  Unknown: 'En attente',
};

@Component({
  selector: 'app-status-dot',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="dot" [class]="cls()" role="img" [attr.aria-label]="label()" [title]="label()"></span>
    @if (showLabel()) { <span class="lbl">{{ label() }}</span> }`,
  styles: `
    :host { display: inline-flex; align-items: center; gap: .45rem; }
    .dot { width: .7rem; height: .7rem; border-radius: 50%; background: var(--none); flex: none; }
    .dot.up { background: var(--ok); box-shadow: 0 0 0 3px var(--ok-bg); }
    .dot.down { background: var(--bad); box-shadow: 0 0 0 3px var(--bad-bg); }
    .dot.unknown { background: var(--warn); }
    .lbl { font-size: .85rem; color: var(--muted); }
  `,
})
export class StatusDot {
  readonly status = input.required<MonitorStatus>();
  readonly showLabel = input(false);
  protected readonly label = computed(() => LABELS[this.status()]);
  protected readonly cls = computed(() => `dot ${this.status().toLowerCase()}`);
}
