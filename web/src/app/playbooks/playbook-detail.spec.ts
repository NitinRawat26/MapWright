import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { UserService } from '../core/user';
import { apiProviders, http, respond, settle, text } from '../testing';
import { PlaybookDetail } from './playbook-detail';

const playbook = (version: string, status: string, description: string) => `# Tax ID playbook.
specVersion: '1.0'
id: domain/tax-id
name: Tax ID
kind: domain
version: '${version}'
status: ${status}
description: ${description}
domain:
  concept:
    name: LegalEntity
    attributes:
      - name: TaxId
  # Terms seen in other systems.
  vocabulary:
    - term: tin
`;

const versions = [
  { id: 'domain/tax-id', version: '1.0.0', status: 'published' },
  { id: 'domain/tax-id', version: '1.1.0', status: 'draft' },
];

const imported = { sequence: 1, id: 'domain/tax-id', version: '1.0.0', actor: 'seed', action: 'imported', to: 'published', occurredAt: '2026-09-25T00:00:00Z' };

async function open(version: string, status: string, history: object[] = [imported]) {
  const fixture = TestBed.createComponent(PlaybookDetail);
  fixture.componentRef.setInput('kind', 'domain');
  fixture.componentRef.setInput('slug', 'tax-id');
  fixture.componentRef.setInput('version', version);
  fixture.detectChanges();
  respond(`/api/playbooks/domain/tax-id/${version}?format=yaml`, playbook(version, status, 'Tax IDs.'));
  respond('/api/playbooks/domain/tax-id', versions);
  respond('/api/playbooks/domain/tax-id/history', history);
  respond('/api/playbooks/domain/tax-id/1.0.0?format=yaml', playbook('1.0.0', 'published', 'Tax IDs.'));
  respond('/api/playbooks/domain/tax-id/1.1.0?format=yaml', playbook('1.1.0', 'draft', 'Tax IDs.'));
  await settle(fixture);
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

function edit(root: HTMLElement, value: string) {
  const editor = root.querySelector('[data-testid="editor"]') as HTMLTextAreaElement | null;
  if (!editor) {
    throw new Error('editor not rendered');
  }
  editor.value = value;
  editor.dispatchEvent(new Event('input'));
}

function click(root: HTMLElement, testId: string) {
  (root.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();
}

describe('PlaybookDetail', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [PlaybookDetail], providers: apiProviders() });
    TestBed.inject(UserService).set('ana');
  });

  it('shows a published version read-only with retire and new-version actions', async () => {
    const { root } = await open('1.0.0', 'published');

    expect(text(root, 'status')).toBe('Published');
    expect(root.querySelector('[data-testid="save"]')).toBeNull();
    expect(root.querySelector('[data-testid="to-retired"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="versions"]')?.textContent).toContain('1.1.0 · Draft');

    click(root, 'new-version');
    const request = http().expectOne({ url: '/api/playbooks/domain/tax-id/1.0.0/versions', method: 'POST' });
    expect(request.request.body).toEqual({ version: undefined, note: undefined });
  });

  it('edits, checks unsaved changes, saves and submits a draft', async () => {
    const { fixture, root } = await open('1.1.0', 'draft');
    const tabs = root.querySelectorAll('[role="tab"]');
    (tabs[1] as HTMLElement).click();
    await settle(fixture);

    expect((root.querySelector('[data-testid="editor"]') as HTMLTextAreaElement).value).toContain('# Terms seen in other systems.');
    edit(root, 'id: [unclosed');
    await settle(fixture);
    expect(text(root, 'parse-error')).toContain('Not valid YAML');
    expect((root.querySelector('[data-testid="validate"]') as HTMLButtonElement).disabled).toBe(true);

    const changed = playbook('1.1.0', 'draft', 'Tax IDs incl. TIN.');
    edit(root, changed);
    await settle(fixture);
    expect(root.querySelector('[data-testid="parse-error"]')).toBeNull();
    expect((root.querySelector('[data-testid="to-inReview"]') as HTMLButtonElement).disabled).toBe(true);

    click(root, 'validate');
    const validate = http().expectOne({ url: '/api/playbooks/validate', method: 'POST' });
    expect(validate.request.body).toBe(changed);
    expect(validate.request.headers.get('Content-Type')).toBe('application/yaml');
    validate.flush({ valid: false, issues: [{ severity: 'error', code: 'PB010', location: '$.domain', message: 'Bad.' }] });
    await settle(fixture);
    expect(text(root, 'valid')).toBe('Invalid');

    (root.querySelectorAll('[role="tab"]')[1] as HTMLElement).click();
    await settle(fixture);
    click(root, 'save');
    const save = http().expectOne({ url: '/api/playbooks/domain/tax-id/1.1.0?format=yaml', method: 'PUT' });
    expect(save.request.body).toBe(changed);
    expect(save.request.headers.get('X-MapWright-User')).toBe('ana');
    save.flush(changed);
    respond('/api/playbooks/domain/tax-id/history', []);
    await settle(fixture);

    click(root, 'to-inReview');
    const submit = http().expectOne({ url: '/api/playbooks/domain/tax-id/1.1.0/status?format=yaml', method: 'POST' });
    expect(submit.request.body).toEqual({ status: 'inReview', note: undefined });
    submit.flush(playbook('1.1.0', 'inReview', 'Tax IDs incl. TIN.'));
    await settle(fixture);
    expect(text(root, 'status')).toBe('In review');
    expect(root.querySelector('[data-testid="to-published"]')).not.toBeNull();
    expect((root.querySelector('[data-testid="editor"]') as HTMLTextAreaElement).value).toContain('# Tax ID playbook.');
  });

  it('deletes a draft that was never submitted', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { fixture, root } = await open('1.1.0', 'draft');

    expect(root.querySelector('[data-testid="to-abandoned"]')?.textContent).toContain('Abandon draft');
    click(root, 'delete-draft');
    const request = http().expectOne({ url: '/api/playbooks/domain/tax-id/1.1.0', method: 'DELETE' });
    expect(request.request.headers.get('X-MapWright-User')).toBe('ana');
    request.flush(null);
    await settle(fixture);
    expect(navigate).toHaveBeenCalledWith(['/playbooks']);
  });

  it('only abandons a draft that has been submitted, and shows abandoned versions read-only', async () => {
    const submitted = { ...imported, sequence: 2, version: '1.1.0', actor: 'ana', action: 'submitted', from: 'draft', to: 'inReview' };
    const draft = await open('1.1.0', 'draft', [imported, submitted]);
    expect(draft.root.querySelector('[data-testid="delete-draft"]')).toBeNull();
    expect(draft.root.querySelector('[data-testid="to-abandoned"]')).not.toBeNull();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ imports: [PlaybookDetail], providers: apiProviders() });
    TestBed.inject(UserService).set('ana');
    const deleted = { ...imported, sequence: 3, version: '1.2.0', actor: 'ana', action: 'deleted', from: 'draft', to: undefined };
    const { fixture, root } = await open('1.1.0', 'abandoned', [imported, deleted]);
    expect(text(root, 'status')).toBe('Abandoned');
    expect(root.querySelector('[data-testid="delete-draft"]')).toBeNull();
    expect(root.querySelector('[data-testid="new-version"]')).not.toBeNull();
    (root.querySelectorAll('[role="tab"]')[3] as HTMLElement).click();
    await settle(fixture);
    expect(root.querySelector('[data-testid="history"]')?.textContent).toContain('Draft → Deleted');
  });

  it('compares the draft side by side with the previous version', async () => {
    const { fixture, root } = await open('1.1.0', 'draft');
    (root.querySelectorAll('[role="tab"]')[1] as HTMLElement).click();
    await settle(fixture);
    edit(root, playbook('1.1.0', 'draft', 'Changed.'));
    (root.querySelectorAll('[role="tab"]')[4] as HTMLElement).click();
    await settle(fixture);

    expect(text(root, 'changes')).toContain('3 changed line(s): 1.0.0 on the left, 1.1.0 (unsaved) on the right');
    const changed = [...root.querySelectorAll('[data-testid="diff"] tr.changed')].map((r) =>
      [...r.querySelectorAll('td')].map((c) => c.textContent?.trim()).join(' | '),
    );
    expect(changed).toEqual([
      "6 | version: '1.0.0' | 6 | version: '1.1.0'",
      '7 | status: published | 7 | status: draft',
      '8 | description: Tax IDs. | 8 | description: Changed.',
    ]);
  });
});
