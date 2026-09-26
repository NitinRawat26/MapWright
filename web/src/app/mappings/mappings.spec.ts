import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { UserService } from '../core/user';
import { apiProviders, http, respond, settle, text } from '../testing';
import { MappingDetail } from './mapping-detail';
import { MappingList } from './mapping-list';

const field = (path: string) => ({ name: path.split(/[./]/).pop(), path, dataType: 'string', required: true });

const row = (id: string, status: string, type = 'oneToOne') => ({
  id, type,
  sources: type === 'unmapped' ? [] : [field('$.account.dbaName')],
  target: field(`/Request/${id}`),
  transformation: { type: 'rename', rule: 'Copy dbaName', valueMap: [] },
  confidencePercent: 60,
  reasoning: 'Matched by field name only.',
  review: { status },
});

const mapping = {
  specVersion: '1.0', id: 'a__b', title: 'A → B', version: '0.1.0', createdAt: '2026-09-25T00:00:00Z',
  source: { name: 'A', format: 'json' }, target: { name: 'B', format: 'xml' },
  confidencePolicy: { highThreshold: 85, mediumThreshold: 60 },
  mappings: [row('M001', 'needsReview'), row('M002', 'autoAccepted'), row('M003', 'needsReview', 'unmapped')],
};

const summary = (open: number, approved: number) => ({
  totalTargetFields: 3, mappedTargetFields: 2, unmappedTargetFields: 1, requiredTargetFields: 3, requiredTargetFieldsMapped: 2,
  requiredCoveragePercent: 66.7, byConfidenceBand: { high: 1, medium: 1, low: 0 },
  byReviewStatus: { autoAccepted: 1, needsReview: open, approved, rejected: 0, overridden: 0 },
  orphanSourceFields: 0, conflicts: 0, assumptions: 0, validationPassed: 0, validationFailed: 0, validationSkipped: 0,
});

async function open() {
  const fixture = TestBed.createComponent(MappingDetail);
  fixture.componentRef.setInput('id', 'a__b');
  fixture.detectChanges();
  respond('/api/mappings/a__b', mapping);
  respond('/api/mappings/a__b/summary', summary(2, 0));
  await settle(fixture);
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

describe('Mappings', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [MappingList, MappingDetail], providers: apiProviders() });
    TestBed.inject(UserService).set('ana');
  });

  it('generates a mapping from two profiles', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(MappingList);
    fixture.componentRef.setInput('source', 'a');
    fixture.detectChanges();
    respond('/api/mappings', []);
    respond('/api/ai', { available: false, maxConfidence: 70 });
    respond('/api/profiles', [
      { id: 'a', system: 'A', format: 'json', fieldCount: 1, createdAt: '', updatedAt: '', updatedBy: 'x' },
      { id: 'b', system: 'B', format: 'xml', fieldCount: 1, createdAt: '', updatedAt: '', updatedBy: 'x' },
    ]);
    await settle(fixture);
    const component = fixture.componentInstance as unknown as { patch(change: { target: string }): void };
    component.patch({ target: 'b' });
    await settle(fixture);

    (fixture.nativeElement.querySelector('[data-testid="generate"]') as HTMLButtonElement).click();
    const request = http().expectOne({ url: '/api/mappings', method: 'POST' });
    expect(request.request.body).toEqual({ source: 'a', target: 'b', title: undefined, id: undefined, replace: false, useAi: false });
    request.flush(mapping);
    expect(navigate).toHaveBeenCalledWith(['/mappings', 'a__b']);
  });

  it('shows the summary, filters rows and links the exports', async () => {
    const { fixture, root } = await open();
    expect(text(root, 'needs-review')).toBe('2');
    expect(root.querySelector('[data-testid="export-xlsx"]')?.getAttribute('href')).toBe('/api/mappings/a__b/export/xlsx');
    expect(root.querySelector('[data-testid="export-pdf"]')?.getAttribute('href')).toBe('/api/mappings/a__b/export/pdf');
    expect(root.querySelectorAll('tr[data-row]').length).toBe(3);

    (root.querySelectorAll('[data-testid="filter"] button')[2] as HTMLButtonElement).click();
    await settle(fixture);
    expect([...root.querySelectorAll('tr[data-row]')].map((r) => r.getAttribute('data-row'))).toEqual(['M003']);
  });

  it('shows what the AI pass reported', async () => {
    const fixture = TestBed.createComponent(MappingDetail);
    fixture.componentRef.setInput('id', 'a__b');
    fixture.detectChanges();
    respond('/api/mappings/a__b', {
      ...mapping,
      aiPass: { provider: 'fake/model', maxConfidence: 70, suggestedRows: ['M001'], unmatched: ['/Request/M003'], warnings: ['fake: ignored a pairing for \'/Request/X\'.'] },
    });
    respond('/api/mappings/a__b/summary', summary(2, 0));
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;

    expect(text(root, 'ai-pass')).toContain('suggested sources for 1 row(s); 1 target field(s) were still unmatched');
    expect(text(root, 'ai-unmatched')).toBe('/Request/M003');
    expect(text(root, 'ai-warning')).toBe("fake: ignored a pairing for '/Request/X'.");
  });

  it('tags the rows AI suggested and filters to them', async () => {
    const fixture = TestBed.createComponent(MappingDetail);
    fixture.componentRef.setInput('id', 'a__b');
    fixture.detectChanges();
    const ai = { ...row('M002', 'needsReview'), evidence: [{ kind: 'aiSuggestion', reference: 'ollama/qwen3', detail: 'Same meaning.' }] };
    respond('/api/mappings/a__b', { ...mapping, mappings: [mapping.mappings[0], ai, mapping.mappings[2]] });
    respond('/api/mappings/a__b/summary', summary(2, 0));
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('tr[data-row="M002"] [data-testid="ai-row"]')?.getAttribute('title')).toContain('ollama/qwen3');
    expect(root.querySelectorAll('[data-testid="ai-row"]').length).toBe(1);

    (root.querySelectorAll('[data-testid="filter"] button')[4] as HTMLButtonElement).click();
    await settle(fixture);
    expect([...root.querySelectorAll('tr[data-row]')].map((r) => r.getAttribute('data-row'))).toEqual(['M002']);
  });

  it('shows no AI panel without an AI pass', async () => {
    const { root } = await open();
    expect(root.querySelector('[data-testid="ai-pass"]')).toBeNull();
  });

  it('approves a row and overrides another', async () => {
    const { fixture, root } = await open();
    (root.querySelector('tr[data-row="M001"]') as HTMLElement).click();
    await settle(fixture);
    const comment = root.querySelector('[data-testid="comment"]') as HTMLInputElement;
    comment.value = 'Checked with the source team';
    comment.dispatchEvent(new Event('input'));
    (root.querySelector('[data-testid="approve"]') as HTMLButtonElement).click();
    const approve = http().expectOne({ url: '/api/mappings/a__b/rows/M001/review', method: 'POST' });
    expect(approve.request.body).toEqual({ decision: 'approve', comment: 'Checked with the source team', row: undefined });
    expect(approve.request.headers.get('X-MapWright-User')).toBe('ana');
    approve.flush({ sequence: 1, mappingId: 'a__b', rowId: 'M001', decision: 'approve', previousStatus: 'needsReview', reviewer: 'ana', row: { ...row('M001', 'approved'), review: { status: 'approved', reviewer: 'ana', reviewedOn: '2026-09-25' } }, decidedAt: '' });
    respond('/api/mappings/a__b/summary', summary(1, 1));
    await settle(fixture);
    expect(root.querySelector('tr[data-row="M001"]')?.textContent).toContain('Approved');
    expect(text(root, 'needs-review')).toBe('1');

    (root.querySelector('tr[data-row="M003"]') as HTMLElement).click();
    await settle(fixture);
    (root.querySelector('[data-testid="edit-override"]') as HTMLButtonElement).click();
    await settle(fixture);
    const editor = root.querySelector('[data-testid="override"]') as HTMLTextAreaElement;
    const edited = { ...JSON.parse(editor.value), type: 'constant', transformation: { type: 'direct', defaultValue: 'SALES_ALPHA', valueMap: [] } };
    editor.value = JSON.stringify(edited);
    editor.dispatchEvent(new Event('input'));
    await settle(fixture);
    (root.querySelector('[data-testid="save-override"]') as HTMLButtonElement).click();
    const override = http().expectOne({ url: '/api/mappings/a__b/rows/M003/review', method: 'POST' });
    expect(override.request.body.decision).toBe('override');
    expect(override.request.body.row).toMatchObject({ id: 'M003', type: 'constant', target: { path: '/Request/M003' }, review: { status: 'needsReview' } });
  });
});
