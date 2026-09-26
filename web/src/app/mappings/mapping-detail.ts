import { DatePipe, UpperCasePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { Api } from '../core/api';
import { FieldMapping, MappingDocument, MappingSummary, ReviewDecision, ReviewDecisionKind, ReviewStatus } from '../core/models';
import { UserService } from '../core/user';
import { Replay } from './replay';

export const reviewLabels: Record<ReviewStatus | string, string> = {
  autoAccepted: 'Auto-accepted',
  needsReview: 'Needs review',
  approved: 'Approved',
  rejected: 'Rejected',
  overridden: 'Overridden',
};

export const reviewClass: Record<ReviewStatus | string, string> = {
  autoAccepted: 'good',
  needsReview: 'warn',
  approved: 'good',
  rejected: 'bad',
  overridden: 'good',
};

@Component({
  selector: 'app-mapping-detail',
  imports: [DatePipe, FormsModule, UpperCasePipe, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatTabsModule, Replay, RouterLink],
  templateUrl: './mapping-detail.html',
  styleUrl: './mapping-detail.scss',
})
export class MappingDetail {
  private readonly api = inject(Api);
  protected readonly user = inject(UserService);

  readonly id = input.required<string>();

  protected readonly labels = reviewLabels;
  protected readonly classes = reviewClass;
  protected readonly mapping = signal<MappingDocument | null>(null);
  protected readonly summary = signal<MappingSummary | null>(null);
  protected readonly reviews = signal<ReviewDecision[]>([]);
  protected readonly filter = signal<'all' | 'open' | 'unmapped' | 'decided' | 'ai'>('all');
  protected readonly search = signal('');
  protected readonly selected = signal<string | null>(null);
  protected readonly comment = signal('');
  protected readonly override = signal('');
  protected readonly busy = signal(false);

  protected readonly rows = computed(() => {
    const text = this.search().trim().toLowerCase();
    return (this.mapping()?.mappings ?? []).filter((row) => {
      const status = row.review.status;
      const shown =
        this.filter() === 'all' ||
        (this.filter() === 'open' && status === 'needsReview') ||
        (this.filter() === 'unmapped' && row.type === 'unmapped') ||
        (this.filter() === 'decided' && (status === 'approved' || status === 'rejected' || status === 'overridden')) ||
        (this.filter() === 'ai' && this.aiProvider(row) !== null);
      return shown && (!text || [row.id, row.target.path, ...row.sources.map((s) => s.path), row.businessConcept ?? ''].some((v) => v.toLowerCase().includes(text)));
    });
  });

  protected aiProvider(row: FieldMapping): string | null {
    return row.evidence?.find((e) => e.kind === 'aiSuggestion')?.reference ?? null;
  }

  protected readonly row = computed(() => this.mapping()?.mappings.find((m) => m.id === this.selected()) ?? null);
  protected readonly overrideError = computed(() => {
    if (!this.override()) {
      return '';
    }

    try {
      JSON.parse(this.override());
      return '';
    } catch {
      return 'Not valid JSON.';
    }
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => {
        this.selected.set(null);
        this.reload();
      });
    });
  }

  protected exportUrl(format: 'xlsx' | 'csv' | 'html' | 'pdf'): string {
    return this.api.exportUrl(this.id(), format);
  }

  protected select(row: FieldMapping): void {
    this.selected.set(this.selected() === row.id ? null : row.id);
    this.comment.set('');
    this.override.set('');
  }

  protected editOverride(row: FieldMapping): void {
    const { review: _review, ...editable } = row;
    this.override.set(JSON.stringify(editable, null, 2));
  }

  protected decide(decision: ReviewDecisionKind): void {
    const row = this.row();
    if (!row) {
      return;
    }

    const replacement = decision === 'override' ? ({ ...JSON.parse(this.override()), review: row.review } as FieldMapping) : undefined;
    this.busy.set(true);
    this.api
      .review(this.id(), row.id, decision, this.comment().trim() || undefined, replacement)
      .pipe(finalize(() => this.busy.set(false)))
      .subscribe({
        next: (result) => {
          this.mapping.update((m) => (m ? { ...m, mappings: m.mappings.map((r) => (r.id === result.rowId ? result.row : r)) } : m));
          this.comment.set('');
          this.override.set('');
          this.loadSummary();
          if (this.reviews().length) {
            this.loadReviews();
          }
        },
        error: () => undefined,
      });
  }

  protected reload(): void {
    this.api.mapping(this.id()).subscribe((mapping) => this.mapping.set(mapping));
    this.loadSummary();
  }

  protected loadReviews(): void {
    this.api.reviews(this.id()).subscribe((reviews) => this.reviews.set([...reviews].reverse()));
  }

  private loadSummary(): void {
    this.api.mappingSummary(this.id()).subscribe((summary) => this.summary.set(summary));
  }
}
