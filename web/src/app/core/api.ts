import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AiStatus,
  MappingListItem,
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

  mappings(): Observable<MappingListItem[]> {
    return this.http.get<MappingListItem[]>('/api/mappings');
  }

  pendingSuggestions(): Observable<unknown[]> {
    return this.http.get<unknown[]>('/api/suggestions', { params: { status: 'pending' } });
  }
}
