/** "just now", "5m ago", "3h ago", "2d ago", or the date for anything older than a week. */
export function ago(iso: string, now = Date.now()): string {
  const minutes = Math.floor((now - Date.parse(iso)) / 60000);
  if (minutes < 1) {
    return 'just now';
  }

  if (minutes < 60) {
    return `${minutes}m ago`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }

  const days = Math.floor(hours / 24);
  return days < 7 ? `${days}d ago` : new Date(iso).toLocaleDateString();
}

/** Up to two initials of a name, e.g. "Nitin Rawat" → "NR". */
export function initials(name: string): string {
  return name
    .split(/[\s._@-]+/)
    .filter((part) => part)
    .slice(0, 2)
    .map((part) => part[0].toUpperCase())
    .join('');
}
