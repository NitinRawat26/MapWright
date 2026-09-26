import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { Api } from '../core/api';
import { Icon } from '../core/icon';
import { Suggestion, SuggestionStatus } from '../core/models';
import { UserService } from '../core/user';

interface Draft {
  concept: string;
  comment: string;
  create: boolean;
}

@Component({
  selector: 'app-suggestion-list',
  imports: [DatePipe, FormsModule, Icon, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, RouterLink],
  templateUrl: './suggestion-list.html',
  styleUrl: './suggestion-list.scss',
})
export class SuggestionList {
  private readonly api = inject(Api);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly user = inject(UserService);

  protected readonly status = signal<SuggestionStatus | ''>('pending');
  protected readonly suggestions = signal<Suggestion[]>([]);
  protected readonly drafts = signal<Record<number, Draft>>({});
  protected readonly busy = signal<number | null>(null);
  protected readonly kinds = computed(() => {
    const list = this.suggestions();
    return {
      mapping: list.filter((s) => s.content.mapping).length,
      detection: list.filter((s) => !s.content.mapping).length,
      drafts: list.filter((s) => s.status === 'pending' && !s.content.domainPlaybook && s.content.proposedConcept).length,
    };
  });

  constructor() {
    effect(() => {
      const status = this.status();
      this.api.suggestions(status || undefined).subscribe((list) => this.suggestions.set(list));
    });
  }

  protected draft(s: Suggestion): Draft {
    return (
      this.drafts()[s.id] ?? {
        concept: s.content.businessConcept ?? s.content.proposedConcept ?? '',
        comment: '',
        create: !s.content.businessConcept && !!s.content.proposedConcept,
      }
    );
  }

  protected edit(s: Suggestion, change: Partial<Draft>): void {
    this.drafts.update((all) => ({ ...all, [s.id]: { ...this.draft(s), ...change } }));
  }

  /** Links to the draft playbook version an approval changed, e.g. domain/tax-id@1.1.0. */
  protected playbookLink(reference: string): string[] {
    const [id, version] = reference.split('@');
    return ['/playbooks', ...id.split('/'), version];
  }

  protected approve(s: Suggestion): void {
    const d = this.draft(s);
    const concept = d.concept.trim();
    this.busy.set(s.id);
    this.api
      .approveSuggestion(s.id, {
        concept: concept && concept !== s.content.businessConcept ? concept : undefined,
        comment: d.comment.trim() || undefined,
        create: d.create || undefined,
      })
      .pipe(finalize(() => this.busy.set(null)))
      .subscribe({
        next: (result) => {
          this.replace(result.suggestion);
          const pairing = s.content.mapping;
          const row = pairing ? `row ${pairing.rowId} of ${pairing.mappingId}` : '';
          const message = !result.playbookId
            ? `Approved ${row}.`
            : (result.created
                ? `Drafted new playbook ${result.playbookId}@${result.version} with '${s.content.fieldName}'.`
                : `Added '${s.content.fieldName}' to draft ${result.playbookId}@${result.version}.`) + (row ? ` Approved ${row}.` : '');
          this.snackBar.open(message, undefined, { duration: 5000 });
        },
        error: () => undefined,
      });
  }

  protected reject(s: Suggestion): void {
    this.busy.set(s.id);
    this.api
      .rejectSuggestion(s.id, this.draft(s).comment.trim() || undefined)
      .pipe(finalize(() => this.busy.set(null)))
      .subscribe({ next: (result) => this.replace(result), error: () => undefined });
  }

  private replace(updated: Suggestion): void {
    this.suggestions.update((list) =>
      this.status() && updated.status !== this.status() ? list.filter((s) => s.id !== updated.id) : list.map((s) => (s.id === updated.id ? updated : s)),
    );
  }
}
