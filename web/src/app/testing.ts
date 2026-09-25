import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentProviders, Provider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { errorInterceptor, userInterceptor } from './core/http';

export function apiProviders(): (Provider | EnvironmentProviders)[] {
  return [provideRouter([]), provideHttpClient(withInterceptors([userInterceptor, errorInterceptor])), provideHttpClientTesting()];
}

export function http(): HttpTestingController {
  return TestBed.inject(HttpTestingController);
}

/** Answers every open request whose url and method match. */
export function respond(url: string, body: string | number | boolean | object | null, method = 'GET'): void {
  for (const request of http().match((r) => r.urlWithParams === url && r.method === method)) {
    request.flush(body);
  }
}

export async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

export function text(root: HTMLElement, testId: string): string {
  return root.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim() ?? '';
}
