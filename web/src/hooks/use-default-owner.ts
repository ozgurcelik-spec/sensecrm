import { useAuthStore } from "@/store/auth.store";

/** New records default to the signed-in user as owner (the server does the same when omitted). */
export function useDefaultOwnerId(): string {
  return useAuthStore((state) => state.me?.user.id ?? "");
}
