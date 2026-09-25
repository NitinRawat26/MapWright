import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { RouterLink } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { Api } from '../core/api';

@Component({
  selector: 'app-home',
  imports: [MatCardModule, RouterLink],
  templateUrl: './home.html',
  styleUrl: './home.scss',
})
export class Home {
  private readonly api = inject(Api);

  protected readonly healthy = toSignal(this.api.health().pipe(map((h) => h.status === 'ok'), catchError(() => of(false))));
  protected readonly ai = toSignal(this.api.ai().pipe(catchError(() => of(null))));
  protected readonly playbooks = toSignal(this.api.playbooks().pipe(catchError(() => of([]))));
  protected readonly profiles = toSignal(this.api.profiles().pipe(catchError(() => of([]))));
  protected readonly mappings = toSignal(this.api.mappings().pipe(catchError(() => of([]))));
  protected readonly pending = toSignal(this.api.pendingSuggestions().pipe(catchError(() => of([]))));

  protected count(status: string): number {
    return (this.playbooks() ?? []).filter((p) => p.status === status).length;
  }
}
