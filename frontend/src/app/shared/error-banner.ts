import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

@Component({
  selector: 'app-error-banner',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="alert-box error row between" role="alert">
      <span>{{ message() }}</span>
      @if (retryable()) { <button type="button" class="btn small" (click)="retry.emit()">Réessayer</button> }
    </div>`,
})
export class ErrorBanner {
  readonly message = input.required<string>();
  readonly retryable = input(true);
  readonly retry = output<void>();
}
