import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { Router, RouterLink } from '@angular/router';
import { Observable, finalize } from 'rxjs';
import { parse as parseYaml } from 'yaml';
import { Api } from '../core/api';
import { changeCount, sideBySide } from '../core/diff';
import { Playbook, PlaybookEvent, PlaybookStatus, PlaybookSummary, TestResponse, ValidationResponse } from '../core/models';
import { UserService } from '../core/user';
import { statusClass, statusLabels } from './status';

interface Transition {
  to: PlaybookStatus;
  label: string;
  primary: boolean;
}

const transitions: Record<PlaybookStatus, Transition[]> = {
  draft: [
    { to: 'inReview', label: 'Submit for review', primary: true },
    { to: 'retired', label: 'Abandon draft', primary: false },
  ],
  inReview: [
    { to: 'published', label: 'Publish', primary: true },
    { to: 'draft', label: 'Request changes', primary: false },
  ],
  published: [{ to: 'retired', label: 'Retire', primary: false }],
  retired: [],
};

@Component({
  selector: 'app-playbook-detail',
  imports: [DatePipe, FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatTabsModule, RouterLink],
  templateUrl: './playbook-detail.html',
  styleUrl: './playbook-detail.scss',
})
export class PlaybookDetail {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly user = inject(UserService);

  readonly kind = input.required<string>();
  readonly slug = input.required<string>();
  readonly version = input.required<string>();

  protected readonly labels = statusLabels;
  protected readonly classes = statusClass;
  protected readonly id = computed(() => `${this.kind()}/${this.slug()}`);

  /** The stored YAML (comments included), and the editor's copy of it (only drafts can be saved). */
  protected readonly saved = signal('');
  protected readonly edited = signal('');
  protected readonly versions = signal<PlaybookSummary[]>([]);
  protected readonly history = signal<PlaybookEvent[]>([]);
  protected readonly validation = signal<ValidationResponse | null>(null);
  protected readonly tests = signal<TestResponse | null>(null);
  protected readonly busy = signal(false);
  protected readonly note = signal('');
  protected readonly newVersion = signal('');
  protected readonly compareWith = signal('');
  protected readonly compareYaml = signal('');
  protected readonly tab = signal(0);

  protected readonly playbook = computed(() => read(this.saved()).playbook);
  protected readonly parseError = computed(() => read(this.edited()).error);
  protected readonly isDraft = computed(() => this.playbook()?.status === 'draft');
  protected readonly dirty = computed(() => this.isDraft() && this.edited() !== this.saved());
  protected readonly actions = computed(() => transitions[this.playbook()?.status ?? 'retired']);
  protected readonly canWrite = computed(() => !!this.user.name() && !this.busy());
  protected readonly others = computed(() => this.versions().filter((v) => v.version !== this.version()));
  protected readonly diff = computed(() => (this.compareYaml() ? sideBySide(this.compareYaml(), this.edited()) : []));
  protected readonly changes = computed(() => changeCount(this.diff()));
  protected readonly failedTests = computed(() => this.tests()?.results.filter((r) => !r.passed).length ?? 0);

  protected readonly counts = computed(() => {
    const domain = this.playbook()?.domain;
    if (!domain) {
      return [];
    }

    return [
      ['Vocabulary terms', domain.vocabulary?.length ?? 0],
      ['Detection signals', domain.signals?.length ?? 0],
      ['Derivations', domain.derivations?.length ?? 0],
      ['Conditional rules', domain.conditions?.length ?? 0],
      ['Value maps', domain.valueMaps?.length ?? 0],
      ['Validation rules', domain.validations?.length ?? 0],
      ['Tests', domain.tests?.length ?? 0],
    ] as const;
  });

  constructor() {
    effect(() => {
      const id = this.id();
      const version = this.version();
      untracked(() => this.load(id, version));
    });
  }

  protected save(): void {
    this.run(this.api.updateDraft(this.id(), this.version(), this.edited()), (yaml) => {
      this.saved.set(yaml);
      this.edited.set(yaml);
      this.loadHistory();
      this.snackBar.open('Draft saved.', undefined, { duration: 3000 });
    });
  }

  protected revert(): void {
    this.edited.set(this.saved());
  }

  /** Drafts are checked as edited (unsaved); other versions as stored. */
  protected validate(): void {
    const request = this.isDraft() ? this.api.validatePlaybook(this.edited()) : this.api.validateStored(this.id(), this.version());
    this.run(request, (result) => this.validation.set(result));
  }

  protected test(): void {
    const request = this.isDraft() ? this.api.testPlaybook(this.edited()) : this.api.testStored(this.id(), this.version());
    this.run(request, (result) => this.tests.set(result));
  }

  protected transition(to: PlaybookStatus): void {
    this.run(this.api.changeStatus(this.id(), this.version(), to, this.note().trim() || undefined), (yaml) => {
      this.note.set('');
      this.saved.set(yaml);
      this.edited.set(yaml);
      this.loadVersions();
      this.loadHistory();
      this.snackBar.open(`Now ${statusLabels[to].toLowerCase()}.`, undefined, { duration: 3000 });
    });
  }

  protected draftNewVersion(): void {
    const request = { version: this.newVersion().trim() || undefined, note: this.note().trim() || undefined };
    this.run(this.api.newVersion(this.id(), this.version(), request), (json) => {
      const draft = JSON.parse(json) as Playbook;
      this.note.set('');
      this.newVersion.set('');
      void this.router.navigate(['/playbooks', this.kind(), this.slug(), draft.version]);
    });
  }

  protected compare(version: string): void {
    this.compareWith.set(version);
    this.compareYaml.set('');
    if (version) {
      this.api.playbookYaml(this.id(), version).subscribe((yaml) => this.compareYaml.set(yaml));
    }
  }

  private load(id: string, version: string): void {
    this.validation.set(null);
    this.tests.set(null);
    this.compare('');
    this.api.playbookYaml(id, version).subscribe((yaml) => {
      this.saved.set(yaml);
      this.edited.set(yaml);
    });
    this.loadVersions();
    this.loadHistory();
  }

  private loadVersions(): void {
    this.api.playbookVersions(this.id()).subscribe((versions) => {
      this.versions.set(versions);
      if (!this.compareWith()) {
        const previous = versions.filter((v) => v.version !== this.version()).at(-1);
        if (previous) {
          this.compare(previous.version);
        }
      }
    });
  }

  private loadHistory(): void {
    this.api.playbookHistory(this.id()).subscribe((events) => this.history.set([...events].reverse()));
  }

  private run<T>(request: Observable<T>, next: (value: T) => void): void {
    this.busy.set(true);
    request.pipe(finalize(() => this.busy.set(false))).subscribe({ next, error: () => undefined });
  }
}

/** Reads playbook YAML (or JSON, which is also YAML) for display; the API does the real validation. */
function read(text: string): { playbook: Playbook | null; error: string } {
  if (!text.trim()) {
    return { playbook: null, error: '' };
  }

  try {
    const value: unknown = parseYaml(text);
    return value !== null && typeof value === 'object' && !Array.isArray(value)
      ? { playbook: value as Playbook, error: '' }
      : { playbook: null, error: 'Not a playbook: expected fields such as id, name and kind.' };
  } catch (e) {
    return { playbook: null, error: `Not valid YAML: ${e instanceof Error ? e.message : String(e)}` };
  }
}
