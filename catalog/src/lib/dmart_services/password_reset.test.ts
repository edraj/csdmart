import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  ResetError,
  createPasswordResetClient,
  detectIdentifier,
  isValidResetPassword,
} from "@shared/password-reset";
// The app-side binding is imported for its message-key map only. Importing it
// also constructs its transport, which reaches for catalog's tsdmart copy —
// stubbed here because the SDK is not what these tests exercise.
vi.mock("@edraj/tsdmart", () => ({
  Dmart: { passwordResetRequest: vi.fn(), axiosDmartInstance: { post: vi.fn() } },
}));
import { resetErrorKey } from "./password_reset";

// The shared client takes its transport by injection precisely so this needs
// no module mocking — and so the test cannot accidentally bind to a different
// copy of the SDK than the app does.
const requestReset = vi.fn();
const confirmReset = vi.fn();
const { requestPasswordReset, confirmPasswordReset } = createPasswordResetClient({
  requestReset: (body) => requestReset(body),
  confirmReset: (body) => confirmReset(body),
});

describe("detectIdentifier", () => {
  it("recognises an email and lowercases it", () => {
    expect(detectIdentifier("User@Example.COM")).toEqual({
      kind: "email",
      value: "user@example.com",
    });
  });

  it("trims surrounding whitespace", () => {
    expect(detectIdentifier("  a@b.co  ")).toEqual({ kind: "email", value: "a@b.co" });
  });

  it("recognises a bare msisdn", () => {
    expect(detectIdentifier("07701234567")).toEqual({
      kind: "msisdn",
      value: "07701234567",
    });
  });

  it("keeps a leading + and strips spaces, dashes and parentheses", () => {
    expect(detectIdentifier("+964 (770) 123-4567")).toEqual({
      kind: "msisdn",
      value: "+9647701234567",
    });
  });

  it("rejects digit strings shorter than 6", () => {
    expect(detectIdentifier("12345")).toBeNull();
  });

  // The server's msisdn pattern tops out at 15 digits (E.164), and
  // /password-reset-request answers an unmatchable number with a silent Ok().
  // Anything we let through past 15 would strand the user on the OTP screen.
  it("accepts exactly 15 digits", () => {
    expect(detectIdentifier("+123456789012345")).toEqual({
      kind: "msisdn",
      value: "+123456789012345",
    });
  });

  it("rejects digit strings longer than 15", () => {
    expect(detectIdentifier("1234567890123456")).toBeNull();
    expect(detectIdentifier("+1234567890123456")).toBeNull();
  });

  it("rejects an empty or whitespace-only string", () => {
    expect(detectIdentifier("")).toBeNull();
    expect(detectIdentifier("   ")).toBeNull();
  });

  it("rejects a malformed email and free text", () => {
    expect(detectIdentifier("user@example")).toBeNull();
    expect(detectIdentifier("not an identifier")).toBeNull();
  });
});

describe("isValidResetPassword", () => {
  it("accepts 8+ chars with a digit and an uppercase letter", () => {
    expect(isValidResetPassword("Password1")).toBe(true);
  });

  it("rejects a password with no uppercase letter", () => {
    expect(isValidResetPassword("password1")).toBe(false);
  });

  it("rejects a password with no digit", () => {
    expect(isValidResetPassword("Passwords")).toBe(false);
  });

  it("rejects fewer than 8 characters", () => {
    expect(isValidResetPassword("Pass123")).toBe(false);
  });

  it("rejects more than 64 characters", () => {
    expect(isValidResetPassword("A1" + "a".repeat(63))).toBe(false);
  });

  it("accepts exactly 64 characters", () => {
    expect(isValidResetPassword("A1" + "a".repeat(62))).toBe(true);
  });

  it("rejects characters outside the allowed set", () => {
    expect(isValidResetPassword("Password1€")).toBe(false);
  });

  // Arabic is unicameral, so the "uppercase" lookahead accepts any Arabic
  // letter. The requirements string shown to the user has to say so.
  it("accepts Arabic letters and Arabic-Indic digits", () => {
    expect(isValidResetPassword("مرحبا١٢٣٤")).toBe(true);
  });

  it("rejects an empty password", () => {
    expect(isValidResetPassword("")).toBe(false);
  });
});

describe("resetErrorKey", () => {
  it("maps every reason to a catalog message key", () => {
    expect(resetErrorKey("code_invalid")).toBe("ResetCodeInvalid");
    expect(resetErrorKey("password_rules")).toBe("PasswordRequirements");
    expect(resetErrorKey("account_locked")).toBe("ResetAccountLocked");
    expect(resetErrorKey("unknown")).toBe("ResetFailed");
  });
});

describe("requestPasswordReset", () => {
  beforeEach(() => {
    requestReset.mockReset();
    requestReset.mockResolvedValue({ status: "success" });
  });

  it("sends only the email field for an email identifier", async () => {
    await requestPasswordReset({ kind: "email", value: "a@b.co" });
    expect(requestReset).toHaveBeenCalledWith({ email: "a@b.co" });
  });

  it("sends only the msisdn field for an msisdn identifier", async () => {
    await requestPasswordReset({ kind: "msisdn", value: "+964770" });
    expect(requestReset).toHaveBeenCalledWith({ msisdn: "+964770" });
  });

  it("propagates a transport failure", async () => {
    requestReset.mockRejectedValue(new Error("Network Error"));
    await expect(
      requestPasswordReset({ kind: "email", value: "a@b.co" }),
    ).rejects.toThrow("Network Error");
  });
});

describe("confirmPasswordReset", () => {
  beforeEach(() => {
    confirmReset.mockReset();
    confirmReset.mockResolvedValue({ status: "success" });
  });

  it("posts the identifier, otp and password", async () => {
    await confirmPasswordReset({ kind: "email", value: "a@b.co" }, "123456", "Password1");
    expect(confirmReset).toHaveBeenCalledWith({
      email: "a@b.co",
      otp: "123456",
      password: "Password1",
    });
  });

  it("resolves on success", async () => {
    await expect(
      confirmPasswordReset({ kind: "msisdn", value: "+964770" }, "123456", "Password1"),
    ).resolves.toBeUndefined();
  });

  it("maps OTP_INVALID (307) to code_invalid", async () => {
    confirmReset.mockRejectedValue({ response: { data: { error: { code: 307 } } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "000000", "Password1"),
    ).rejects.toMatchObject({ reason: "code_invalid", code: 307 });
  });

  it("maps INVALID_PASSWORD_RULES (17) to password_rules", async () => {
    confirmReset.mockRejectedValue({ response: { data: { error: { code: 17 } } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "123456", "weak"),
    ).rejects.toMatchObject({ reason: "password_rules", code: 17 });
  });

  it("maps USER_ACCOUNT_LOCKED (110) to account_locked", async () => {
    confirmReset.mockRejectedValue({ response: { data: { error: { code: 110 } } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "000000", "Password1"),
    ).rejects.toMatchObject({ reason: "account_locked", code: 110 });
  });

  it("maps an unrecognised code to unknown", async () => {
    confirmReset.mockRejectedValue({ response: { data: { error: { code: 999 } } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "123456", "Password1"),
    ).rejects.toMatchObject({ reason: "unknown" });
  });

  it("maps a bodyless transport error to a ResetError", async () => {
    confirmReset.mockRejectedValue(new Error("Network Error"));
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "123456", "Password1"),
    ).rejects.toBeInstanceOf(ResetError);
  });

  it("treats a 2xx body with status failed as an error", async () => {
    confirmReset.mockResolvedValue({ status: "failed", error: { code: 307 } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "000000", "Password1"),
    ).rejects.toMatchObject({ reason: "code_invalid" });
  });
});
