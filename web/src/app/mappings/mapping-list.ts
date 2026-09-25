import { DatePipe } from '@angular/common';
import { Component, inject, input, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { catchError, finalize, of } from 'rxjs';
import { Api } from '../core/api';
import { MappingListItem, ProfileSummary } from '../core/models';
import { UserService } from '../core/user';

@Component({
  selector: 'app-mapping-list',
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, RouterLink],
  templateUrl: './mapping-list.html',
})
export class MappingList {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  protected readonly user = inject(UserService);

  /** Preselected source profile, from `?source=`. */
  readonly source = input<string>();

  protected readonly mappings = signal<MappingListItem[]>([]);
  protected readonly profiles = signal<ProfileSummary[]>([]);
  protected readonly busy = signal(false);
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
    this.busy.set(true);
    this.api
      .generateMapping({ source: r.source, target: r.target, title: r.title.trim() || undefined, id: r.id.trim() || undefined, replace: r.replace, useAi: r.useAi })
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({ next: (mapping) => void this.router.navigate(['/mappings', mapping.id]), error: () => undefined });
  }

  protected patch(change: Partial<ReturnType<typeof this.request>>): void {
    this.request.update((r) => ({ ...r, ...change }));
  }

  protected drop(mapping: MappingListItem): void {
    if (confirm(`Delete mapping ${mapping.id} and all its review decisions?`)) {
      this.api.deleteMapping(mapping.id).subscribe(() => this.load());
    }
  }

  private load(): void {
    this.api.mappings().subscribe((list) => this.mappings.set(list));
  }
}
