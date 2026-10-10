import { createHash, randomBytes } from "node:crypto";
import { createServer, type Server } from "node:http";
import { ADMIN, PASSWORD, expect, test } from "./support";

// Two sign-in pages whose success is a redirect to ANOTHER origin: the OIDC
// provider's form (to a relying party) and the MCP authorization form (to the
// MCP client's loopback callback). Chromium checks that redirect against the
// page's CSP form-action, so `form-action 'self'` alone signed the user in and
// then blocked the hand-off. The other origin is a real listener on another
// loopback port (page.route does not see a redirect's follow-up request); a
// CSP violation would also show as a console error, which the fixture fails on.
const OTHER = "http://127.0.0.1:5398";

test.describe("sign-in forms that hand a code to another origin", () => {
  // One listener on a fixed port, so these run one after the other.
  test.describe.configure({ mode: "serial" });
  // No dmart session: with one, the OIDC provider signs in without a form.
  test.use({ storageState: { cookies: [], origins: [] } });

  let server: Server;
  test.beforeAll(async () => {
    server = createServer((_, res) => {
      res.writeHead(200, { "Content-Type": "text/html" });
      res.end("<!doctype html><title>other origin</title><p>other origin</p>");
    });
    await new Promise<void>((resolve) => server.listen(5398, "127.0.0.1", resolve));
  });
  test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())));

  test("the OIDC sign-in form reaches the relying party", async ({ page }) => {
    const query = new URLSearchParams({
      response_type: "code",
      client_id: "e2e-rp",
      redirect_uri: `${OTHER}/cb`,
      scope: "openid profile",
      state: "e2e-state",
      nonce: "e2e-nonce",
    });
    await page.goto(`/oidc/authorize?${query}`);
    await page.getByLabel("Username or email").fill(ADMIN);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();

    await page.waitForURL(`${OTHER}/cb?**`);
    const back = new URL(page.url());
    expect(back.searchParams.get("code")).toBeTruthy();
    expect(back.searchParams.get("state")).toBe("e2e-state");
    await expect(page.getByText("other origin")).toBeVisible();
  });

  test("the MCP authorization form reaches the client's callback", async ({ page, request }) => {
    const callback = `${OTHER}/callback`;
    const registered = await request.post("/oauth/register", {
      data: { redirect_uris: [callback], client_name: "e2e MCP client" },
    });
    expect(registered.status()).toBe(201);
    const { client_id } = await registered.json();

    const verifier = randomBytes(32).toString("base64url");
    const query = new URLSearchParams({
      response_type: "code",
      client_id,
      redirect_uri: callback,
      state: "mcp-state",
      code_challenge: createHash("sha256").update(verifier).digest("base64url"),
      code_challenge_method: "S256",
    });
    await page.goto(`/oauth/authorize?${query}`);
    await page.getByLabel("Shortname").fill(ADMIN);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in and authorize" }).click();

    await page.waitForURL(`${callback}?**`);
    const back = new URL(page.url());
    expect(back.searchParams.get("code")).toBeTruthy();
    expect(back.searchParams.get("state")).toBe("mcp-state");
  });
});
