import { DatePipe, UpperCasePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router, RouterLink } from '@angular/router';
import { catchError, finalize, of } from 'rxjs';
import { Api } from '../core/api';
import { Icon } from '../core/icon';
import { IconName } from '../core/icons';
import { NewProfile, ProfileSummary } from '../core/models';
import { ago } from '../core/time';
import { UserService } from '../core/user';
import { accepts, allExtensions, presetById, profilePresets } from './profile-presets';

export const profileExtensions = allExtensions;

const documentPattern = /\.(pdf|docx)$/i;

const presetIcons: Record<string, IconName> = {
  'json-rest': 'data_object',
  'soap-xml': 'schema',
  'xml-file': 'description',
  'field-spec': 'table_chart',
  'data-dictionary': 'list_alt',
  document: 'description',
  samples: 'science',
  custom: 'tune',
};

@Component({
  selector: 'app-profile-list',
  imports: [
    DatePipe,
    FormsModule,
    Icon,
    MatButtonModule,
    MatButtonToggleModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    RouterLink,
    UpperCasePipe,
  ],
  templateUrl: './profile-list.html',
  styleUrl: './profile-list.scss',
})
export class ProfileList {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  protected readonly user = inject(UserService);

  protected readonly presets = profilePresets;
  protected readonly presetIcons = presetIcons;
  protected readonly preset = signal(presetById('custom'));
  protected readonly extensions = computed(() => this.preset().accept);
  protected readonly profiles = signal<ProfileSummary[]>([]);
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly request = signal<NewProfile>({ system: '' });
  protected readonly ai = toSignal(this.api.ai().pipe(catchError(() => of(null))), { initialValue: null });
  protected readonly filter = signal('');
  protected readonly dragging = signal(false);
  protected readonly shown = computed(() => {
    const term = this.filter().trim().toLowerCase();
    return term ? this.profiles().filter((p) => `${p.id} ${p.system}`.toLowerCase().includes(term)) : this.profiles();
  });
  protected readonly formats = computed(() => ({
    json: this.profiles().filter((p) => p.format === 'json').length,
    xml: this.profiles().filter((p) => p.format === 'xml').length,
  }));
  protected readonly totalFields = computed(() => this.profiles().reduce((sum, p) => sum + p.fieldCount, 0));
  protected readonly hasDocuments = computed(() => this.files().some((f) => documentPattern.test(f.name)));
  protected readonly outside = computed(() =>
    this.files()
      .filter((f) => !accepts(this.preset().accept, f.name))
      .map((f) => f.name)
      .join(', '),
  );

  constructor() {
    this.load();
  }

  protected choose(id: string): void {
    const previous = this.preset();
    const next = presetById(id);
    this.preset.set(next);
    this.request.update((r) => ({
      ...r,
      description: !r.description?.trim() || r.description === previous.defaults.description ? next.defaults.description : r.description,
      noValues: r.noValues === previous.defaults.noValues ? next.defaults.noValues : r.noValues,
    }));
  }

  protected pick(input: HTMLInputElement): void {
    this.files.update((current) => [...current, ...Array.from(input.files ?? [])]);
    input.value = '';
  }

  protected dropFiles(event: DragEvent): void {
    event.preventDefault();
    this.dragging.set(false);
    const dropped = Array.from(event.dataTransfer?.files ?? []);
    if (dropped.length) {
      this.files.update((current) => [...current, ...dropped]);
    }
  }

  protected open(profile: ProfileSummary): void {
    void this.router.navigate(['/profiles', profile.id]);
  }

  protected scrollToBuilder(event: Event): void {
    event.preventDefault();
    document.getElementById('build')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  protected ago(iso: string): string {
    return iso ? ago(iso) : '—';
  }

  protected remove(file: File): void {
    this.files.update((current) => current.filter((f) => f !== file));
  }

  protected build(): void {
    this.busy.set(true);
    const request = { ...this.request(), system: this.request().system.trim() };
    if (!this.hasDocuments() || !this.ai()?.available) {
      delete request.useAi;
    }
    this.api
      .createProfile(this.files(), request)
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: (id) => {
          this.files.set([]);
          this.request.set({ system: '', description: this.preset().defaults.description, noValues: this.preset().defaults.noValues });
          void this.router.navigate(['/profiles', id]);
        },
        error: () => undefined,
      });
  }

  protected patch(change: Partial<NewProfile>): void {
    this.request.update((r) => ({ ...r, ...change }));
  }

  protected drop(profile: ProfileSummary): void {
    if (confirm(`Delete profile ${profile.id}? Mappings that used it are kept.`)) {
      this.api.deleteProfile(profile.id).subscribe(() => this.load());
    }
  }

  private load(): void {
    this.api.profiles().subscribe((list) => this.profiles.set(list));
  }
}
