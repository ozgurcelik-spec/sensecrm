import { useTranslation } from "react-i18next";
import { toLocale } from "@/lib/locale";
import { toastApiError } from "@/hooks/use-toast";
import { updateMe } from "@/services/me.service";
import { useAuthStore } from "@/store/auth.store";
import type { Locale } from "@/types";

/**
 * Switches the UI language. When signed in the choice is saved as the user's locale (PATCH /me),
 * so it follows the user across devices; on the login/sign-up screens it is only cached locally.
 */
export function useChangeLanguage() {
  const { i18n } = useTranslation();
  const signedIn = useAuthStore((state) => !!state.me);
  const refreshMe = useAuthStore((state) => state.refreshMe);

  const current = toLocale(i18n.resolvedLanguage ?? i18n.language);

  async function change(locale: Locale) {
    if (locale === current) return;
    await i18n.changeLanguage(locale);
    if (!signedIn) return;
    try {
      await updateMe({ locale });
      await refreshMe();
    } catch (error) {
      toastApiError(error);
    }
  }

  return { current, change };
}
