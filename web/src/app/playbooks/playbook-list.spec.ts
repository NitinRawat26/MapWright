import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { UserService } from '../core/user';
import { apiProviders, http, respond, settle } from '../testing';
import { PlaybookList } from './playbook-list';

const summary = (id: string, version: string, status: string) => ({
  id, version, status, name: id, kind: id.split('/')[0], createdAt: '2026-09-25T00:00:00Z', createdBy: 'seed',
  updatedAt: '2026-09-25T00:00:00Z', updatedBy: 'seed', reference: `${id}@${version}`,
});

describe('PlaybookList', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [PlaybookList], providers: apiProviders() });
  });

  it('lists playbooks, filters by status on the server and searches locally', async () => {
    const fixture = TestBed.createComponent(PlaybookList);
    fixture.detectChanges();
    respond('/api/playbooks', [summary('domain/tax-id', '1.0.0', 'published'), summary('process/onboard-new-system', '1.0.0', 'published')]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('tr.mat-mdc-row').length).toBe(2);
    expect(root.querySelector('a.mono')?.getAttribute('href')).toBe('/playbooks/domain/tax-id/1.0.0');

    (root.querySelectorAll('mat-button-toggle button')[1] as HTMLButtonElement).click();
    respond('/api/playbooks?status=draft', [summary('domain/tax-id', '1.1.0', 'draft')]);
    await settle(fixture);
    expect(root.querySelector('tbody')?.textContent).toContain('1.1.0');
    expect(root.querySelector('tbody')?.textContent).toContain('Draft');
  });

  it('leaves abandoned versions out of All and shows them under Abandoned', async () => {
    const fixture = TestBed.createComponent(PlaybookList);
    fixture.detectChanges();
    const abandoned = summary('domain/tax-id', '1.1.0', 'abandoned');
    respond('/api/playbooks', [summary('domain/tax-id', '1.0.0', 'published'), abandoned]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('tr.mat-mdc-row').length).toBe(1);
    expect(root.querySelector('tbody')?.textContent).not.toContain('1.1.0');

    (root.querySelectorAll('mat-button-toggle button')[5] as HTMLButtonElement).click();
    respond('/api/playbooks?status=abandoned', [abandoned]);
    await settle(fixture);
    expect(root.querySelectorAll('tr.mat-mdc-row').length).toBe(1);
    expect(root.querySelector('tbody')?.textContent).toContain('Abandoned');
  });

  it('creates a draft from pasted JSON once a name is set', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(PlaybookList);
    fixture.detectChanges();
    respond('/api/playbooks', []);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const textarea = root.querySelector('[data-testid="import-text"]') as HTMLTextAreaElement;
    textarea.value = '{"id":"domain/fees"}';
    textarea.dispatchEvent(new Event('input'));
    await settle(fixture);
    const button = root.querySelector('[data-testid="import"]') as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    TestBed.inject(UserService).set('ana');
    await settle(fixture);
    button.click();
    const request = http().expectOne({ url: '/api/playbooks', method: 'POST' });
    expect(request.request.body).toBe('{"id":"domain/fees"}');
    expect(request.request.headers.get('X-MapWright-User')).toBe('ana');
    expect(request.request.headers.get('Content-Type')).toBe('application/json');
    request.flush(JSON.stringify({ id: 'domain/fees', version: '1.0.0' }));
    expect(navigate).toHaveBeenCalledWith(['/playbooks', 'domain', 'fees', '1.0.0']);
  });

  it('creates a draft from pasted YAML, sent as YAML', async () => {
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    TestBed.inject(UserService).set('ana');
    const fixture = TestBed.createComponent(PlaybookList);
    fixture.detectChanges();
    respond('/api/playbooks', []);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const yaml = '# Fees.\nid: domain/fees\n';
    const textarea = root.querySelector('[data-testid="import-text"]') as HTMLTextAreaElement;
    textarea.value = yaml;
    textarea.dispatchEvent(new Event('input'));
    await settle(fixture);

    (root.querySelector('[data-testid="import"]') as HTMLButtonElement).click();
    const request = http().expectOne({ url: '/api/playbooks', method: 'POST' });
    expect(request.request.body).toBe(yaml);
    expect(request.request.headers.get('Content-Type')).toBe('application/yaml');
  });

  it('counts published, draft and in-review playbooks and opens a row on click', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(PlaybookList);
    fixture.detectChanges();
    respond('/api/playbooks', [
      summary('domain/tax-id', '1.0.0', 'published'),
      summary('domain/tax-id', '1.1.0', 'draft'),
      summary('domain/principals', '1.1.0', 'inReview'),
      summary('process/onboard-new-system', '1.0.0', 'published'),
    ]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const values = [...root.querySelectorAll('[data-testid="playbook-stats"] .value')].map((v) => v.textContent?.trim());
    expect(values).toEqual(['2', '1', '1', '3 · 1']);

    (root.querySelectorAll('tr.mat-mdc-row')[1] as HTMLElement).click();
    expect(navigate).toHaveBeenCalledWith(['/playbooks', 'domain', 'tax-id', '1.1.0']);
  });
});
