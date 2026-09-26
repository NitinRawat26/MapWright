import { TestBed } from '@angular/core/testing';
import { apiProviders, http, settle, text } from '../testing';
import { SettingsPage } from './settings';

const settings = {
  version: '1.0.0',
  ai: { available: true, provider: 'ollama/qwen3', maxConfidence: 70, timeoutSeconds: 600, ollama: { url: 'http://localhost:11434/', model: 'qwen3', contextTokens: 16384 } },
  signIn: { required: false, apiKeys: ['ci'], jwt: false, proxy: false },
  storage: { databasePath: '/data/mapwright.db', inMemory: false, sizeBytes: 2 * 1024 * 1024, requireIndependentReview: true },
  confidence: { highThreshold: 85, mediumThreshold: 60 },
  playbooks: { published: 6, domain: 5, process: 1 },
};

describe('SettingsPage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SettingsPage], providers: apiProviders() });
  });

  it('shows the AI provider, sign-in, API key names and storage', async () => {
    const fixture = TestBed.createComponent(SettingsPage);
    fixture.detectChanges();
    http().expectOne('/api/settings').flush(settings);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;

    expect(text(root, 'settings-ai')).toContain('ollama/qwen3');
    expect(text(root, 'settings-ai')).toContain('http://localhost:11434/');
    expect(text(root, 'settings-ai')).toContain('16,384 tokens');
    expect(root.querySelector('[data-testid="signin-off"]')).not.toBeNull();
    expect(text(root, 'settings-keys')).toContain('ci');
    expect(text(root, 'settings-storage')).toContain('2.0 MB');
    expect(root.querySelector('[data-testid="storage-temporary"]')).toBeNull();
    expect(text(root, 'settings-confidence')).toContain('60% to 84%');
    expect(text(root, 'settings-about')).toContain('6 (5 domain, 1 process)');
  });

  it('says when AI is not configured and the database is temporary', async () => {
    const fixture = TestBed.createComponent(SettingsPage);
    fixture.detectChanges();
    http().expectOne('/api/settings').flush({
      ...settings,
      ai: { available: false, maxConfidence: 70, timeoutSeconds: 180 },
      storage: { ...settings.storage, databasePath: ':memory:', inMemory: true, sizeBytes: undefined },
    });
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(text(root, 'settings-ai')).toContain('Not configured');
    expect(root.querySelector('[data-testid="storage-temporary"]')).not.toBeNull();
  });

  it('says when the settings cannot be loaded', async () => {
    const fixture = TestBed.createComponent(SettingsPage);
    fixture.detectChanges();
    http().expectOne('/api/settings').flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);
    expect(text(fixture.nativeElement as HTMLElement, 'settings-error')).toContain("couldn't be loaded");
  });
});
