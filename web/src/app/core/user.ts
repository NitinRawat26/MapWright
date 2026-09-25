import { Injectable, computed, signal } from '@angular/core';
import { Me } from './models';

const Key = 'mapwright.user';
const ApiKeyKey = 'mapwright.apiKey';

/**
 * Who the changes are recorded for. Without sign-in it is the name typed in the toolbar (sent as X-MapWright-User);
 * once the API requires sign-in it is the verified name from /api/me, and the typed name is ignored.
 */
@Injectable({ providedIn: 'root' })
export class UserService {
  private readonly typed = signal(localStorage.getItem(Key) ?? '');

  /** The API's answer from /api/me, or null before it has answered. */
  readonly me = signal<Me | null>(null);

  /** Kept for this browser tab only. */
  readonly apiKey = signal(sessionStorage.getItem(ApiKeyKey) ?? '');

  readonly signInRequired = computed(() => this.me()?.signInRequired ?? false);

  readonly name = computed(() => (this.signInRequired() ? (this.me()?.name ?? '') : this.typed()));

  set(name: string): void {
    const trimmed = name.trim();
    this.typed.set(trimmed);
    if (trimmed) {
      localStorage.setItem(Key, trimmed);
    } else {
      localStorage.removeItem(Key);
    }
  }

  setApiKey(key: string): void {
    const trimmed = key.trim();
    this.apiKey.set(trimmed);
    if (trimmed) {
      sessionStorage.setItem(ApiKeyKey, trimmed);
    } else {
      sessionStorage.removeItem(ApiKeyKey);
    }
  }
}
