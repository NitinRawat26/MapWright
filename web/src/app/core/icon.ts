import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { IconName, icons } from './icons';

/** An inline Material Symbols icon that takes the text colour; decorative unless given a label. */
@Component({
  selector: 'app-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<svg viewBox="0 -960 960 960" [attr.width]="size()" [attr.height]="size()" [attr.aria-hidden]="label() ? null : 'true'" [attr.aria-label]="label()" [attr.role]="label() ? 'img' : null"><path [attr.d]="path()" /></svg>`,
  styles: `
    :host {
      display: inline-flex;
      flex: none;
      line-height: 0;
    }
    svg {
      fill: currentColor;
    }
  `,
})
export class Icon {
  readonly name = input.required<IconName>();
  readonly size = input(20);
  readonly label = input<string | null>(null);
  protected readonly path = computed(() => icons[this.name()]);
}
