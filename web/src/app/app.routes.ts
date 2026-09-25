import { Routes } from '@angular/router';
import { Home } from './home/home';

export const routes: Routes = [
  { path: '', component: Home, title: 'MapWright' },
  { path: 'playbooks', loadComponent: () => import('./playbooks/playbook-list').then((m) => m.PlaybookList), title: 'Playbooks · MapWright' },
  {
    path: 'playbooks/:kind/:slug/:version',
    loadComponent: () => import('./playbooks/playbook-detail').then((m) => m.PlaybookDetail),
    title: 'Playbook · MapWright',
  },
  { path: '**', redirectTo: '' },
];
