import { defineConfig } from "@playwright/test";
import { PORT } from "./support/processes";

// Real end to end: browser -> web -> real skail environment <- worker. One order at a time (the page locks on
// an active order and there is a single worker), so everything runs serially.
export default defineConfig({
  testDir: "./tests",
  globalSetup: "./support/global-setup.ts",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 150_000,
  expect: { timeout: 60_000 },
  reporter: [["list"]],
  use: { baseURL: `http://localhost:${PORT}`, trace: "retain-on-failure", screenshot: "only-on-failure" },
  outputDir: "./test-results",
});
