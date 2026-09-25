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
  { path: 'profiles', loadComponent: () => import('./profiles/profile-list').then((m) => m.ProfileList), title: 'Profiles · MapWright' },
  { path: 'profiles/:id', loadComponent: () => import('./profiles/profile-detail').then((m) => m.ProfileDetail), title: 'Profile · MapWright' },
  { path: 'mappings', loadComponent: () => import('./mappings/mapping-list').then((m) => m.MappingList), title: 'Mappings · MapWright' },
  { path: 'mappings/:id', loadComponent: () => import('./mappings/mapping-detail').then((m) => m.MappingDetail), title: 'Mapping · MapWright' },
  { path: '**', redirectTo: '' },
];
