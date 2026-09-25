import { changeCount, sideBySide } from './diff';

describe('sideBySide', () => {
  it('pairs changed lines and marks additions and removals', () => {
    const rows = sideBySide(['a', 'b', 'c', 'd'].join('\n'), ['a', 'B', 'c', 'e', 'f'].join('\n'), 5);

    expect(rows.map((r) => [r.kind, r.left?.text ?? '', r.right?.text ?? ''])).toEqual([
      ['same', 'a', 'a'],
      ['changed', 'b', 'B'],
      ['same', 'c', 'c'],
      ['changed', 'd', 'e'],
      ['added', '', 'f'],
    ]);
    expect(rows[4].right?.number).toBe(5);
    expect(changeCount(rows)).toBe(3);
  });

  it('folds long unchanged runs but keeps context around changes', () => {
    const before = Array.from({ length: 30 }, (_, k) => `line ${k}`);
    const after = [...before];
    after[15] = 'changed';

    const rows = sideBySide(before.join('\n'), after.join('\n'), 2);

    expect(rows.map((r) => r.kind)).toEqual(['skipped', 'same', 'same', 'changed', 'same', 'same', 'skipped']);
    expect(rows[0].skipped).toBe(13);
    expect(rows[6].skipped).toBe(12);
    expect(changeCount(sideBySide('x', 'x'))).toBe(0);
  });
});
