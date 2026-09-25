import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Api } from './core/api';
import { UserService } from './core/user';

@Component({
  selector: 'app-root',
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSidenavModule,
    MatToolbarModule,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly user = inject(UserService);
  private readonly api = inject(Api);

  constructor() {
    this.refresh();
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

  protected readonly links = [
    { path: '/', label: 'Overview', exact: true },
    { path: '/playbooks', label: 'Playbooks', exact: false },
    { path: '/profiles', label: 'Profiles', exact: false },
    { path: '/mappings', label: 'Mappings', exact: false },
    { path: '/suggestions', label: 'AI suggestions', exact: false },
  ];
}
