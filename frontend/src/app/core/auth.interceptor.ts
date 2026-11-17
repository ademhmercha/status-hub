import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { ConfigService } from './config';

/** Adds the bearer token to API calls (never to Cognito) and signs out on 401. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const base = inject(ConfigService).config.apiBaseUrl;
  const isApi = req.url.startsWith(base);
  const isPublic = req.url.startsWith(`${base}/api/v1/status/`) || req.url.startsWith(`${base}/dev/token`);
  const token = auth.token();
  const authed = isApi && !isPublic && token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  return next(authed).pipe(
    catchError((e: unknown) => {
      if (e instanceof HttpErrorResponse && e.status === 401 && isApi && !isPublic) auth.logout();
      return throwError(() => e);
    }),
  );
};
