import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["playwright-report", "test-results", "artifacts", ".scratch", "node_modules"] },
  {
    files: ["**/*.ts"],
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    languageOptions: { ecmaVersion: 2023, globals: { ...globals.node, ...globals.browser } },
    rules: {
      // Playwright fixtures must destructure "nothing" as `{}` and unused catch bindings are fine.
      "no-empty-pattern": "off",
      // Flaky by construction: no fixed sleeps, no focused/skipped-without-reason tests.
      "no-restricted-properties": [
        "error",
        { object: "page", property: "waitForTimeout", message: "Use web-first assertions instead of fixed sleeps." },
      ],
    },
  },
);
