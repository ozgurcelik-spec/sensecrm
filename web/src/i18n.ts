import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import HttpBackend from "i18next-http-backend";
import { DEFAULT_LOCALE, SUPPORTED_LOCALES, toLocale } from "@/lib/locale";

export const NAMESPACES = [
  "common",
  "auth",
  "navigation",
  "home",
  "settings",
  "users",
  "audit",
  "account",
  "crm",
  "activities",
  "reports",
  "workflows",
  "campaigns",
  "security",
] as const;

i18n
  .use(HttpBackend)
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    fallbackLng: DEFAULT_LOCALE,
    supportedLngs: [...SUPPORTED_LOCALES],
    nonExplicitSupportedLngs: true,
    load: "languageOnly",
    defaultNS: "common",
    ns: [...NAMESPACES],
    interpolation: {
      escapeValue: false,
    },
    backend: {
      loadPath: "/locales/{{lng}}/{{ns}}.json",
    },
    // Turkish is the product default: only an explicit earlier choice (?lng= or the language menu,
    // cached in localStorage) overrides it. After login the user's own locale wins (see App.tsx).
    detection: {
      order: ["querystring", "localStorage"],
      caches: ["localStorage"],
    },
  });

i18n.on("languageChanged", (lng) => {
  document.documentElement.lang = toLocale(lng);
});

export default i18n;
