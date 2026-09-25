import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import {
  AiStatus,
  ApprovedSuggestion,
  ReplayResponse,
  Suggestion,
  SuggestionStatus,
  DetectResponse,
  FieldMapping,
  MappingDocument,
  MappingListItem,
  MappingSummary,
  Me,
  NewProfile,
  ReviewDecision,
  ReviewDecisionKind,
  SystemProfile,
  PlaybookEvent,
  PlaybookStatus,
  PlaybookSummary,
  ProfileSummary,
  TestResponse,
  ValidationResponse,
} from './models';

const json = new HttpHeaders({ 'Content-Type': 'application/json' });
const yaml = { format: 'yaml' };

/** Playbook text is JSON when it starts with '{', otherwise YAML (the API tells them apart the same way). */
function playbookHeaders(playbook: string): HttpHeaders {
  return new HttpHeaders({ 'Content-Type': playbook.trimStart().startsWith('{') ? 'application/json' : 'application/yaml' });
}

/** Typed client for the MapWright API. Reads and writes go through the user and error interceptors. */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  health(): Observable<{ status: string }> {
    return this.http.get<{ status: string }>('/health');
  }

  me(): Observable<Me> {
    return this.http.get<Me>('/api/me');
  }

  ai(): Observable<AiStatus> {
    return this.http.get<AiStatus>('/api/ai');
  }

  playbooks(status?: PlaybookStatus): Observable<PlaybookSummary[]> {
    return this.http.get<PlaybookSummary[]>('/api/playbooks', { params: status ? { status } : {} });
  }

  /** Versions of one playbook, e.g. id "domain/tax-id". */
  playbookVersions(id: string): Observable<PlaybookSummary[]> {
    return this.http.get<PlaybookSummary[]>(`/api/playbooks/${id}`);
  }

  playbookHistory(id: string): Observable<PlaybookEvent[]> {
    return this.http.get<PlaybookEvent[]>(`/api/playbooks/${id}/history`);
  }

  /** The playbook as YAML, with the comments it was written with. */
  playbookYaml(id: string, version: string): Observable<string> {
    return this.http.get(`/api/playbooks/${id}/${version}`, { params: yaml, responseType: 'text' });
  }

  /** Creates a draft from YAML or JSON; answers with the stored playbook as JSON. */
  createPlaybook(playbook: string): Observable<string> {
    return this.http.post('/api/playbooks', playbook, { headers: playbookHeaders(playbook), responseType: 'text' });
  }

  /** Saves a draft from YAML or JSON; answers with the stored YAML. */
  updateDraft(id: string, version: string, playbook: string): Observable<string> {
    return this.http.put(`/api/playbooks/${id}/${version}`, playbook, { headers: playbookHeaders(playbook), params: yaml, responseType: 'text' });
  }

  newVersion(id: string, from: string, request: { version?: string; note?: string }): Observable<string> {
    return this.http.post(`/api/playbooks/${id}/${from}/versions`, request, { responseType: 'text' });
  }

  /** Deletes a draft that was never submitted for review. */
  deleteDraft(id: string, version: string, note?: string): Observable<unknown> {
    return this.http.delete(`/api/playbooks/${id}/${version}`, { params: note ? { note } : {} });
  }

  /** Moves a version to another status; answers with its YAML. */
  changeStatus(id: string, version: string, status: PlaybookStatus, note?: string): Observable<string> {
    return this.http.post(`/api/playbooks/${id}/${version}/status`, { status, note }, { params: yaml, responseType: 'text' });
  }

  /** Validates an unsaved playbook (YAML or JSON) against the published library. */
  validatePlaybook(playbook: string): Observable<ValidationResponse> {
    return this.http.post<ValidationResponse>('/api/playbooks/validate', playbook, { headers: playbookHeaders(playbook) });
  }

  /** Runs the detection tests and rule examples of an unsaved playbook (YAML or JSON). */
  testPlaybook(playbook: string): Observable<TestResponse> {
    return this.http.post<TestResponse>('/api/playbooks/test', playbook, { headers: playbookHeaders(playbook) });
  }

  validateStored(id: string, version: string): Observable<ValidationResponse> {
    return this.http.get<ValidationResponse>(`/api/playbooks/${id}/${version}/validate`);
  }

  testStored(id: string, version: string): Observable<TestResponse> {
    return this.http.get<TestResponse>(`/api/playbooks/${id}/${version}/test`);
  }

  profiles(): Observable<ProfileSummary[]> {
    return this.http.get<ProfileSummary[]>('/api/profiles');
  }

  profile(id: string): Observable<SystemProfile> {
    return this.http.get<SystemProfile>(`/api/profiles/${encodeURIComponent(id)}`);
  }

  /** Builds and stores a profile from uploaded samples and contracts; emits the new profile's id. */
  createProfile(files: File[], request: NewProfile): Observable<string> {
    const form = new FormData();
    for (const file of files) {
      form.append('files', file, file.name);
    }

    for (const [name, value] of Object.entries(request)) {
      if (value !== undefined && value !== '') {
        form.append(name, String(value));
      }
    }

    return this.http
      .post<SystemProfile>('/api/profiles', form, { observe: 'response' })
      .pipe(map((response) => decodeURIComponent((response.headers.get('Location') ?? '').split('/').pop() ?? '')));
  }

  deleteProfile(id: string): Observable<unknown> {
    return this.http.delete(`/api/profiles/${encodeURIComponent(id)}`);
  }

  /** Playbook detection; with useAi the unrecognised fields go to AI and its answers to the suggestions inbox. */
  detect(id: string, useAi: boolean): Observable<DetectResponse> {
    return this.http.post<DetectResponse>(`/api/profiles/${encodeURIComponent(id)}/detect`, { useAi });
  }

  mappings(): Observable<MappingListItem[]> {
    return this.http.get<MappingListItem[]>('/api/mappings');
  }

  mapping(id: string): Observable<MappingDocument> {
    return this.http.get<MappingDocument>(`/api/mappings/${encodeURIComponent(id)}`);
  }

  generateMapping(request: { source: string; target: string; id?: string; title?: string; replace?: boolean; useAi?: boolean }): Observable<MappingDocument> {
    return this.http.post<MappingDocument>('/api/mappings', request);
  }

  deleteMapping(id: string): Observable<unknown> {
    return this.http.delete(`/api/mappings/${encodeURIComponent(id)}`);
  }

  mappingSummary(id: string): Observable<MappingSummary> {
    return this.http.get<MappingSummary>(`/api/mappings/${encodeURIComponent(id)}/summary`);
  }

  exportUrl(id: string, format: 'xlsx' | 'csv' | 'html' | 'pdf'): string {
    return `/api/mappings/${encodeURIComponent(id)}/export/${format}`;
  }

  review(id: string, rowId: string, decision: ReviewDecisionKind, comment?: string, row?: FieldMapping): Observable<ReviewDecision> {
    return this.http.post<ReviewDecision>(`/api/mappings/${encodeURIComponent(id)}/rows/${encodeURIComponent(rowId)}/review`, { decision, comment, row });
  }

  reviews(id: string): Observable<ReviewDecision[]> {
    return this.http.get<ReviewDecision[]>(`/api/mappings/${encodeURIComponent(id)}/reviews`);
  }

  pendingSuggestions(): Observable<Suggestion[]> {
    return this.suggestions('pending');
  }

  suggestions(status?: SuggestionStatus): Observable<Suggestion[]> {
    return this.http.get<Suggestion[]>('/api/suggestions', { params: status ? { status } : {} });
  }

  approveSuggestion(id: number, request: { concept?: string; comment?: string; create?: boolean }): Observable<ApprovedSuggestion> {
    return this.http.post<ApprovedSuggestion>(`/api/suggestions/${id}/approve`, request);
  }

  rejectSuggestion(id: number, comment?: string): Observable<Suggestion> {
    return this.http.post<Suggestion>(`/api/suggestions/${id}/reject`, { comment });
  }

  /** Runs source samples through a mapping; with record the runs are saved on the mapping. */
  replay(id: string, files: File[], request: { target: string; xmlNamespace?: string; record?: boolean; mask?: boolean }): Observable<ReplayResponse> {
    const form = new FormData();
    for (const file of files) {
      form.append('files', file, file.name);
    }

    form.append('target', request.target);
    if (request.xmlNamespace) {
      form.append('xmlNamespace', request.xmlNamespace);
    }

    if (request.record) {
      form.append('record', 'true');
    }

    if (request.mask) {
      form.append('mask', 'true');
    }

    return this.http.post<ReplayResponse>(`/api/mappings/${encodeURIComponent(id)}/replay`, form);
  }
}
