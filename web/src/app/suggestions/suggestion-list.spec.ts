import { TestBed } from '@angular/core/testing';
import { UserService } from '../core/user';
import { apiProviders, http, respond, settle, text } from '../testing';
import { SuggestionList } from './suggestion-list';

const suggestion = (id: number, status = 'pending') => ({
  id, profileId: 'salesalpha-crm', system: 'SalesAlpha CRM', status,
  content: {
    path: `$.account.f${id}`, fieldName: `f${id}`, businessConcept: 'LegalEntity.TaxId', domainPlaybook: 'domain/tax-id@1.0.0',
    meaning: 'The merchant tax number.', confidencePercent: 70, reasoning: 'Name and shape.', provider: 'fake', model: 'm1',
  },
  createdAt: '2026-09-25T00:00:00Z', createdBy: 'ana',
});

describe('SuggestionList', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [SuggestionList], providers: apiProviders() });
    TestBed.inject(UserService).set('bo');
  });

  async function open() {
    const fixture = TestBed.createComponent(SuggestionList);
    fixture.detectChanges();
    await settle(fixture);
    respond('/api/suggestions?status=pending', [suggestion(1), suggestion(2)]);
    await settle(fixture);
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('approves with the AI concept and removes the suggestion from the pending list', async () => {
    const { fixture, root } = await open();
    expect(root.querySelectorAll('[data-suggestion]').length).toBe(2);
    const first = root.querySelector('[data-suggestion="1"]') as HTMLElement;
    (first.querySelector('[data-testid="approve"]') as HTMLButtonElement).click();
    const approve = http().expectOne({ url: '/api/suggestions/1/approve', method: 'POST' });
    expect(approve.request.body).toEqual({ concept: undefined, comment: undefined });
    expect(approve.request.headers.get('X-MapWright-User')).toBe('bo');
    approve.flush({ suggestion: { ...suggestion(1, 'approved'), playbook: 'domain/tax-id@1.1.0' }, playbookId: 'domain/tax-id', version: '1.1.0' });
    await settle(fixture);
    expect([...root.querySelectorAll('[data-suggestion]')].map((s) => s.getAttribute('data-suggestion'))).toEqual(['2']);
  });

  it('shows the mapping row an AI pairing fills and approves only the row without a concept', async () => {
    const fixture = TestBed.createComponent(SuggestionList);
    fixture.detectChanges();
    await settle(fixture);
    const base = suggestion(7);
    const pairing = { mappingId: 'sales-alpha__uw-core', rowId: 'M021', targetSystem: 'UW Core', target: '/Request/EstablishedDate', targetField: 'EstablishedDate', sources: ['$.account.f7'] };
    const paired = { ...base, content: { ...base.content, businessConcept: undefined, domainPlaybook: undefined, mapping: pairing } };
    respond('/api/suggestions?status=pending', [paired]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(text(root, 'pairing')).toContain('Source of /Request/EstablishedDate in UW Core');
    expect(root.querySelector('[data-testid="pairing"] a')?.getAttribute('href')).toBe('/mappings/sales-alpha__uw-core');

    const approveButton = root.querySelector('[data-suggestion="7"] [data-testid="approve"]') as HTMLButtonElement;
    expect(approveButton.disabled).toBe(false);
    approveButton.click();
    const approve = http().expectOne('/api/suggestions/7/approve');
    expect(approve.request.body).toEqual({ concept: undefined, comment: undefined, create: undefined });
    approve.flush({ suggestion: { ...paired, status: 'approved' }, created: false });
    await settle(fixture);
    expect(root.querySelectorAll('[data-suggestion]').length).toBe(0);
    expect(document.body.textContent).toContain('Approved row M021 of sales-alpha__uw-core.');
  });

  it('ticks create for a proposed concept and sends it', async () => {
    const fixture = TestBed.createComponent(SuggestionList);
    fixture.detectChanges();
    await settle(fixture);
    const proposed = suggestion(4);
    respond('/api/suggestions?status=pending', [
      { ...proposed, content: { ...proposed.content, businessConcept: undefined, domainPlaybook: undefined, proposedConcept: 'Merchant.Mcc' } },
      suggestion(5),
    ]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const create = (id: number) => root.querySelector(`[data-suggestion="${id}"] [data-testid="create"] input`) as HTMLInputElement;
    expect(create(4).checked).toBe(true);
    expect(create(5).checked).toBe(false);

    (root.querySelector('[data-suggestion="4"] [data-testid="approve"]') as HTMLButtonElement).click();
    const approve = http().expectOne('/api/suggestions/4/approve');
    expect(approve.request.body).toEqual({ concept: 'Merchant.Mcc', comment: undefined, create: true });
    approve.flush({ suggestion: { ...suggestion(4, 'approved'), playbook: 'domain/merchant@0.1.0' }, playbookId: 'domain/merchant', version: '0.1.0', created: true });
    await settle(fixture);
    expect([...root.querySelectorAll('[data-suggestion]')].map((s) => s.getAttribute('data-suggestion'))).toEqual(['5']);
  });

  it('sends a changed concept and a rejection comment, and shows decided suggestions', async () => {
    const { fixture, root } = await open();
    const second = root.querySelector('[data-suggestion="2"]') as HTMLElement;
    const concept = second.querySelector('[data-testid="concept"]') as HTMLInputElement;
    concept.value = 'LegalEntity';
    concept.dispatchEvent(new Event('input'));
    await settle(fixture);
    (second.querySelector('[data-testid="approve"]') as HTMLButtonElement).click();
    expect(http().expectOne('/api/suggestions/2/approve').request.body).toEqual({ concept: 'LegalEntity', comment: undefined });

    const first = root.querySelector('[data-suggestion="1"]') as HTMLElement;
    const comment = first.querySelector('[data-testid="comment"]') as HTMLInputElement;
    comment.value = 'Internal id';
    comment.dispatchEvent(new Event('input'));
    await settle(fixture);
    (first.querySelector('[data-testid="reject"]') as HTMLButtonElement).click();
    expect(http().expectOne('/api/suggestions/1/reject').request.body).toEqual({ comment: 'Internal id' });

    (root.querySelectorAll('[data-testid="status-filter"] button')[1] as HTMLButtonElement).click();
    await settle(fixture);
    respond('/api/suggestions?status=approved', [{ ...suggestion(3, 'approved'), decidedBy: 'bo', decidedAt: '2026-09-25T01:00:00Z', playbook: 'domain/tax-id@1.1.0' }]);
    await settle(fixture);
    expect(text(root, 'decision')).toContain('Approved by bo');
    expect(root.querySelector('[data-testid="decision"] a')?.getAttribute('href')).toBe('/playbooks/domain/tax-id/1.1.0');
  });
});
