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

export interface PlaybookEvent {
  sequence: number;
  id: string;
  version: string;
  actor: string;
  action: string;
  from?: PlaybookStatus;
  to: PlaybookStatus;
  note?: string;
  occurredAt: string;
}

export interface ValidationResponse {
  valid: boolean;
  issues: SpecIssue[];
}

export interface PlaybookTestResult {
  playbook: string;
  kind: string;
  id: string;
  passed: boolean;
  message?: string;
}

export interface TestResponse {
  passed: boolean;
  results: PlaybookTestResult[];
}

export interface ConceptAttribute {
  name: string;
  description?: string;
  dataType?: string;
  sensitivity?: string;
}

export interface VocabularyTerm {
  term: string;
  appliesTo?: string;
  relation?: string;
}

export interface DomainDefinition {
  concept: { name: string; description?: string; cardinality?: string; attributes: ConceptAttribute[] };
  vocabulary?: VocabularyTerm[];
  qualifiers?: unknown[];
  signals?: unknown[];
  derivations?: unknown[];
  conditions?: unknown[];
  valueMaps?: unknown[];
  validations?: unknown[];
  risks?: { id: string; appliesTo?: string; level: string; text: string }[];
  tests?: unknown[];
}

export interface ProcessStep {
  id: string;
  name: string;
  kind: string;
  description?: string;
}

/** A playbook in the file format; only the parts the UI shows are typed. */
export interface Playbook {
  specVersion: string;
  id: string;
  name: string;
  kind: PlaybookKind;
  version: string;
  status: PlaybookStatus;
  owner?: string;
  description?: string;
  changeNotes?: { version: string; date: string; author: string; description: string }[];
  domain?: DomainDefinition;
  process?: { steps?: ProcessStep[] };
}
