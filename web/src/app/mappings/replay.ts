import { DatePipe, UpperCasePipe } from '@angular/common';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { finalize } from 'rxjs';
import { Api } from '../core/api';
import { MappingDocument, ProfileSummary, ReplaySample, ValidationRun } from '../core/models';
import { UserService } from '../core/user';

export function counts(run: ValidationRun): { pass: number; fail: number; skipped: number } {
  const count = (outcome: string) => run.results.filter((r) => r.outcome === outcome).length;
  return { pass: count('pass'), fail: count('fail'), skipped: count('skipped') };
}

@Component({
  selector: 'app-replay',
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, UpperCasePipe],
  templateUrl: './replay.html',
})
export class Replay {
  private readonly api = inject(Api);
  protected readonly user = inject(UserService);

  readonly mapping = input.required<MappingDocument>();
  /** Emitted after runs were recorded on the mapping. */
  readonly recorded = output<void>();

  protected readonly counts = counts;
  protected readonly profiles = signal<ProfileSummary[]>([]);
  protected readonly targets = computed(() => this.profiles().filter((p) => p.format === this.mapping().target.format));
  protected readonly target = signal('');
  protected readonly xmlNamespace = signal('');
  protected readonly record = signal(false);
  protected readonly mask = signal(true);
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly samples = signal<ReplaySample[]>([]);
  protected readonly open = signal<string | null>(null);
  protected readonly extension = computed(() => (this.mapping().source.format === 'xml' ? '.xml' : '.json'));

  constructor() {
    this.api.profiles().subscribe((profiles) => {
      this.profiles.set(profiles);
      const match = this.targets().find((p) => p.system === this.mapping().target.name) ?? this.targets()[0];
      if (match && !this.target()) {
        this.target.set(match.id);
      }
    });
  }

  protected pick(input: HTMLInputElement): void {
    this.files.update((current) => [...current, ...Array.from(input.files ?? [])]);
    input.value = '';
  }

  protected remove(file: File): void {
    this.files.update((current) => current.filter((f) => f !== file));
  }

  protected run(): void {
    this.busy.set(true);
    this.api
      .replay(this.mapping().id, this.files(), { target: this.target(), xmlNamespace: this.xmlNamespace().trim() || undefined, record: this.record(), mask: this.mask() })
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: (response) => {
          this.samples.set(response.samples);
          this.open.set(response.samples[0]?.sample ?? null);
          if (response.recorded) {
            this.recorded.emit();
          }
        },
        error: () => undefined,
      });
  }

  protected download(sample: ReplaySample): void {
    const type = this.mapping().target.format === 'xml' ? 'application/xml' : 'application/json';
    const url = URL.createObjectURL(new Blob([sample.payload], { type }));
    const link = document.createElement('a');
    link.href = url;
    link.download = sample.sample.replace(/\.[^.]+$/, '') + '.target.' + this.mapping().target.format;
    link.click();
    URL.revokeObjectURL(url);
  }
}
