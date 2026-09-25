import { Injectable, signal } from '@angular/core';

const Key = 'mapwright.user';

/** The reviewer's name, sent as X-MapWright-User and recorded in the audit trail. */
@Injectable({ providedIn: 'root' })
export class UserService {
  readonly name = signal(localStorage.getItem(Key) ?? '');

  set(name: string): void {
    const trimmed = name.trim();
    this.name.set(trimmed);
    if (trimmed) {
      localStorage.setItem(Key, trimmed);
    } else {
      localStorage.removeItem(Key);
    }
  }
}
