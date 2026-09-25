import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { UserService } from '../core/user';
import { apiProviders, http, respond, settle, text } from '../testing';
import { ProfileDetail } from './profile-detail';
import { ProfileList } from './profile-list';

const profile = {
  specVersion: '1.0', system: 'SalesAlpha CRM', format: 'json', createdAt: '2026-09-25T00:00:00Z',
  inputs: [{ name: 'a.json', kind: 'samplePayload' }],
  fields: [
    { path: '$.account.taxId', name: 'taxId', kind: 'value', cardinality: 'single', dataType: 'string', required: 'likelyRequired', sampleValue: '*****6789', sensitive: true },
    { path: '$.applicationId', name: 'applicationId', kind: 'value', cardinality: 'single', dataType: 'string', required: 'likelyRequired' },
  ],
  findings: [],
};

describe('Profiles', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [ProfileList, ProfileDetail], providers: apiProviders() });
    TestBed.inject(UserService).set('ana');
  });

  it('uploads files as multipart form data and opens the new profile', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(ProfileList);
    fixture.detectChanges();
    respond('/api/profiles', [{ id: 'uw-core', system: 'UW Core', format: 'xml', fieldCount: 30, createdAt: '', updatedAt: '', updatedBy: 'seed' }]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="profiles"]')?.textContent).toContain('UW Core');

    const system = root.querySelector('[data-testid="system"]') as HTMLInputElement;
    system.value = 'SalesAlpha CRM';
    system.dispatchEvent(new Event('input'));
    const picker = root.querySelector('[data-testid="files"]') as HTMLInputElement;
    const file = new File(['{"a":1}'], 'a.json', { type: 'application/json' });
    Object.defineProperty(picker, 'files', { value: [file], configurable: true });
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);

    (root.querySelector('[data-testid="build"]') as HTMLButtonElement).click();
    const request = http().expectOne({ url: '/api/profiles', method: 'POST' });
    const form = request.request.body as FormData;
    expect(form.get('system')).toBe('SalesAlpha CRM');
    expect((form.getAll('files')[0] as File).name).toBe('a.json');
    expect(form.has('id')).toBe(false);
    request.flush(profile, { headers: { Location: '/api/profiles/salesalpha-crm' }, status: 201, statusText: 'Created' });
    expect(navigate).toHaveBeenCalledWith(['/profiles', 'salesalpha-crm']);
  });

  it('shows fields and runs playbook-only detection when AI is not configured', async () => {
    const fixture = TestBed.createComponent(ProfileDetail);
    fixture.componentRef.setInput('id', 'salesalpha-crm');
    fixture.detectChanges();
    respond('/api/ai', { available: false, maxConfidence: 70 });
    respond('/api/profiles/salesalpha-crm', profile);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="fields"]')?.textContent).toContain('*****6789');
    expect(root.querySelector('[data-testid="use-ai"] input')?.hasAttribute('disabled')).toBe(true);

    (root.querySelector('[data-testid="detect"]') as HTMLButtonElement).click();
    const detect = http().expectOne({ url: '/api/profiles/salesalpha-crm/detect', method: 'POST' });
    expect(detect.request.body).toEqual({ useAi: false });
    detect.flush({
      system: 'SalesAlpha CRM',
      recognised: [{ path: '$.account.taxId', detection: { playbook: 'domain/tax-id@1.0.0', concept: 'LegalEntity', businessConcept: 'LegalEntity.TaxId', score: 95, requiresReview: false } }],
      suggestions: [],
      remaining: ['$.applicationId'],
      warnings: [],
    });
    await settle(fixture);
    expect(text(root, 'detected')).toBe('Recognised 1 of 2 fields');
    expect(root.textContent).toContain('LegalEntity.TaxId');
  });
});
