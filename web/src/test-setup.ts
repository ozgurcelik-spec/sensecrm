// Registers jest-dom's DOM matchers (toBeInTheDocument, ...) for every vitest run, and the browser
// APIs jsdom lacks but Mantine needs (matchMedia, ResizeObserver).
import "@testing-library/jest-dom/vitest";
import { configure } from "@testing-library/react";

// findBy* / waitFor wait up to 1 s by default; lazy route chunks and Mantine forms take longer than
// that when the whole suite (or other agents' builds) loads the machine. Passing waits return at once.
configure({ asyncUtilTimeout: 5000 });

if (!window.matchMedia) {
  Object.defineProperty(window, "matchMedia", {
    writable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }),
  });
}

// Mantine's combobox scrolls the active option into view.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => {};
}

// Mantine's autosizing Textarea listens to font loading.
if (!("fonts" in document)) {
  Object.defineProperty(document, "fonts", {
    value: { addEventListener() {}, removeEventListener() {}, ready: Promise.resolve() },
  });
}

if (!window.ResizeObserver) {
  window.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}
