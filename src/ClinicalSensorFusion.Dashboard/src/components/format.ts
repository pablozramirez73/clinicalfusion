import { locale } from '../i18n';

// I formati seguono la lingua scelta: si creano a ogni uso, la lingua può cambiare senza ricaricare la pagina.
const dateTime = () => new Intl.DateTimeFormat(locale(), { dateStyle: 'medium', timeStyle: 'short' });
const date = () => new Intl.DateTimeFormat(locale(), { dateStyle: 'medium' });
const shortDate = () => new Intl.DateTimeFormat(locale(), { day: '2-digit', month: 'short' });

export const fmtDateTime = (iso: string | null | undefined) => (iso ? dateTime().format(new Date(iso)) : '—');
export const fmtDate = (iso: string | null | undefined) => (iso ? date().format(new Date(iso)) : '—');
export const fmtShortDate = (iso: string) => shortDate().format(new Date(iso));

export function fmtDuration(seconds: number | null | undefined) {
  if (seconds == null) return '—';
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return `${m}:${s.toString().padStart(2, '0')}`;
}

export function age(birthDate: string | null) {
  if (!birthDate) return null;
  const b = new Date(birthDate);
  const now = new Date();
  let years = now.getFullYear() - b.getFullYear();
  if (now.getMonth() < b.getMonth() || (now.getMonth() === b.getMonth() && now.getDate() < b.getDate())) years--;
  return years;
}

export function fullName(p: { firstName: string | null; lastName: string | null }) {
  const name = [p.lastName, p.firstName].filter(Boolean).join(' ');
  return name || null;
}
