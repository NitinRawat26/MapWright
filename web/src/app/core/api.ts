import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import {
  AiStatus,
  DetectResponse,
  FieldMapping,
  MappingDocument,
  MappingListItem,
  MappingSummary,
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

/** Typed client for the MapWright API. Reads and writes go through the user and error interceptors. */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  health(): Observable<{ status: string }> {
    return this.http.get<{ status: string }>('/health');
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

  /** The playbook exactly as stored, in the file format. */
  playbookJson(id: string, version: string): Observable<string> {
    return this.http.get(`/api/playbooks/${id}/${version}`, { responseType: 'text' });
  }

  createPlaybook(playbook: string): Observable<string> {
    return this.http.post('/api/playbooks', playbook, { headers: json, responseType: 'text' });
  }

  updateDraft(id: string, version: string, playbook: string): Observable<string> {
    return this.http.put(`/api/playbooks/${id}/${version}`, playbook, { headers: json, responseType: 'text' });
  }

  newVersion(id: string, from: string, request: { version?: string; note?: string }): Observable<string> {
    return this.http.post(`/api/playbooks/${id}/${from}/versions`, request, { responseType: 'text' });
  }

  changeStatus(id: string, version: string, status: PlaybookStatus, note?: string): Observable<string> {
    return this.http.post(`/api/playbooks/${id}/${version}/status`, { status, note }, { responseType: 'text' });
  }

  /** Validates unsaved playbook JSON against the published library. */
  validatePlaybook(playbook: string): Observable<ValidationResponse> {
    return this.http.post<ValidationResponse>('/api/playbooks/validate', playbook, { headers: json });
  }

  /** Runs the detection tests and rule examples of unsaved playbook JSON. */
  testPlaybook(playbook: string): Observable<TestResponse> {
    return this.http.post<TestResponse>('/api/playbooks/test', playbook, { headers: json });
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

  exportUrl(id: string, format: 'xlsx' | 'csv' | 'html'): string {
    return `/api/mappings/${encodeURIComponent(id)}/export/${format}`;
  }

  review(id: string, rowId: string, decision: ReviewDecisionKind, comment?: string, row?: FieldMapping): Observable<ReviewDecision> {
    return this.http.post<ReviewDecision>(`/api/mappings/${encodeURIComponent(id)}/rows/${encodeURIComponent(rowId)}/review`, { decision, comment, row });
  }

  reviews(id: string): Observable<ReviewDecision[]> {
    return this.http.get<ReviewDecision[]>(`/api/mappings/${encodeURIComponent(id)}/reviews`);
  }

  pendingSuggestions(): Observable<unknown[]> {
    return this.http.get<unknown[]>('/api/suggestions', { params: { status: 'pending' } });
  }
}
