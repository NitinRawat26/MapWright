import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { apiProviders, http } from '../testing';
import { describeError, UserHeader } from './http';
import { UserService } from './user';

describe('http', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: apiProviders() });
  });

  it('sends the reviewer name once it is set, and remembers it', () => {
    const client = TestBed.inject(HttpClient);
    client.get('/api/profiles').subscribe();
    expect(http().expectOne('/api/profiles').request.headers.has(UserHeader)).toBe(false);

    TestBed.inject(UserService).set('  ana ');
    client.post('/api/mappings', {}).subscribe();
    expect(http().expectOne('/api/mappings').request.headers.get(UserHeader)).toBe('ana');
    expect(localStorage.getItem('mapwright.user')).toBe('ana');
  });

  it('describes API problems with their issues', () => {
    const problem = new HttpErrorResponse({
      status: 400,
      error: { title: 'Invalid', status: 400, detail: 'Playbook is invalid.', issues: [{ severity: 'error', code: 'PB001', location: '$.id', message: 'Missing id.' }] },
    });
    expect(describeError(problem)).toBe('Playbook is invalid.\nerror PB001 [$.id] Missing id.');
    const text = new HttpErrorResponse({
      status: 409,
      statusText: 'Conflict',
      error: JSON.stringify({ title: 'Conflict', status: 409, detail: "Playbook 'domain/tax-id@1.1.0' already exists.", issues: [] }),
    });
    expect(describeError(text)).toBe("Playbook 'domain/tax-id@1.1.0' already exists.");
    expect(describeError(new HttpErrorResponse({ status: 409, statusText: 'Conflict', error: 'not json' }))).toBe('409 Conflict');
    expect(describeError(new HttpErrorResponse({ status: 0 }))).toBe('The MapWright API is not reachable.');
    expect(describeError(new HttpErrorResponse({ status: 500, statusText: 'Server Error' }))).toBe('500 Server Error');
  });
});
