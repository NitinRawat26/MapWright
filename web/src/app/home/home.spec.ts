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
});
