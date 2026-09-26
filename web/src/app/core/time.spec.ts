import { ago, initials } from './time';

describe('time', () => {
  it('says how long ago something happened', () => {
    const now = Date.parse('2026-09-24T12:00:00Z');
    expect(ago('2026-09-24T11:59:40Z', now)).toBe('just now');
    expect(ago('2026-09-24T11:55:00Z', now)).toBe('5m ago');
    expect(ago('2026-09-24T09:00:00Z', now)).toBe('3h ago');
    expect(ago('2026-09-22T12:00:00Z', now)).toBe('2d ago');
    expect(ago('2026-08-01T12:00:00Z', now)).toBe(new Date('2026-08-01T12:00:00Z').toLocaleDateString());
  });

  it('takes up to two initials', () => {
    expect(initials('Nitin Rawat')).toBe('NR');
    expect(initials('ci-bot')).toBe('CB');
    expect(initials('ana')).toBe('A');
    expect(initials('')).toBe('');
  });
});
