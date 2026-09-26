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
import { Icon } from '../core/icon';
import { FieldMapping, MappingDocument, MappingOrigin, MappingSummary, ReviewDecision, ReviewDecisionKind, ReviewStatus } from '../core/models';
import { UserService } from '../core/user';
import { Replay } from './replay';

export const reviewLabels: Record<ReviewStatus | string, string> = {
  autoAccepted: 'Auto-accepted',
  needsReview: 'Needs review',
  approved: 'Approved',
  rejected: 'Rejected',
  overridden: 'Overridden',
};

export const originLabels: Record<MappingOrigin, string> = {
  playbook: 'Playbook',
  nameMatch: 'Name match',
  ai: 'AI',
  reviewer: 'Reviewer',
  other: 'Other',
};

const originClasses: Record<MappingOrigin, string> = {
  playbook: 'good',
  nameMatch: 'info',
  ai: 'ai',
  reviewer: '',
  other: '',
};

export const reviewClass: Record<ReviewStatus | string, string> = {
  autoAccepted: 'good',
  needsReview: 'warn',
  approved: 'good',
  rejected: 'bad',
  overridden: 'good',
};

export type RowFilter =
  | 'all'
  | 'open'
  | 'unmapped'
  | 'decided'
  | 'ai'
  | 'mapped'
  | 'required'
  | 'requiredUnmapped'
  | 'high'
  | 'medium'
  | 'low'
  | 'approved'
  | 'rejected';

/** Names of the filters only the summary cards set; the others have a toggle button. */
const cardFilterLabels: Partial<Record<RowFilter, string>> = {
  mapped: 'Mapped',
  required: 'Required',
  requiredUnmapped: 'Required, not mapped',
  high: 'High confidence',
  medium: 'Medium confidence',
  low: 'Low confidence',
  approved: 'Approved',
  rejected: 'Rejected',
};

@Component({
  selector: 'app-mapping-detail',
  imports: [DatePipe, FormsModule, Icon, UpperCasePipe, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatTabsModule, Replay, RouterLink],
  templateUrl: './mapping-detail.html',
  styleUrl: './mapping-detail.scss',
})
export class MappingDetail {
  private readonly api = inject(Api);
  protected readonly user = inject(UserService);

  readonly id = input.required<string>();

  protected readonly labels = reviewLabels;
  protected readonly classes = reviewClass;
  protected readonly originLabels = originLabels;
  protected readonly originClasses = originClasses;
  protected readonly mapping = signal<MappingDocument | null>(null);
  protected readonly summary = signal<MappingSummary | null>(null);
  protected readonly reviews = signal<ReviewDecision[]>([]);
  protected readonly filterLabels = cardFilterLabels;
  protected readonly filter = signal<RowFilter>('all');
  protected readonly tab = signal(0);
  protected readonly search = signal('');
  protected readonly selected = signal<string | null>(null);
  protected readonly comment = signal('');
  protected readonly override = signal('');
  protected readonly busy = signal(false);

  protected readonly rows = computed(() => {
    const text = this.search().trim().toLowerCase();
    const mapping = this.mapping();
    return (mapping?.mappings ?? []).filter((row) => {
      const shown = this.matches(row, this.filter(), mapping!.confidencePolicy);
      return shown && (!text || [row.id, row.target.path, ...row.sources.map((s) => s.path), row.businessConcept ?? ''].some((v) => v.toLowerCase().includes(text)));
    });
  });

  /** Shows only the rows behind a summary number; clicking the active one again shows all rows. */
  protected show(filter: RowFilter): void {
    this.filter.set(this.filter() === filter ? 'all' : filter);
    this.tab.set(0);
  }

  private matches(row: FieldMapping, filter: RowFilter, policy: MappingDocument['confidencePolicy']): boolean {
    const status = row.review.status;
    const mapped = row.type !== 'unmapped';
    const band = row.confidencePercent >= policy.highThreshold ? 'high' : row.confidencePercent >= policy.mediumThreshold ? 'medium' : 'low';
    switch (filter) {
      case 'all':
        return true;
      case 'open':
        return status === 'needsReview';
      case 'unmapped':
        return !mapped;
      case 'decided':
        return status === 'approved' || status === 'rejected' || status === 'overridden';
      case 'ai':
        return this.aiProvider(row) !== null;
      case 'mapped':
        return mapped;
      case 'required':
        return !!row.target.required;
      case 'requiredUnmapped':
        return !!row.target.required && !mapped;
      case 'high':
      case 'medium':
      case 'low':
        return mapped && band === filter;
      case 'approved':
        return status === 'approved' || status === 'overridden';
      case 'rejected':
        return status === 'rejected';
    }
  }

  /** What produced a row, in the same order as the server's summary: AI, playbook, name match, reviewer. */
  protected origin(row: FieldMapping): MappingOrigin | null {
    if (row.type === 'unmapped') {
      return null;
    }
    const has = (kind: string) => row.evidence?.some((e) => e.kind === kind) ?? false;
    return has('aiSuggestion') ? 'ai' : has('playbook') ? 'playbook' : has('nameSimilarity') ? 'nameMatch' : has('reviewer') ? 'reviewer' : 'other';
  }

  protected band(row: FieldMapping): 'high' | 'medium' | 'low' {
    const policy = this.mapping()?.confidencePolicy;
    return !policy || row.confidencePercent >= policy.highThreshold ? 'high' : row.confidencePercent >= policy.mediumThreshold ? 'medium' : 'low';
  }

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
