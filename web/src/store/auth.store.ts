import { create } from "zustand";
import { persist } from "zustand/middleware";
import {
  AUTH_TOKEN_STORAGE_KEY,
  REFRESH_TOKEN_STORAGE_KEY,
  readStorage,
  writeStorage,
} from "@/lib/api-client";
import { queryClient } from "@/lib/query-client";
import * as authService from "@/services/auth.service";
import type { SignupRequest } from "@/services/auth.service";
import { changePassword as changePasswordRequest, getMe } from "@/services/me.service";
import type { AuthTokens, Me } from "@/types";

interface AuthStore {
  /** Access token (single live copy also kept under AUTH_TOKEN_STORAGE_KEY for the api-client). */
  token: string | null;
  refreshToken: string | null;
  /** `GET /api/me` - user, active organization, role, permissions, organization list. */
  me: Me | null;
  /** False until zustand `persist` has restored `me`; ProtectedRoute waits on it to avoid a permission flash. */
  hasHydrated: boolean;
  /** The API only accepts the password-change flow until this is cleared (C-SEC). Persisted with `me`. */
  mustChangePassword: boolean;
  setMustChangePassword: (value: boolean) => void;
  /** `POST /me/password`: stores the new tokens, clears the forced-change flag and reloads /me. */
  changePassword: (currentPassword: string, newPassword: string) => Promise<void>;
  login: (email: string, password: string) => Promise<void>;
  signup: (request: SignupRequest) => Promise<void>;
  logout: () => Promise<void>;
  /** Rotates the token pair; wired into the api-client refresh handler. Never throws. */
  refresh: () => Promise<boolean>;
  /** Re-reads /api/me (permissions, organizations) - call after changing anything that affects the caller. */
  refreshMe: () => Promise<void>;
  switchOrganization: (organizationId: string) => Promise<void>;
  isAuthenticated: () => boolean;
  hasPermission: (permission: string) => boolean;
}

function storeTokens(tokens: AuthTokens): void {
  writeStorage(AUTH_TOKEN_STORAGE_KEY, tokens.accessToken);
  writeStorage(REFRESH_TOKEN_STORAGE_KEY, tokens.refreshToken);
}

function clearTokens(): void {
  writeStorage(AUTH_TOKEN_STORAGE_KEY, null);
  writeStorage(REFRESH_TOKEN_STORAGE_KEY, null);
}

// `onRehydrateStorage` can run synchronously inside `create()` (localStorage is sync), before
// `useAuthStore` is assigned - capture the creator's `set` instead of closing over the store.
let setAuthState: ((partial: Partial<AuthStore>) => void) | null = null;

export const useAuthStore = create<AuthStore>()(
  persist(
    (set, get) => {
      setAuthState = set;

      /** Stores the tokens, loads /me and populates the store (login, signup, org switch). */
      async function establishSession(tokens: AuthTokens): Promise<void> {
        storeTokens(tokens);
        set({ token: tokens.accessToken, refreshToken: tokens.refreshToken });
        try {
          const me = await getMe();
          set({
            me,
            mustChangePassword:
              me.mustChangePassword === true || tokens.mustChangePassword === true,
          });
        } catch (error) {
          clearTokens();
          set({ token: null, refreshToken: null, me: null, mustChangePassword: false });
          throw error;
        }
      }

      return {
        token: readStorage(AUTH_TOKEN_STORAGE_KEY),
        refreshToken: readStorage(REFRESH_TOKEN_STORAGE_KEY),
        me: null,
        hasHydrated: false,
        mustChangePassword: false,

        setMustChangePassword: (value) => set({ mustChangePassword: value }),

        changePassword: async (currentPassword, newPassword) => {
          const tokens = await changePasswordRequest({ currentPassword, newPassword });
          storeTokens(tokens);
          set({
            token: tokens.accessToken,
            refreshToken: tokens.refreshToken,
            mustChangePassword: false,
          });
          await get().refreshMe();
        },

        login: async (email, password) => {
          await establishSession(await authService.login(email, password));
        },

        signup: async (request) => {
          await establishSession(await authService.signup(request));
        },

        logout: async () => {
          const refreshToken = get().refreshToken;
          clearTokens();
          set({ token: null, refreshToken: null, me: null, mustChangePassword: false });
          queryClient.clear();
          if (refreshToken) {
            // Best effort: the server revokes the refresh token; the local session is already gone.
            await authService.logout(refreshToken).catch(() => undefined);
          }
        },

        refresh: async () => {
          const refreshToken = get().refreshToken ?? readStorage(REFRESH_TOKEN_STORAGE_KEY);
          if (!refreshToken) return false;
          try {
            const tokens = await authService.refreshTokens(refreshToken);
            storeTokens(tokens);
            set({ token: tokens.accessToken, refreshToken: tokens.refreshToken });
            if (typeof tokens.mustChangePassword === "boolean") {
              set({ mustChangePassword: tokens.mustChangePassword });
            }
            return true;
          } catch {
            return false;
          }
        },

        refreshMe: async () => {
          if (!get().token && !get().refreshToken) return;
          try {
            const me = await getMe();
            set({
              me,
              mustChangePassword:
                typeof me.mustChangePassword === "boolean"
                  ? me.mustChangePassword
                  : get().mustChangePassword,
            });
          } catch {
            // A 401 is handled by the api-client; other failures keep the cached profile.
          }
        },

        switchOrganization: async (organizationId) => {
          await establishSession(await authService.switchOrganization(organizationId));
          // Every cached query belongs to the previous organization.
          queryClient.removeQueries();
        },

        isAuthenticated: () => {
          const { token, refreshToken, me } = get();
          // An expired access token is fine while a refresh token exists: the api-client refreshes it.
          return !!(token || refreshToken) && !!me;
        },

        hasPermission: (permission) => get().me?.permissions.includes(permission) ?? false,
      };
    },
    {
      name: "auth-store",
      version: 1,
      // Tokens live under their own storage keys (read by the api-client); only the profile is persisted here.
      partialize: (state) => ({ me: state.me, mustChangePassword: state.mustChangePassword }),
      onRehydrateStorage: () => () => {
        setAuthState?.({ hasHydrated: true });
      },
    }
  )
);
