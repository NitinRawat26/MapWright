import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { catchError, filter, map, of } from 'rxjs';
import { Api } from './core/api';
import { Icon } from './core/icon';
import { IconName } from './core/icons';
import { Search } from './core/search';
import { initials } from './core/time';
import { UserService } from './core/user';

const methods: Record<string, string> = { apiKey: 'API key', bearer: 'Single sign-on token', proxy: 'SSO proxy', header: 'Name header' };

@Component({
  selector: 'app-root',
  imports: [
    Icon,
    MatButtonModule,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    Search,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly user = inject(UserService);
  private readonly api = inject(Api);
  private readonly router = inject(Router);

  protected readonly initials = initials;
  protected readonly ai = toSignal(this.api.ai().pipe(catchError(() => of(null))));
  protected readonly pending = signal(0);
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: '/' },
  );

  /** The nav label of the current page, for the header. */
  protected readonly section = computed(() => {
    const first = this.url().split(/[/?#]/)[1] ?? '';
    return [...this.links, ...this.configure.filter((l) => !l.fragment)].find((l) => l.path === `/${first}`)?.label ?? 'Overview';
  });

  protected readonly method = computed(() => methods[this.user.me()?.method ?? ''] ?? '');

  constructor() {
    this.refresh();
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => this.countPending());
  }

  /** Re-counted after every navigation, so the badge follows approvals and new AI passes. */
  private countPending(): void {
    this.api
      .pendingSuggestions()
      .pipe(catchError(() => of([])))
      .subscribe((list) => this.pending.set(list.length));
  }

  protected signIn(key: string): void {
    this.user.setApiKey(key);
    this.refresh();
  }

  protected signOut(): void {
    this.user.setApiKey('');
    this.refresh();
  }

  private refresh(): void {
    this.api.me().subscribe({
      next: (me) => this.user.me.set(me),
      error: () => {
        this.user.setApiKey('');
        this.user.me.set({ signInRequired: true });
      },
    });
  }

  protected readonly links: { key: string; path: string; label: string; icon: IconName; exact: boolean }[] = [
    { key: 'overview', path: '/', label: 'Overview', icon: 'space_dashboard', exact: true },
    { key: 'profiles', path: '/profiles', label: 'Profiles', icon: 'database', exact: false },
    { key: 'mappings', path: '/mappings', label: 'Mappings', icon: 'account_tree', exact: false },
    { key: 'playbooks', path: '/playbooks', label: 'Playbooks', icon: 'menu_book', exact: false },
    { key: 'suggestions', path: '/suggestions', label: 'AI suggestions', icon: 'star_shine', exact: false },
  ];

  protected readonly configure: { key: string; path: string; label: string; icon: IconName; exact: boolean; fragment?: string }[] = [
    { key: 'api-keys', path: '/settings', label: 'API keys', icon: 'key', exact: false, fragment: 'api-keys' },
    { key: 'settings', path: '/settings', label: 'Settings', icon: 'settings', exact: false },
  ];
}
