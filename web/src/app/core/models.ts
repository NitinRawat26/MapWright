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

export type PlaybookStatus = 'draft' | 'inReview' | 'published' | 'retired' | 'abandoned';
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
  /** Absent when the version was deleted. */
  to?: PlaybookStatus;
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

export interface ProfileField {
  path: string;
  name: string;
  parentPath?: string;
  kind: string;
  cardinality: string;
  dataType: string;
  format?: string;
  required: string;
  maxLength?: number;
  allowedValues?: string[];
  observedValues?: string[];
  sampleValue?: string;
  sensitive?: boolean;
  description?: string;
  seenIn?: string[];
}

export interface ProfileFinding {
  kind: string;
  path?: string;
  message: string;
  inputs?: string[];
}

export interface SystemProfile {
  specVersion: string;
  system: string;
  version?: string;
  format: 'json' | 'xml';
  description?: string;
  createdAt: string;
  inputs?: { name: string; kind: string; format?: string; sha256?: string; notes?: string }[];
  fields: ProfileField[];
  findings?: ProfileFinding[];
}

export interface PlaybookMatch {
  path: string;
  detection: {
    playbook: string;
    concept: string;
    attribute?: string;
    businessConcept: string;
    score: number;
    evidence?: string[];
    requiresReview: boolean;
    questions?: string[];
    warnings?: string[];
  };
}

export interface Suggestion {
  id: number;
  profileId: string;
  system: string;
  status: SuggestionStatus;
  content: {
    path: string;
    fieldName: string;
    businessConcept?: string;
    domainPlaybook?: string;
    proposedConcept?: string;
    meaning: string;
    confidencePercent: number;
    reasoning: string;
    question?: string;
    provider: string;
    model: string;
  };
  createdAt: string;
  createdBy: string;
  decidedAt?: string;
  decidedBy?: string;
  comment?: string;
  playbook?: string;
}

export interface DetectResponse {
  system: string;
  recognised: PlaybookMatch[];
  suggestions: Suggestion[];
  remaining: string[];
  warnings: string[];
  usedAi?: boolean;
  detectedAt?: string;
  detectedBy?: string;
  stale?: string[];
}

export interface FieldDescriptor {
  name: string;
  path: string;
  dataType: string;
  format?: string;
  maxLength?: number;
  required?: boolean;
  cardinality?: string;
  allowedValues?: string[];
  sampleValue?: string;
  description?: string;
}

export type ReviewStatus = 'autoAccepted' | 'needsReview' | 'approved' | 'rejected' | 'overridden';

export interface FieldMapping {
  id: string;
  type: string;
  sources: FieldDescriptor[];
  target: FieldDescriptor;
  businessConcept?: string;
  domainPlaybook?: string;
  transformation: {
    type: string;
    rule?: string;
    expression?: string;
    inputs?: Record<string, string>;
    pattern?: string;
    condition?: string;
    defaultValue?: string;
    valueMap?: { sourceValue: string; targetValue: string; notes?: string }[];
    cases?: { when: { source: string; in: string[] }[]; then: string }[];
  };
  confidencePercent: number;
  reasoning: string;
  evidence?: { kind: string; reference: string; detail?: string }[];
  risk?: { dataLoss: string; dataLossNote?: string; sensitivity: string; targetValidationRules?: string[] };
  review: { status: ReviewStatus; reviewer?: string; reviewedOn?: string; comments?: string; openQuestion?: string };
  suggestedResolution?: string;
}

export interface ValidationResult {
  mappingId: string;
  outcome: 'pass' | 'fail' | 'skipped';
  expected?: string;
  actual?: string;
  message?: string;
}

export interface ValidationRun {
  id: string;
  ranAt: string;
  samplePayload: string;
  results: ValidationResult[];
}

export interface MappingDocument {
  specVersion: string;
  id: string;
  title: string;
  version: string;
  createdAt: string;
  description?: string;
  source: { name: string; version?: string; format: 'json' | 'xml'; description?: string };
  target: { name: string; version?: string; format: 'json' | 'xml'; description?: string };
  confidencePolicy: { highThreshold: number; mediumThreshold: number };
  playbooks?: string[];
  mappings: FieldMapping[];
  orphanSourceFields?: { field: FieldDescriptor; suggestedResolution?: string }[];
  findings?: { id: string; kind: string; description: string; mappingIds?: string[]; sources?: string[]; resolution?: string }[];
  validationRuns?: ValidationRun[];
  aiPass?: { provider: string; maxConfidence: number; suggestedRows?: string[]; unmatched?: string[]; warnings?: string[] };
}

export interface MappingSummary {
  totalTargetFields: number;
  mappedTargetFields: number;
  unmappedTargetFields: number;
  requiredTargetFields: number;
  requiredTargetFieldsMapped: number;
  requiredCoveragePercent: number;
  byConfidenceBand: Record<'high' | 'medium' | 'low', number>;
  byReviewStatus: Record<ReviewStatus, number>;
  orphanSourceFields: number;
  conflicts: number;
  assumptions: number;
  validationPassed: number;
  validationFailed: number;
  validationSkipped: number;
}

export type ReviewDecisionKind = 'approve' | 'reject' | 'override';

export interface ReviewDecision {
  sequence: number;
  mappingId: string;
  rowId: string;
  decision: ReviewDecisionKind;
  previousStatus: ReviewStatus;
  reviewer: string;
  comment?: string;
  row: FieldMapping;
  decidedAt: string;
}

export interface NewProfile {
  system: string;
  id?: string;
  version?: string;
  description?: string;
  root?: string;
  noValues?: boolean;
  replace?: boolean;
  useAi?: boolean;
}

export interface ReplaySample {
  sample: string;
  payload: string;
  run: ValidationRun;
}

export interface ReplayResponse {
  samples: ReplaySample[];
  recorded: boolean;
  masked?: boolean;
}

export interface ApprovedSuggestion {
  suggestion: Suggestion;
  playbookId: string;
  version: string;
  created?: boolean;
}

/** GET /api/me: who the API records as the author of changes. */
export interface Me {
  name?: string;
  method?: 'header' | 'apiKey' | 'bearer' | 'proxy';
  signInRequired: boolean;
}
