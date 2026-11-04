import { expect, test, type Locator, type Page } from "@playwright/test";
import { DEADLINE_SECONDS, startWeb, startWorker, type Managed } from "../support/processes";

// Real end to end, through the browser: page -> web -> skail environment <- worker (a real process).
// The deadlines are DEADLINE_SECONDS for each level.

let web: Managed;
let worker: Managed;

test.beforeAll(async () => {
  web = await startWeb();
  worker = await startWorker();
});

test.afterAll(async () => {
  await worker?.stop();
  await web?.stop();
});

// the level cards carry data-level, so the tests find them without depending on the layout
const card = (page: Page, level: "Manager" | "Director"): Locator => page.locator(`.area[data-level="${level === "Manager" ? "manager" : "director"}"]`);
const badge = (page: Page) => page.locator("#state");
const result = (page: Page) => page.locator("#result");
const timeline = (page: Page) => page.locator("#timeline li");

/**
 * Time for the function to finish its activation and hibernate on its wait after the manager card turns Pending.
 * A worker killed *during* an activation leaves the execution leased until the skail server's visibility timeout
 * (minutes), which is not what these scenarios are about: they crash the worker while the order is waiting.
 */
const SETTLE_MS = 3000;

async function request(page: Page, supplier = "ACME E2E") {
  await page.goto("/");
  await expect(page.getByText(`Deadlines: Manager ${DEADLINE_SECONDS}s · Director ${DEADLINE_SECONDS}s`)).toBeVisible();
  await page.getByLabel("Supplier").fill(supplier);
  await page.getByRole("button", { name: "Request approval" }).click();
  await expect(badge(page)).toHaveText("Waiting for Manager");
  // the worker ran RequestApproval: the manager card is Pending and can decide
  await expect(card(page, "Manager").locator(".pill")).toHaveText("Pending");
  await expect(card(page, "Manager").getByRole("button", { name: "Approve" })).toBeEnabled();
}

test("layout: idle page, then the order locks the form", async ({ page }) => {
  await page.goto("/");
  await expect(badge(page)).toHaveText("Idle");
  await expect(page.getByLabel("Supplier")).toHaveValue("Papelaria Central");
  await expect(page.getByLabel("Amount")).toHaveValue("18500");
  await expect(card(page, "Manager").locator(".pill")).toHaveText("Not requested");
  await expect(card(page, "Director").locator(".pill")).toHaveText("Not requested");
  await expect(card(page, "Manager").getByRole("button", { name: "Approve" })).toBeDisabled();

  const widths = await page.locator(".areas article").evaluateAll((els) => els.map((e) => Math.round(e.getBoundingClientRect().width)));
  expect(widths).toHaveLength(2);
  expect(Math.abs(widths[0]! - widths[1]!)).toBeLessThanOrEqual(1); // two equal columns (grid 1fr 1fr)
  await page.screenshot({ path: "test-results/idle.png", fullPage: true });
});

test("manager approves within the deadline: approved at once, the deadline is not waited for", async ({ page }) => {
  await request(page);
  await expect(page.getByLabel("Supplier")).toBeDisabled();
  await expect(card(page, "Manager").locator(".countdown")).toContainText("Deadline in");

  const started = Date.now();
  await card(page, "Manager").getByPlaceholder("Decided by").fill("Maria");
  await card(page, "Manager").getByRole("button", { name: "Approve" }).click();

  await expect(badge(page)).toHaveText("Approved");
  expect(Date.now() - started).toBeLessThan(DEADLINE_SECONDS * 1000);
  await expect(result(page)).toHaveText(/^Approved by the Manager · reference PO-[0-9A-F]{8}$/);
  await expect(card(page, "Manager").locator(".pill")).toHaveText("Approved");
  await expect(card(page, "Manager").locator(".detail")).toContainText("Maria");
  await expect(timeline(page).last()).toContainText("Purchase approved (PO-");
  await expect(page.getByLabel("Supplier")).toBeEnabled();
  await page.screenshot({ path: "test-results/approved.png", fullPage: true });
});

test("manager rejects with a reason: rejected, not cancelled", async ({ page }) => {
  await request(page);
  await card(page, "Manager").getByPlaceholder("Decided by").fill("Maria");
  await card(page, "Manager").getByPlaceholder("Reason (if rejecting)").fill("fora do orçamento");
  await card(page, "Manager").getByRole("button", { name: "Reject" }).click();

  await expect(badge(page)).toHaveText("Rejected");
  await expect(result(page)).toHaveText("Rejected by the Manager (fora do orçamento)");
  await expect(card(page, "Manager").locator(".pill")).toHaveText("Rejected");
  await expect(card(page, "Manager").locator(".detail")).toContainText("“fora do orçamento”");
  await expect(card(page, "Director").locator(".pill")).toHaveText("Not requested");
});

test("no answer from the manager escalates to the director, who approves; the manager can no longer decide", async ({ page }) => {
  await request(page);
  await expect(badge(page)).toHaveText("Waiting for Director"); // after the deadline
  await expect(card(page, "Manager").locator(".pill")).toHaveText("No response");
  await expect(card(page, "Director").locator(".pill")).toHaveText("Pending");
  await expect(card(page, "Manager").getByRole("button", { name: "Approve" })).toBeDisabled();
  await expect(card(page, "Director").getByRole("button", { name: "Approve" })).toBeEnabled();

  // the manager's decision after the escalation is refused by the web app, and nothing is fired
  const id = await page.locator("#orderId").innerText();
  const late = await page.request.post(`/api/orders/${id}/decision`, { data: { level: "manager", approved: true } });
  expect(late.status()).toBe(409);

  await card(page, "Director").getByPlaceholder("Decided by").fill("Diana");
  await card(page, "Director").getByRole("button", { name: "Approve" }).click();
  await expect(badge(page)).toHaveText("Approved");
  await expect(result(page)).toHaveText(/^Approved by the Director · reference PO-/);
  await expect(card(page, "Manager").locator(".pill")).toHaveText("No response");

  // a second decision after the end is refused too
  const again = await page.request.post(`/api/orders/${id}/decision`, { data: { level: "director", approved: false } });
  expect(again.status()).toBe(409);
});

test("the director rejects after the escalation", async ({ page }) => {
  await request(page);
  await expect(badge(page)).toHaveText("Waiting for Director");
  await card(page, "Director").getByPlaceholder("Reason (if rejecting)").fill("no budget");
  await card(page, "Director").getByRole("button", { name: "Reject" }).click();
  await expect(badge(page)).toHaveText("Rejected");
  await expect(result(page)).toHaveText("Rejected by the Director (no budget)");
});

test("no answer from anyone: cancelled for no response (not a rejection)", async ({ page }) => {
  await request(page);
  await expect(badge(page)).toHaveText("Waiting for Director");
  await expect(badge(page)).toHaveText("Cancelled");
  await expect(result(page)).toHaveText("Cancelled · no response from the Manager or the Director");
  await expect(card(page, "Manager").locator(".pill")).toHaveText("No response");
  await expect(card(page, "Director").locator(".pill")).toHaveText("No response");
  await expect(timeline(page).last()).toContainText("Order cancelled: no response");
  await expect(page.getByLabel("Supplier")).toBeEnabled();
  await page.screenshot({ path: "test-results/cancelled.png", fullPage: true });
});

test("reloading the page mid-order restores the cards, the countdown and the controls", async ({ page }) => {
  await request(page);
  await page.reload();
  await expect(badge(page)).toHaveText("Waiting for Manager");
  await expect(card(page, "Manager").locator(".pill")).toHaveText("Pending");
  await expect(card(page, "Manager").locator(".countdown")).toContainText("Deadline");
  await expect(card(page, "Manager").getByRole("button", { name: "Approve" })).toBeEnabled();
  await expect(page.getByLabel("Supplier")).toBeDisabled();
  await expect(page.locator("#orderId")).not.toHaveText("-");

  await card(page, "Manager").getByRole("button", { name: "Approve" }).click(); // leave no order running
  await expect(badge(page)).toHaveText("Approved");
});

test("durability: the worker is killed while the order waits; the deadline passes with it down; on return the escalation happens and the manager is not asked again", async ({ page }) => {
  await request(page);
  await page.waitForTimeout(SETTLE_MS);
  await worker.stop(); // kill -9 while the order waits

  // the deadline of skail keeps counting; nobody is running, so the page cannot move on
  await page.waitForTimeout((DEADLINE_SECONDS + 3) * 1000);
  await expect(badge(page)).toHaveText("Waiting for Manager");

  worker = await startWorker();
  await expect(badge(page)).toHaveText("Waiting for Director");
  await expect(card(page, "Manager").locator(".pill")).toHaveText("No response");

  await card(page, "Director").getByRole("button", { name: "Approve" }).click();
  await expect(badge(page)).toHaveText("Approved");
  // RequestApproval for the manager was recorded once, not run again after the restart
  const requested = await timeline(page).allInnerTexts();
  expect(requested.filter((t) => t.includes("Approval requested from the Manager"))).toHaveLength(1);
  expect(requested.filter((t) => t.includes("escalated to the Director"))).toHaveLength(1);
});

test("durability: a decision made while the worker is down is picked up when it comes back", async ({ page }) => {
  await request(page);
  await page.waitForTimeout(SETTLE_MS);
  await worker.stop();

  await card(page, "Manager").getByPlaceholder("Decided by").fill("Maria");
  await card(page, "Manager").getByRole("button", { name: "Approve" }).click();
  await expect(card(page, "Manager").locator(".pill")).toHaveText("Approved"); // registered by the web app, fired to skail
  await expect(badge(page)).toHaveText("Waiting for Manager"); // the function has not consumed it yet

  worker = await startWorker();
  await expect(badge(page)).toHaveText("Approved");
  await expect(result(page)).toHaveText(/^Approved by the Manager · reference PO-/);
});
