import { languages, setLanguage, t, useLanguage, type Language } from './index';

/** Scelta della lingua della dashboard: vale subito e resta salvata nel browser. */
export function LanguageSelect() {
  const language = useLanguage();
  return (
    <select
      className="language"
      value={language}
      onChange={(e) => setLanguage(e.target.value as Language)}
      aria-label={t('Lingua')}
    >
      {languages.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}
    </select>
  );
}
