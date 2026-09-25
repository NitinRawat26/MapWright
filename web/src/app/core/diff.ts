export type DiffKind = 'same' | 'changed' | 'added' | 'removed' | 'skipped';

export interface DiffLine {
  number: number;
  text: string;
}

export interface DiffRow {
  kind: DiffKind;
  left?: DiffLine;
  right?: DiffLine;
  /** For 'skipped' rows: how many unchanged lines were folded away. */
  skipped?: number;
}

/**
 * Side-by-side line diff (longest common subsequence). Adjacent removed and added lines are paired as changed,
 * and unchanged runs longer than 2 × context are folded into one 'skipped' row.
 */
export function sideBySide(before: string, after: string, context = 3): DiffRow[] {
  const a = before.split('\n');
  const b = after.split('\n');
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) {
    start++;
  }

  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }

  const n = endA - start;
  const m = endB - start;
  const lengths = new Uint32Array((n + 1) * (m + 1));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lengths[i * (m + 1) + j] =
        a[start + i] === b[start + j]
          ? lengths[(i + 1) * (m + 1) + j + 1] + 1
          : Math.max(lengths[(i + 1) * (m + 1) + j], lengths[i * (m + 1) + j + 1]);
    }
  }

  const rows: DiffRow[] = [];
  const same = (i: number, j: number) => rows.push({ kind: 'same', left: { number: i + 1, text: a[i] }, right: { number: j + 1, text: b[j] } });
  for (let k = 0; k < start; k++) {
    same(k, k);
  }

  let removed: DiffLine[] = [];
  let added: DiffLine[] = [];
  const flush = () => {
    for (let k = 0; k < Math.max(removed.length, added.length); k++) {
      const left = removed[k];
      const right = added[k];
      rows.push({ kind: left && right ? 'changed' : left ? 'removed' : 'added', left, right });
    }
    removed = [];
    added = [];
  };

  let i = 0;
  let j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && a[start + i] === b[start + j]) {
      flush();
      same(start + i, start + j);
      i++;
      j++;
    } else if (j >= m || (i < n && lengths[(i + 1) * (m + 1) + j] >= lengths[i * (m + 1) + j + 1])) {
      removed.push({ number: start + i + 1, text: a[start + i] });
      i++;
    } else {
      added.push({ number: start + j + 1, text: b[start + j] });
      j++;
    }
  }

  flush();
  for (let k = 0; k < a.length - endA; k++) {
    same(endA + k, endB + k);
  }

  return fold(rows, context);
}

function fold(rows: DiffRow[], context: number): DiffRow[] {
  const result: DiffRow[] = [];
  let k = 0;
  while (k < rows.length) {
    if (rows[k].kind !== 'same') {
      result.push(rows[k++]);
      continue;
    }

    let end = k;
    while (end < rows.length && rows[end].kind === 'same') {
      end++;
    }

    const keepBefore = k === 0 ? 0 : context;
    const keepAfter = end === rows.length ? 0 : context;
    if (end - k > keepBefore + keepAfter + 1) {
      result.push(...rows.slice(k, k + keepBefore));
      result.push({ kind: 'skipped', skipped: end - k - keepBefore - keepAfter });
      result.push(...rows.slice(end - keepAfter, end));
    } else {
      result.push(...rows.slice(k, end));
    }

    k = end;
  }

  return result;
}

export function changeCount(rows: DiffRow[]): number {
  return rows.filter((r) => r.kind !== 'same' && r.kind !== 'skipped').length;
}
