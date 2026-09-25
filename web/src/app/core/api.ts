import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AiStatus, MappingListItem, PlaybookStatus, PlaybookSummary, ProfileSummary } from './models';

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
