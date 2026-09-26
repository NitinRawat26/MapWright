import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { Router, RouterLink } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { Api } from '../core/api';
import { Icon } from '../core/icon';
import { IconName } from '../core/icons';
import { MappingListItem, MappingOrigin } from '../core/models';
import { ago } from '../core/time';
import { UserService } from '../core/user';

interface Attention {
  count: number;
  tone: 'bad' | 'warn' | 'ai' | 'info';
  text: string;
  link: string;
}

interface Change {
  at: string;
  who: string;
  icon: IconName;
  text: string;
  link: string;
}

/** Colours of the "How targets were filled" ring, in the order they are drawn. */
const fills: { key: MappingOrigin | 'unmapped'; label: string; color: string }[] = [
  { key: 'playbook', label: 'Playbook rules', color: '#4f46e5' },
  { key: 'nameMatch', label: 'Name match', color: '#0ea5e9' },
  { key: 'ai', label: 'AI suggested', color: '#8b5cf6' },
  { key: 'reviewer', label: 'Reviewer', color: '#10b981' },
  { key: 'other', label: 'Other evidence', color: '#94a3b8' },
  { key: 'unmapped', label: 'Unmapped', color: '#dfe2ee' },
];

@Component({
  selector: 'app-home',
  imports: [Icon, MatButtonModule, RouterLink],
  templateUrl: './home.html',
  styleUrl: './home.scss',
})
export class Home {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  protected readonly user = inject(UserService);
  protected readonly ago = ago;

  protected readonly healthy = toSignal(this.api.health().pipe(map((h) => h.status === 'ok'), catchError(() => of(false))));
  protected readonly ai = toSignal(this.api.ai().pipe(catchError(() => of(null))));
  protected readonly playbooks = toSignal(this.api.playbooks().pipe(catchError(() => of([]))));
  protected readonly profiles = toSignal(this.api.profiles().pipe(catchError(() => of([]))));
  protected readonly mappings = toSignal(this.api.mappings().pipe(catchError(() => of([]))));
  protected readonly pending = toSignal(this.api.pendingSuggestions().pipe(catchError(() => of([]))));

  protected readonly greeting = computed(() => {
    const hour = new Date().getHours();
    const part = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening';
    const first = this.user.name().split(/\s+/)[0];
    return first ? `${part}, ${first}` : 'Welcome to MapWright';
  });

  protected readonly inReview = computed(() => (this.mappings() ?? []).filter((m) => this.needsReview(m) > 0).length);

  protected readonly recent = computed(() =>
    [...(this.mappings() ?? [])].sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt)).slice(0, 6),
  );

  /** Every target field of every mapping, split by what filled it. */
  protected readonly filled = computed(() => {
    const total = (this.mappings() ?? []).reduce((sum, m) => sum + (m.summary?.totalTargetFields ?? 0), 0);
    const count = (key: MappingOrigin | 'unmapped') =>
      (this.mappings() ?? []).reduce(
        (sum, m) => sum + (key === 'unmapped' ? (m.summary?.unmappedTargetFields ?? 0) : (m.summary?.byOrigin?.[key] ?? 0)),
        0,
      );
    const parts = fills.map((f) => ({ ...f, count: count(f.key), percent: total ? Math.round((100 * count(f.key)) / total) : 0 }));
    let start = 0;
    const stops = parts
      .filter((p) => p.count)
      .map((p) => {
        const end = start + (100 * p.count) / total;
        const stop = `${p.color} ${start}% ${end}%`;
        start = end;
        return stop;
      });
    const mapped = total - count('unmapped');
    return {
      total,
      covered: total ? Math.round((100 * mapped) / total) : 0,
      parts: parts.filter((p) => p.count || p.key === 'playbook' || p.key === 'ai' || p.key === 'unmapped'),
      ring: stops.length ? `conic-gradient(${stops.join(', ')})` : 'conic-gradient(#dfe2ee 0 100%)',
    };
  });

  protected readonly steps = computed(() => {
    const mappings = this.mappings() ?? [];
    return [
      { text: 'Build the source and target profiles', done: (this.profiles()?.length ?? 0) >= 2, link: '/profiles' },
      { text: 'Generate a mapping; AI can fill the gaps', done: mappings.length > 0, link: '/mappings' },
      { text: 'Review the rows that need a decision', done: mappings.some((m) => m.summary && this.needsReview(m) === 0), link: '/mappings' },
      {
        text: 'Replay samples, then export',
        done: mappings.some((m) => (m.summary?.validationPassed ?? 0) + (m.summary?.validationFailed ?? 0) > 0),
        link: '/mappings',
      },
    ];
  });

  protected readonly attention = computed(() => {
    const items: Attention[] = [];
    for (const m of this.mappings() ?? []) {
      const s = m.summary;
      if (!s) {
        continue;
      }

      const missing = s.requiredTargetFields - s.requiredTargetFieldsMapped;
      if (missing) {
        items.push({ count: missing, tone: 'bad', text: `Required target fields unmapped in ${m.sourceSystem} → ${m.targetSystem}`, link: `/mappings/${m.id}` });
      }

      if (s.byReviewStatus.needsReview) {
        items.push({ count: s.byReviewStatus.needsReview, tone: 'warn', text: `Rows waiting for review in ${m.sourceSystem} → ${m.targetSystem}`, link: `/mappings/${m.id}` });
      }
    }

    const pending = this.pending()?.length ?? 0;
    if (pending) {
      items.push({ count: pending, tone: 'ai', text: 'AI suggestions waiting for a decision', link: '/suggestions' });
    }

    const inReview = this.count('inReview');
    if (inReview) {
      items.push({ count: inReview, tone: 'info', text: 'Playbook versions waiting for a second reviewer', link: '/playbooks' });
    }

    return items.sort((a, b) => ['bad', 'warn', 'ai', 'info'].indexOf(a.tone) - ['bad', 'warn', 'ai', 'info'].indexOf(b.tone)).slice(0, 6);
  });

  /** The latest saves of profiles, mappings and playbooks. */
  protected readonly changes = computed(() => {
    const all: Change[] = [
      ...(this.profiles() ?? []).map((p) => ({ at: p.updatedAt, who: p.updatedBy, icon: 'database' as const, text: `saved profile ${p.system}`, link: `/profiles/${p.id}` })),
      ...(this.mappings() ?? []).map((m) => ({
        at: m.updatedAt,
        who: m.updatedBy,
        icon: 'account_tree' as const,
        text: `saved mapping ${m.sourceSystem} → ${m.targetSystem}`,
        link: `/mappings/${m.id}`,
      })),
      ...(this.playbooks() ?? []).map((p) => ({
        at: p.updatedAt,
        who: p.updatedBy,
        icon: 'menu_book' as const,
        text: `${p.status === 'inReview' ? 'sent for review' : p.status} ${p.reference}`,
        link: `/playbooks/${p.id}/${p.version}`,
      })),
    ];
    return all.sort((a, b) => Date.parse(b.at) - Date.parse(a.at)).slice(0, 6);
  });

  protected count(status: string): number {
    return (this.playbooks() ?? []).filter((p) => p.status === status).length;
  }

  protected needsReview(m: MappingListItem): number {
    return m.summary?.byReviewStatus.needsReview ?? 0;
  }

  protected coverage(m: MappingListItem): number {
    return m.summary?.totalTargetFields ? Math.round((100 * m.summary.mappedTargetFields) / m.summary.totalTargetFields) : 0;
  }

  protected open(link: string): void {
    void this.router.navigateByUrl(link);
  }
}
