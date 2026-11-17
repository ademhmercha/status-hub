import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Day } from '../core/models';
import { availabilityClass, pct } from '../core/format';

/** 90-day availability strip: one bar per day, coloured by availability, grey when there is no data. */
@Component({
  selector: 'app-availability-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="bar" role="img" [attr.aria-label]="'Disponibilité sur ' + days().length + ' jours'">
      @for (d of cells(); track d.date) {
        <span class="cell" [class]="d.cls" [title]="d.title"></span>
      }
    </div>`,
  styles: `
    .bar { display: flex; gap: 2px; height: 2rem; }
    .cell { flex: 1; min-width: 2px; border-radius: 2px; background: var(--none); }
    .cell.ok { background: var(--ok); }
    .cell.warn { background: var(--warn); }
    .cell.bad { background: var(--bad); }
  `,
})
export class AvailabilityBar {
  readonly days = input.required<Day[]>();

  protected readonly cells = computed(() =>
    this.days().map((d) => {
      const c = availabilityClass(d.availability);
      return {
        date: d.date,
        cls: `cell ${c}`,
        title: d.availability === null ? `${d.date} : pas de données` : `${d.date} : ${pct(d.availability, 2)} (${d.total} contrôles)`,
      };
    }),
  );
}
