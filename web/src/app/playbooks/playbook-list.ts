import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { Icon } from '../core/icon';
import { Playbook, PlaybookStatus, PlaybookSummary } from '../core/models';
import { UserService } from '../core/user';
import { statusClass, statusLabels } from './status';

@Component({
  selector: 'app-playbook-list',
  imports: [
    DatePipe,
    FormsModule,
    Icon,
    MatButtonModule,
    MatButtonToggleModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatInputModule,
    MatTableModule,
    RouterLink,
  ],
  templateUrl: './playbook-list.html',
  styleUrl: './playbook-list.scss',
})
export class PlaybookList {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  protected readonly user = inject(UserService);

  protected readonly labels = statusLabels;
  protected readonly classes = statusClass;
  protected readonly columns = ['id', 'version', 'name', 'kind', 'status', 'owner', 'updated'];
  protected readonly status = signal<PlaybookStatus | ''>('');
  protected readonly search = signal('');
  protected readonly all = signal<PlaybookSummary[]>([]);
  private readonly everything = signal<PlaybookSummary[]>([]);
  protected readonly importText = signal('');
  protected readonly importOpen = signal(false);
  protected readonly stats = computed(() => {
    const list = this.everything();
    const count = (status: PlaybookStatus) => list.filter((p) => p.status === status).length;
    return {
      published: count('published'),
      draft: count('draft'),
      inReview: count('inReview'),
      domain: list.filter((p) => p.kind === 'domain' && p.status !== 'abandoned').length,
      process: list.filter((p) => p.kind === 'process' && p.status !== 'abandoned').length,
    };
  });

  /** "All" leaves out abandoned drafts; the Abandoned filter shows them. */
  protected readonly rows = computed(() => {
    const term = this.search().trim().toLowerCase();
    return this.all()
      .filter((p) => this.status() !== '' || p.status !== 'abandoned')
      .filter((p) => !term || `${p.id} ${p.name} ${p.owner ?? ''}`.toLowerCase().includes(term));
  });

  constructor() {
    this.load();
  }

  protected filter(status: PlaybookStatus | ''): void {
    this.status.set(status);
    this.load();
  }

  protected async readFile(input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0];
    if (file) {
      this.importText.set(await file.text());
    }
    input.value = '';
  }

  protected create(): void {
    this.api.createPlaybook(this.importText()).subscribe((created) => {
      const playbook = JSON.parse(created) as Playbook;
      this.importText.set('');
      void this.router.navigate(['/playbooks', ...playbook.id.split('/'), playbook.version]);
    });
  }

  protected open(p: PlaybookSummary): void {
    void this.router.navigate(this.link(p));
  }

  protected openImport(): void {
    this.importOpen.set(true);
    setTimeout(() => document.getElementById('add-playbook')?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
  }

  protected link(p: PlaybookSummary): string[] {
    return ['/playbooks', ...p.id.split('/'), p.version];
  }

  private load(): void {
    const status = this.status();
    this.api.playbooks(status || undefined).subscribe((list) => {
      this.all.set(list);
      if (!status) {
        this.everything.set(list);
      }
    });
  }
}
