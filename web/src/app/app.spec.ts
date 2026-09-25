import { TestBed } from '@angular/core/testing';
import { App } from './app';
import { ApiKeyHeader } from './core/http';
import { UserService } from './core/user';
import { apiProviders, http, respond, settle, text } from './testing';

describe('App sign-in', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    TestBed.configureTestingModule({ providers: apiProviders() });
  });

  it('asks for a name when the API does not require sign-in', async () => {
    const fixture = TestBed.createComponent(App);
    respond('/api/me', { method: 'header', signInRequired: false });
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="user"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="api-key"]')).toBeNull();
  });

  it('signs in with an API key, uses the verified name and signs out', async () => {
    localStorage.setItem('mapwright.user', 'typed name');
    const fixture = TestBed.createComponent(App);
    respond('/api/me', { signInRequired: true });
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    const user = TestBed.inject(UserService);
    expect(root.querySelector('[data-testid="user"]')).toBeNull();
    expect(user.name()).toBe('');

    const input = root.querySelector<HTMLInputElement>('[data-testid="api-key"]')!;
    input.value = ' mw_key ';
    input.dispatchEvent(new Event('change'));
    const me = http().expectOne('/api/me');
    expect(me.request.headers.get(ApiKeyHeader)).toBe('mw_key');
    me.flush({ name: 'ci-bot', method: 'apiKey', signInRequired: true });
    await settle(fixture);

    expect(text(root, 'signed-in')).toBe('Signed in as ci-bot');
    expect(user.name()).toBe('ci-bot');
    expect(sessionStorage.getItem('mapwright.apiKey')).toBe('mw_key');

    root.querySelector<HTMLButtonElement>('[data-testid="sign-out"]')!.click();
    const after = http().expectOne('/api/me');
    expect(after.request.headers.has(ApiKeyHeader)).toBe(false);
    after.flush({ signInRequired: true });
    await settle(fixture);
    expect(root.querySelector('[data-testid="api-key"]')).not.toBeNull();
    expect(sessionStorage.getItem('mapwright.apiKey')).toBeNull();
  });

  it('forgets a rejected key', async () => {
    sessionStorage.setItem('mapwright.apiKey', 'old');
    TestBed.inject(UserService).setApiKey('old');
    const fixture = TestBed.createComponent(App);
    http().expectOne('/api/me').flush({ title: 'Sign-in required', status: 401, detail: 'The API key is not valid.', issues: [] }, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);

    expect(TestBed.inject(UserService).apiKey()).toBe('');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="api-key"]')).not.toBeNull();
  });
});
