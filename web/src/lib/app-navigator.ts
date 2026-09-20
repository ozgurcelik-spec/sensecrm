/**
 * Lets code outside the router tree (toasts are rendered above `<Router>`) navigate in-app.
 * The app shell registers `useNavigate()`'s function; without it we fall back to a page load.
 */
type Navigate = (to: string) => void;

let navigateFn: Navigate | null = null;

export function setAppNavigator(fn: Navigate | null): void {
  navigateFn = fn;
}

export function navigateApp(to: string): void {
  if (navigateFn) navigateFn(to);
  else window.location.assign(to);
}
