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

  it('shows guidance and file types for each preset and fills in an editable description', async () => {
    const fixture = TestBed.createComponent(ProfileList);
    fixture.detectChanges();
    respond('/api/profiles', []);
    respond('/api/ai', { available: false, maxConfidence: 70 });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const presets = Array.from(root.querySelectorAll('[data-testid="preset"] [data-preset]')).map((e) => e.textContent?.trim());
    expect(presets).toEqual(['JSON REST API', 'SOAP/XML service', 'XML file or batch', 'Field spec', 'Data dictionary', 'PDF/Word spec', 'Samples only', 'Mixed/custom']);
    expect(text(root, 'preset-guide')).toContain('Mixed/custom');
    const picker = root.querySelector('[data-testid="files"]') as HTMLInputElement;
    const description = () => (root.querySelector('[data-testid="description"]') as HTMLInputElement).value;

    (root.querySelector('[data-preset="data-dictionary"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(picker.getAttribute('accept')).toBe('.csv,.xlsx,.json,.xml');
    expect(text(root, 'preset-guide')).toContain('Nullable');
    expect(description()).toBe('From the data dictionary');

    (root.querySelector('[data-preset="document"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(picker.getAttribute('accept')).toBe('.pdf,.docx,.json,.xml');
    expect(description()).toBe('From the interface specification');

    const input = root.querySelector('[data-testid="description"]') as HTMLInputElement;
    input.value = 'Sales Beta v3.1 spec';
    input.dispatchEvent(new Event('input'));
    (root.querySelector('[data-preset="soap-xml"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(picker.getAttribute('accept')).toBe('.xml,.xsd,.wsdl');
    expect(description()).toBe('Sales Beta v3.1 spec');
    expect((root.querySelector('[data-testid="root"]') as HTMLInputElement).placeholder).toContain('WSDL operation');
  });

  it('warns about files that do not fit the preset but still uploads them', async () => {
    const fixture = TestBed.createComponent(ProfileList);
    fixture.detectChanges();
    respond('/api/profiles', []);
    respond('/api/ai', { available: false, maxConfidence: 70 });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    (root.querySelector('[data-preset="json-rest"] button') as HTMLButtonElement).click();
    await settle(fixture);
    const system = root.querySelector('[data-testid="system"]') as HTMLInputElement;
    system.value = 'Sales Beta';
    system.dispatchEvent(new Event('input'));
    const picker = root.querySelector('[data-testid="files"]') as HTMLInputElement;
    Object.defineProperty(picker, 'files', { value: [new File(['{}'], 'a.json'), new File(['%PDF'], 'spec.pdf')], configurable: true });
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(text(root, 'outside')).toContain('spec.pdf');
    expect(text(root, 'outside')).not.toContain('a.json');

    (root.querySelector('[data-testid="build"]') as HTMLButtonElement).click();
    const request = http().expectOne({ url: '/api/profiles', method: 'POST' });
    const form = request.request.body as FormData;
    expect(form.getAll('files').map((f) => (f as File).name)).toEqual(['a.json', 'spec.pdf']);
    expect(form.get('description')).toBe('JSON REST API');

    (root.querySelector('[data-preset="custom"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(root.querySelector('[data-testid="outside"]')).toBeNull();
  });

  it('offers AI for document text only when a PDF or Word file is added and AI is configured', async () => {
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(ProfileList);
    fixture.detectChanges();
    respond('/api/profiles', []);
    respond('/api/ai', { available: true, provider: 'vertex', maxConfidence: 70 });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="files"]')?.getAttribute('accept')).toContain('.pdf,.docx');
    expect(root.querySelector('[data-testid="use-ai"]')).toBeNull();

    const system = root.querySelector('[data-testid="system"]') as HTMLInputElement;
    system.value = 'Sales Beta';
    system.dispatchEvent(new Event('input'));
    const picker = root.querySelector('[data-testid="files"]') as HTMLInputElement;
    Object.defineProperty(picker, 'files', { value: [new File(['%PDF'], 'spec.PDF')], configurable: true });
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(text(root, 'ai-note')).toContain('nothing is sent to AI');

    (root.querySelector('[data-testid="use-ai"] input') as HTMLInputElement).click();
    await settle(fixture);
    expect(text(root, 'ai-note')).toContain('sent to vertex');
    expect(text(root, 'ai-note')).toContain('70%');

    (root.querySelector('[data-testid="build"]') as HTMLButtonElement).click();
    const request = http().expectOne({ url: '/api/profiles', method: 'POST' });
    expect((request.request.body as FormData).get('useAi')).toBe('true');
  });

  it('does not send useAi when AI is not configured', async () => {
    const fixture = TestBed.createComponent(ProfileList);
    fixture.detectChanges();
    respond('/api/profiles', []);
    respond('/api/ai', { available: false, maxConfidence: 70 });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const picker = root.querySelector('[data-testid="files"]') as HTMLInputElement;
    Object.defineProperty(picker, 'files', { value: [new File(['x'], 'spec.docx')], configurable: true });
    picker.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(root.querySelector('[data-testid="use-ai"] input')?.hasAttribute('disabled')).toBe(true);
    expect(text(root, 'ai-note')).toContain('AI is not configured');
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

  it('shows the saved detection result and warns when it is out of date', async () => {
    const fixture = TestBed.createComponent(ProfileDetail);
    fixture.componentRef.setInput('id', 'salesalpha-crm');
    fixture.detectChanges();
    respond('/api/ai', { available: false, maxConfidence: 70 });
    respond('/api/profiles/salesalpha-crm', profile);
    respond('/api/profiles/salesalpha-crm/detection', {
      system: 'SalesAlpha CRM',
      recognised: [{ path: '$.account.taxId', detection: { playbook: 'domain/tax-id@1.0.0', concept: 'LegalEntity', businessConcept: 'LegalEntity.TaxId', score: 95, requiresReview: false } }],
      suggestions: [],
      remaining: ['$.applicationId'],
      warnings: [],
      usedAi: false,
      detectedAt: '2026-09-25T10:00:00Z',
      detectedBy: 'ben',
      stale: ['The profile has been saved again since detection ran.'],
    });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;

    expect(text(root, 'detected')).toBe('Recognised 1 of 2 fields');
    expect(text(root, 'detected-at')).toContain('by ben, playbooks only');
    expect(text(root, 'stale')).toBe('The profile has been saved again since detection ran. Run detection again to update it.');
  });

  it('shows no result when detection has not run', async () => {
    const fixture = TestBed.createComponent(ProfileDetail);
    fixture.componentRef.setInput('id', 'salesalpha-crm');
    fixture.detectChanges();
    respond('/api/ai', { available: false, maxConfidence: 70 });
    respond('/api/profiles/salesalpha-crm', profile);
    respond('/api/profiles/salesalpha-crm/detection', null);
    await settle(fixture);

    expect(text(fixture.nativeElement as HTMLElement, 'detected')).toBe('');
  });
});
