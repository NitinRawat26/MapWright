import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, throwError } from 'rxjs';
import { ApiProblem } from './models';
import { UserService } from './user';

export const UserHeader = 'X-MapWright-User';

export const userInterceptor: HttpInterceptorFn = (request, next) => {
  const user = inject(UserService).name();
  return next(user && !request.headers.has(UserHeader) ? request.clone({ setHeaders: { [UserHeader]: user } }) : request);
};

/** Turns an error response into one readable message, using the API's problem body when there is one. */
export function describeError(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return String(error);
  }

  const problem = readProblem(error.error);
  if (problem && typeof problem === 'object' && typeof problem.detail === 'string') {
    const issues = (problem.issues ?? []).map((i) => `${i.severity} ${i.code} [${i.location}] ${i.message}`);
    return [problem.detail, ...issues].join('\n');
  }

  return error.status === 0 ? 'The MapWright API is not reachable.' : `${error.status} ${error.statusText}`;
}

function readProblem(body: unknown): Partial<ApiProblem> | null {
  if (typeof body !== 'string') {
    return body as Partial<ApiProblem> | null;
  }

  try {
    return JSON.parse(body) as Partial<ApiProblem>;
  } catch {
    return null;
  }
}

export const errorInterceptor: HttpInterceptorFn = (request, next) => {
  const snackBar = inject(MatSnackBar);
  return next(request).pipe(
    catchError((error: unknown) => {
      snackBar.open(describeError(error), 'Close', { panelClass: 'problem', duration: 12000 });
      return throwError(() => error);
    }),
  );
};
