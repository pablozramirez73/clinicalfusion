import { languages, setLanguage, useLanguage, type Language } from './index';

/**
 * Scelta della lingua della dashboard: vale subito e resta salvata nel browser.
 * L'etichetta è scritta nelle due lingue e non si traduce: chi non capisce la lingua corrente deve
 * comunque riconoscere il comando.
 */
export function LanguageSelect() {
  const language = useLanguage();
  return (
    <label className="language">
      <span>🌐 Lingua / Language</span>
      <select value={language} onChange={(e) => setLanguage(e.target.value as Language)}>
        {languages.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}
      </select>
    </label>
  );
}
