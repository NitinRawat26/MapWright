import { DatePipe } from '@angular/common';
import { Component, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { catchError, finalize, of } from 'rxjs';
import { Api } from '../core/api';
import { Icon } from '../core/icon';
import { MappingListItem, ProfileSummary } from '../core/models';
import { ago } from '../core/time';
import { UserService } from '../core/user';

@Component({
  selector: 'app-mapping-list',
  imports: [
    DatePipe,
    FormsModule,
    Icon,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    RouterLink,
  ],
  templateUrl: './mapping-list.html',
  styleUrl: './mapping-list.scss',
})
export class MappingList {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  protected readonly user = inject(UserService);

  /** Preselected source profile, from `?source=`. */
  readonly source = input<string>();

  protected readonly mappings = signal<MappingListItem[]>([]);
  protected readonly profiles = signal<ProfileSummary[]>([]);
  /** What a running generation is waiting for; empty when none is running. */
  protected readonly busy = signal<'' | 'playbooks' | 'ai'>('');
  protected readonly ai = toSignal(this.api.ai().pipe(catchError(() => of(null))), { initialValue: null });
  protected readonly request = signal({ source: '', target: '', title: '', id: '', replace: false, useAi: false });

  constructor() {
    this.load();
    this.api.profiles().subscribe((profiles) => {
      this.profiles.set(profiles);
      if (this.source() && profiles.some((p) => p.id === this.source())) {
        this.patch({ source: this.source() ?? '' });
      }
    });
  }

  protected generate(): void {
    const r = this.request();
    this.busy.set(r.useAi && this.ai()?.available ? 'ai' : 'playbooks');
    this.api
      .generateMapping({ source: r.source, target: r.target, title: r.title.trim() || undefined, id: r.id.trim() || undefined, replace: r.replace, useAi: r.useAi })
      .pipe(finalize(() => this.busy.set('')))
      .subscribe({ next: (mapping) => void this.router.navigate(['/mappings', mapping.id]), error: () => undefined });
  }

  protected patch(change: Partial<ReturnType<typeof this.request>>): void {
    this.request.update((r) => ({ ...r, ...change }));
  }

  protected coverage(mapping: MappingListItem): number {
    const s = mapping.summary;
    return s?.totalTargetFields ? Math.round((100 * s.mappedTargetFields) / s.totalTargetFields) : 0;
  }

  protected open(mapping: MappingListItem): void {
    void this.router.navigate(['/mappings', mapping.id]);
  }

  protected scrollToGenerator(event: Event): void {
    event.preventDefault();
    document.getElementById('generate')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  protected ago(iso: string): string {
    return iso ? ago(iso) : '—';
  }

  protected drop(mapping: MappingListItem): void {
    if (confirm(`Delete mapping ${mapping.id}, all its review decisions and its pending AI suggestions?`)) {
      this.api.deleteMapping(mapping.id).subscribe(() => this.load());
    }
  }

  private load(): void {
    this.api.mappings().subscribe((list) => this.mappings.set(list));
  }
}
