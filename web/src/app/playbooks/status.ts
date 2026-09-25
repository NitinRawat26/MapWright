import { PlaybookStatus } from '../core/models';

export const statusLabels: Record<PlaybookStatus | string, string> = {
  draft: 'Draft',
  inReview: 'In review',
  published: 'Published',
  retired: 'Retired',
};

export const statusClass: Record<PlaybookStatus | string, string> = {
  draft: 'warn',
  inReview: 'warn',
  published: 'good',
  retired: '',
};
