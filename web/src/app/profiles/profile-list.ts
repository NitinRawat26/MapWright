import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { Api } from '../core/api';
import { NewProfile, ProfileSummary } from '../core/models';
import { UserService } from '../core/user';

export const profileExtensions = '.json,.xml,.xsd,.wsdl,.csv,.xlsx,.yaml,.yml';

@Component({
  selector: 'app-profile-list',
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, RouterLink],
  templateUrl: './profile-list.html',
})
export class ProfileList {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  protected readonly user = inject(UserService);

  protected readonly extensions = profileExtensions;
  protected readonly profiles = signal<ProfileSummary[]>([]);
  protected readonly files = signal<File[]>([]);
  protected readonly busy = signal(false);
  protected readonly request = signal<NewProfile>({ system: '' });

  constructor() {
    this.load();
  }

  protected pick(input: HTMLInputElement): void {
    this.files.update((current) => [...current, ...Array.from(input.files ?? [])]);
    input.value = '';
  }

  protected remove(file: File): void {
    this.files.update((current) => current.filter((f) => f !== file));
  }

  protected build(): void {
    this.busy.set(true);
    const request = { ...this.request(), system: this.request().system.trim() };
    this.api
      .createProfile(this.files(), request)
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: (id) => {
          this.files.set([]);
          this.request.set({ system: '' });
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
