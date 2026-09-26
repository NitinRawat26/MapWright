import { DecimalPipe } from '@angular/common';
import { Component, Injector, afterNextRender, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { Icon } from '../core/icon';
import { Settings } from '../core/models';

@Component({
  selector: 'app-settings',
  imports: [DecimalPipe, Icon, RouterLink],
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
})
export class SettingsPage {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);

  protected readonly settings = signal<Settings | null>(null);
  protected readonly failed = signal(false);

  constructor() {
    this.api.settings().subscribe({
      next: (s) => {
        this.settings.set(s);
        afterNextRender(() => this.scrollToFragment(), { injector: this.injector });
      },
      error: () => this.failed.set(true),
    });
  }

  private scrollToFragment(): void {
    const fragment = this.route.snapshot.fragment;
    if (fragment) {
      document.getElementById(fragment)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  }

  protected size(bytes: number): string {
    return bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }
}
