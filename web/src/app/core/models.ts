/** Shapes returned by the MapWright API (camelCase JSON, enums as camelCase strings). */

export interface SpecIssue {
  severity: 'error' | 'warning' | 'info';
  code: string;
  location: string;
  message: string;
}

export interface ApiProblem {
  title: string;
  status: number;
  detail: string;
  issues: SpecIssue[];
}

export interface AiStatus {
  available: boolean;
  provider?: string;
  maxConfidence: number;
}

export type PlaybookStatus = 'draft' | 'inReview' | 'published' | 'retired';
export type PlaybookKind = 'domain' | 'process';

export interface PlaybookSummary {
  id: string;
  version: string;
  name: string;
  kind: PlaybookKind;
  status: PlaybookStatus;
  owner?: string;
  createdAt: string;
  createdBy: string;
  updatedAt: string;
  updatedBy: string;
  reference: string;
}

export interface ProfileSummary {
  id: string;
  system: string;
  version?: string;
  format: 'json' | 'xml';
  fieldCount: number;
  createdAt: string;
  updatedAt: string;
  updatedBy: string;
}

export interface MappingListItem {
  id: string;
  title: string;
  version: string;
  sourceSystem: string;
  targetSystem: string;
  createdAt: string;
  updatedAt: string;
  updatedBy: string;
}

export type SuggestionStatus = 'pending' | 'approved' | 'rejected';
