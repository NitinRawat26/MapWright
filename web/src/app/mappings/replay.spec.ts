import { TestBed } from '@angular/core/testing';
import { MappingDocument } from '../core/models';
import { UserService } from '../core/user';
import { apiProviders, http, respond, settle, text } from '../testing';
import { Replay } from './replay';

const mapping = {
  specVersion: '1.0', id: 'a__b', title: 'A → B', version: '0.1.0', createdAt: '',
  source: { name: 'A', format: 'json' }, target: { name: 'UW Core', format: 'xml' },
  confidencePolicy: { highThreshold: 85, mediumThreshold: 60 }, mappings: [],
  validationRuns: [{ id: 'V001', ranAt: '2026-09-25T00:00:00Z', samplePayload: 'old.json', results: [{ mappingId: 'M001', outcome: 'pass' }] }],
} as MappingDocument;

describe('Replay', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [Replay], providers: apiProviders() });
    TestBed.inject(UserService).set('ana');
  });

  it('replays samples against the matching target profile and shows the results', async () => {
    const fixture = TestBed.createComponent(Replay);
    fixture.componentRef.setInput('mapping', mapping);
    let recorded = 0;
    fixture.componentInstance.recorded.subscribe(() => recorded++);
    fixture.detectChanges();
    respond('/api/profiles', [
      { id: 'a', system: 'A', format: 'json', fieldCount: 1, createdAt: '', updatedAt: '', updatedBy: 'x' },
      { id: 'uw-core', system: 'UW Core', format: 'xml', fieldCount: 30, createdAt: '', updatedAt: '', updatedBy: 'x' },
    ]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="saved-runs"]')?.textContent).toContain('old.json');

    const picker = root.querySelector('[data-testid="replay-files"]') as HTMLInputElement;
    expect(picker.accept).toBe('.json');
    Object.defineProperty(picker, 'files', { value: [new File(['{}'], 'corp.json')], configurable: true });
    picker.dispatchEvent(new Event('change'));
    (root.querySelector('[data-testid="record"] input') as HTMLInputElement).click();
    await settle(fixture);
    (root.querySelector('[data-testid="replay"]') as HTMLButtonElement).click();

    const request = http().expectOne({ url: '/api/mappings/a__b/replay', method: 'POST' });
    const form = request.request.body as FormData;
    expect(form.get('target')).toBe('uw-core');
    expect(form.get('record')).toBe('true');
    expect((form.get('files') as File).name).toBe('corp.json');
    request.flush({
      recorded: true,
      samples: [{
        sample: 'corp.json', payload: '<UnderwritingRequest/>',
        run: { id: 'V002', ranAt: '', samplePayload: 'corp.json', results: [
          { mappingId: 'M001', outcome: 'pass' }, { mappingId: 'M002', outcome: 'fail', message: 'Required target has no value.' }, { mappingId: 'M003', outcome: 'skipped' },
        ] },
      }],
    });
    await settle(fixture);
    expect(text(root, 'failed-corp.json')).toBe('1 failed');
    expect(root.querySelector('[data-testid="results-corp.json"]')?.textContent).toContain('Required target has no value.');
    expect(text(root, 'payload')).toBe('<UnderwritingRequest/>');
    expect(recorded).toBe(1);
  });
});
