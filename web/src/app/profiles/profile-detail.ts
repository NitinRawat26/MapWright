import { DatePipe, UpperCasePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink } from '@angular/router';
import { catchError, finalize, of } from 'rxjs';
import { Api } from '../core/api';
import { DetectResponse, SystemProfile } from '../core/models';
import { UserService } from '../core/user';

@Component({
  selector: 'app-profile-detail',
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, UpperCasePipe, MatFormFieldModule, MatInputModule, MatTabsModule, RouterLink],
  templateUrl: './profile-detail.html',
})
export class ProfileDetail {
  private readonly api = inject(Api);
  protected readonly user = inject(UserService);

  readonly id = input.required<string>();

  protected readonly profile = signal<SystemProfile | null>(null);
  protected readonly detection = signal<DetectResponse | null>(null);
  protected readonly busy = signal(false);
  protected readonly useAi = signal(false);
  protected readonly search = signal('');
  protected readonly ai = toSignal(this.api.ai().pipe(catchError(() => of(null))), { initialValue: null });

  protected readonly fields = computed(() => {
    const text = this.search().trim().toLowerCase();
    const fields = this.profile()?.fields ?? [];
    return text ? fields.filter((f) => f.path.toLowerCase().includes(text) || (f.description ?? '').toLowerCase().includes(text)) : fields;
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => {
        this.detection.set(null);
        this.api.profile(id).subscribe((profile) => this.profile.set(profile));
        this.api.detection(id).subscribe({ next: (saved) => this.detection.set(saved), error: () => undefined });
      });
    });
  }

  protected detect(): void {
    this.busy.set(true);
    this.api
      .detect(this.id(), this.useAi())
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({ next: (result) => this.detection.set(result), error: () => undefined });
  }
}
