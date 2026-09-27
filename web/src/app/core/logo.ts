import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The MapWright mark: a wright's square framing a source node routed onto a target node.
 * Drawn inline so it takes the surrounding colour scheme; `tile` adds the brand gradient tile behind it
 * (the same drawing as public/favicon.svg, kept in step by hand).
 */
@Component({
  selector: 'app-logo',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg viewBox="0 0 64 64" [attr.width]="size()" [attr.height]="size()" role="img" aria-label="MapWright">
      <defs>
        <linearGradient id="mw-tile" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stop-color="#6366f1" />
          <stop offset="1" stop-color="#a855f7" />
        </linearGradient>
        <linearGradient id="mw-route" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stop-color="#c7d2fe" />
          <stop offset="1" stop-color="#ffffff" />
        </linearGradient>
      </defs>
      @if (tile()) {
        <rect width="64" height="64" rx="16" fill="url(#mw-tile)" />
      }
      <g fill="none" stroke-linecap="round" stroke-linejoin="round">
        <path class="square" d="M17 13v34h34" stroke-width="5" />
        <path class="route" d="M23 22c14 0 12 20 26 20" stroke-width="4.5" />
        <circle class="src" cx="22" cy="22" r="5.5" />
        <path class="tgt" d="M49 35.5 55.5 42 49 48.5 42.5 42Z" stroke-width="3" />
      </g>
    </svg>
  `,
  styles: `
    :host {
      display: inline-flex;
      flex: none;
      line-height: 0;
    }
    .square {
      stroke: currentColor;
      opacity: 0.55;
    }
    .route {
      stroke: currentColor;
    }
    .src {
      fill: currentColor;
    }
    .tgt {
      stroke: currentColor;
      fill: transparent;
    }
    :host(.tile) .square {
      stroke: #fff;
      opacity: 0.6;
    }
    :host(.tile) .route {
      stroke: url(#mw-route);
    }
    :host(.tile) .src,
    :host(.tile) .tgt {
      stroke: #fff;
      fill: #fff;
    }
    :host(.tile) .tgt {
      fill: rgba(255, 255, 255, 0.35);
    }
  `,
  host: { '[class.tile]': 'tile()' },
})
export class Logo {
  readonly size = input(34);
  readonly tile = input(false);
}
