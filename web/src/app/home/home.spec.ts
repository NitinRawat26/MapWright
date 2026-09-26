import { TestBed } from '@angular/core/testing';
import { apiProviders, respond, settle, text } from '../testing';
import { Home } from './home';

describe('Home', () => {
  it('shows the API, AI and store counts', async () => {
    TestBed.configureTestingModule({ imports: [Home], providers: apiProviders() });
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();

    respond('/health', { status: 'ok' });
    respond('/api/ai', { available: false, maxConfidence: 70 });
    respond('/api/playbooks', [{ status: 'published' }, { status: 'published' }, { status: 'draft' }]);
    respond('/api/profiles', [{ id: 'a' }, { id: 'b' }]);
    respond('/api/mappings', [{ id: 'a__b' }]);
    respond('/api/suggestions?status=pending', []);
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(text(root, 'health')).toBe('API connected');
    expect(text(root, 'ai')).toBe('AI not configured: playbooks only');
    expect([text(root, 'playbooks'), text(root, 'profiles'), text(root, 'mappings'), text(root, 'pending')]).toEqual(['2', '2', '1', '0']);
  });

  it('shows recent mappings, how targets were filled, what needs attention and recent changes', async () => {
    TestBed.configureTestingModule({ imports: [Home], providers: apiProviders() });
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();
    const summary = (over: object) => ({
      totalTargetFields: 10,
      mappedTargetFields: 8,
      unmappedTargetFields: 2,
      requiredTargetFields: 6,
      requiredTargetFieldsMapped: 5,
      requiredCoveragePercent: 83.3,
      byConfidenceBand: { high: 6, medium: 2, low: 0 },
      byReviewStatus: { autoAccepted: 6, needsReview: 2, approved: 0, rejected: 0, overridden: 0 },
      byOrigin: { playbook: 6, nameMatch: 1, ai: 1, reviewer: 0, other: 0 },
      validationPassed: 0,
      validationFailed: 0,
      ...over,
    });

    respond('/health', { status: 'ok' });
    respond('/api/ai', { available: true, provider: 'ollama', maxConfidence: 70 });
    respond('/api/playbooks', [{ id: 'domain/tax-id', version: '1.1.0', reference: 'domain/tax-id@1.1.0', status: 'inReview', updatedAt: '2026-09-24T10:00:00Z', updatedBy: 'ben' }]);
    respond('/api/profiles', [
      { id: 'a', system: 'SalesAlpha', updatedAt: '2026-09-23T10:00:00Z', updatedBy: 'ana' },
      { id: 'b', system: 'UW Core', updatedAt: '2026-09-23T11:00:00Z', updatedBy: 'ana' },
    ]);
    respond('/api/mappings', [
      { id: 'a__b', sourceSystem: 'SalesAlpha', targetSystem: 'UW Core', updatedAt: '2026-09-24T09:00:00Z', updatedBy: 'ana', summary: summary({}) },
      {
        id: 'c__b',
        sourceSystem: 'SalesGamma',
        targetSystem: 'UW Core',
        updatedAt: '2026-09-24T11:00:00Z',
        updatedBy: 'cy',
        summary: summary({
          mappedTargetFields: 10,
          unmappedTargetFields: 0,
          requiredTargetFieldsMapped: 6,
          byReviewStatus: { autoAccepted: 10, needsReview: 0, approved: 0, rejected: 0, overridden: 0 },
          byOrigin: { playbook: 9, nameMatch: 1, ai: 0, reviewer: 0, other: 0 },
          validationPassed: 3,
        }),
      },
    ]);
    respond('/api/suggestions?status=pending', [{ id: 1 }]);
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect([...root.querySelectorAll('[data-mapping]')].map((r) => r.getAttribute('data-mapping'))).toEqual(['c__b', 'a__b']);
    expect(root.querySelector('[data-mapping="a__b"]')?.textContent).toContain('8/10');
    expect(root.querySelector('[data-mapping="a__b"]')?.textContent).toContain('In review');
    expect(root.querySelector('[data-mapping="a__b"] [data-testid="ai-rows"]')?.textContent?.trim()).toBe('1 AI');
    expect(root.querySelector('[data-mapping="c__b"]')?.textContent).toContain('Reviewed');
    expect(text(root, 'in-review')).toBe('1 waiting for review');

    expect(text(root, 'covered')).toBe('90%');
    expect(text(root, 'legend')).toContain('Playbook rules75%');
    expect(text(root, 'legend')).toContain('AI suggested5%');
    expect(text(root, 'legend')).toContain('Unmapped10%');

    expect([...root.querySelectorAll('[data-testid="attention"]')].map((a) => a.textContent?.trim())).toEqual([
      '1Required target fields unmapped in SalesAlpha → UW Core',
      '2Rows waiting for review in SalesAlpha → UW Core',
      '1AI suggestions waiting for a decision',
      '1Playbook versions waiting for a second reviewer',
    ]);
    expect([...root.querySelectorAll('[data-testid="step"]')].map((s) => s.classList.contains('done'))).toEqual([true, true, true, true]);
    const changes = [...root.querySelectorAll('[data-testid="change"]')].map((c) => c.textContent?.replace(/\s+/g, ' ').trim());
    expect(changes[0]).toMatch(/^cy saved mapping SalesGamma → UW Core/);
    expect(changes[1]).toMatch(/^ben sent for review domain\/tax-id@1\.1\.0/);
    expect(changes.length).toBe(5);
  });

  it('says so when nothing needs attention', async () => {
    TestBed.configureTestingModule({ imports: [Home], providers: apiProviders() });
    const fixture = TestBed.createComponent(Home);
    fixture.detectChanges();
    for (const url of ['/api/playbooks', '/api/profiles', '/api/mappings', '/api/suggestions?status=pending']) {
      respond(url, []);
    }
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(text(root, 'attention-none')).toBe('Nothing needs attention.');
    expect(text(root, 'covered')).toBe('0%');
    expect(root.querySelector('[data-testid="recent"]')).toBeNull();
  });
});
