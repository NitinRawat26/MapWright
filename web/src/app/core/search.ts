import { Component, ElementRef, HostListener, computed, inject, signal, viewChild } from '@angular/core';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { Router } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';
import { Api } from './api';
import { Icon } from './icon';
import { IconName } from './icons';

interface Hit {
  group: 'Profiles' | 'Mappings' | 'Playbooks';
  icon: IconName;
  label: string;
  detail: string;
  link: string;
}

/** Header search over profiles, mappings and playbooks; Ctrl+K or ⌘K focuses it. */
@Component({
  selector: 'app-search',
  imports: [Icon, MatAutocompleteModule],
  template: `
    <label class="search">
      <app-icon name="search" [size]="18" />
      <input
        #box
        type="search"
        placeholder="Search profiles, mappings, playbooks…"
        aria-label="Search"
        data-testid="search"
        [value]="query()"
        (focus)="load()"
        (input)="query.set(box.value)"
        [matAutocomplete]="results" />
      <kbd>{{ shortcut }}</kbd>
    </label>
    <mat-autocomplete #results="matAutocomplete" (optionSelected)="open($event)" class="search-results">
      @for (group of groups(); track group.name) {
        <mat-optgroup [label]="group.name">
          @for (hit of group.hits; track hit.link) {
            <mat-option [value]="hit" data-testid="search-hit">
              <span class="hit"><app-icon [name]="hit.icon" [size]="18" /><b>{{ hit.label }}</b><span class="muted">{{ hit.detail }}</span></span>
            </mat-option>
          }
        </mat-optgroup>
      }
      @if (query().trim() && !groups().length) {
        <mat-option disabled>No matches</mat-option>
      }
    </mat-autocomplete>
  `,
  styles: `
    :host {
      flex: 1;
      max-width: 520px;
    }
    .search {
      display: flex;
      align-items: center;
      gap: 8px;
      background: var(--mw-bg);
      border: 1px solid var(--mw-line);
      border-radius: 10px;
      padding: 0 12px;
      color: var(--mw-muted);
    }
    input {
      flex: 1;
      border: 0;
      outline: 0;
      background: none;
      font: inherit;
      padding: 9px 0;
      color: var(--mw-ink);
    }
    kbd {
      font: inherit;
      font-size: 11px;
      border: 1px solid var(--mw-line);
      border-radius: 6px;
      padding: 1px 6px;
      background: #fff;
    }
    .hit {
      display: flex;
      align-items: center;
      gap: 8px;
    }
  `,
})
export class Search {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly box = viewChild.required<ElementRef<HTMLInputElement>>('box');

  protected readonly shortcut = /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘K' : 'Ctrl K';
  protected readonly query = signal('');
  private readonly hits = signal<Hit[]>([]);

  protected readonly groups = computed(() => {
    const words = this.query().toLowerCase().split(/\s+/).filter((w) => w);
    if (!words.length) {
      return [];
    }

    const found = this.hits().filter((hit) => words.every((w) => `${hit.label} ${hit.detail}`.toLowerCase().includes(w)));
    return (['Profiles', 'Mappings', 'Playbooks'] as const)
      .map((name) => ({ name, hits: found.filter((h) => h.group === name).slice(0, 6) }))
      .filter((g) => g.hits.length);
  });

  @HostListener('document:keydown', ['$event'])
  protected focus(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.box().nativeElement.focus();
    }
  }

  /** Reloads the lists each time the box gets focus, so new profiles and mappings are found. */
  protected load(): void {
    forkJoin({
      profiles: this.api.profiles().pipe(catchError(() => of([]))),
      mappings: this.api.mappings().pipe(catchError(() => of([]))),
      playbooks: this.api.playbooks().pipe(catchError(() => of([]))),
    }).subscribe(({ profiles, mappings, playbooks }) =>
      this.hits.set([
        ...profiles.map((p): Hit => ({ group: 'Profiles', icon: 'database', label: p.system, detail: `${p.id} · ${p.fieldCount} fields`, link: `/profiles/${encodeURIComponent(p.id)}` })),
        ...mappings.map((m): Hit => ({ group: 'Mappings', icon: 'account_tree', label: `${m.sourceSystem} → ${m.targetSystem}`, detail: m.id, link: `/mappings/${encodeURIComponent(m.id)}` })),
        ...playbooks.map((p): Hit => ({ group: 'Playbooks', icon: 'menu_book', label: p.name, detail: `${p.reference} · ${p.status}`, link: `/playbooks/${p.id}/${p.version}` })),
      ]),
    );
  }

  protected open(event: MatAutocompleteSelectedEvent): void {
    const hit = event.option.value as Hit;
    this.query.set('');
    this.box().nativeElement.value = '';
    this.box().nativeElement.blur();
    void this.router.navigateByUrl(hit.link);
  }
}
