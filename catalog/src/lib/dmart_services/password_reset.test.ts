import { beforeEach, describe, expect, it, vi } from "vitest";
import { detectIdentifier, isValidResetPassword } from "./password_reset";
import {
  ResetError,
  confirmPasswordReset,
  requestPasswordReset,
} from "./password_reset";

const post = vi.fn();
const passwordResetRequest = vi.fn();

vi.mock("@edraj/tsdmart", () => ({
  Dmart: {
    get axiosDmartInstance() {
      return { post };
    },
    passwordResetRequest: (...args: any[]) => passwordResetRequest(...args),
  },
}));

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

  it("accepts Arabic letters and Arabic-Indic digits", () => {
    expect(isValidResetPassword("مرحبا١٢٣٤")).toBe(true);
  });

  it("rejects an empty password", () => {
    expect(isValidResetPassword("")).toBe(false);
  });
});

describe("requestPasswordReset", () => {
  beforeEach(() => {
    post.mockReset();
    passwordResetRequest.mockReset();
    passwordResetRequest.mockResolvedValue({ status: "success" });
  });

  it("sends only the email field for an email identifier", async () => {
    await requestPasswordReset({ kind: "email", value: "a@b.co" });
    expect(passwordResetRequest).toHaveBeenCalledWith({ email: "a@b.co" });
  });

  it("sends only the msisdn field for an msisdn identifier", async () => {
    await requestPasswordReset({ kind: "msisdn", value: "+964770" });
    expect(passwordResetRequest).toHaveBeenCalledWith({ msisdn: "+964770" });
  });

  it("propagates a transport failure", async () => {
    passwordResetRequest.mockRejectedValue(new Error("Network Error"));
    await expect(
      requestPasswordReset({ kind: "email", value: "a@b.co" }),
    ).rejects.toThrow("Network Error");
  });
});

describe("confirmPasswordReset", () => {
  beforeEach(() => {
    post.mockReset();
    post.mockResolvedValue({ data: { status: "success" } });
  });

  it("posts the identifier, otp and password", async () => {
    await confirmPasswordReset({ kind: "email", value: "a@b.co" }, "123456", "Password1");
    expect(post).toHaveBeenCalledWith("user/password-reset-confirm", {
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

  it("maps OTP_INVALID (307) to ResetCodeInvalid", async () => {
    post.mockRejectedValue({ response: { data: { error: { code: 307 } } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "000000", "Password1"),
    ).rejects.toMatchObject({ messageKey: "ResetCodeInvalid", code: 307 });
  });

  it("maps INVALID_PASSWORD_RULES (17) to PasswordRequirements", async () => {
    post.mockRejectedValue({ response: { data: { error: { code: 17 } } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "123456", "weak"),
    ).rejects.toMatchObject({ messageKey: "PasswordRequirements", code: 17 });
  });

  it("maps USER_ACCOUNT_LOCKED (110) to ResetAccountLocked", async () => {
    post.mockRejectedValue({ response: { data: { error: { code: 110 } } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "000000", "Password1"),
    ).rejects.toMatchObject({ messageKey: "ResetAccountLocked", code: 110 });
  });

  it("maps an unrecognised code to ResetFailed", async () => {
    post.mockRejectedValue({ response: { data: { error: { code: 999 } } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "123456", "Password1"),
    ).rejects.toMatchObject({ messageKey: "ResetFailed" });
  });

  it("maps a bodyless transport error to ResetFailed", async () => {
    post.mockRejectedValue(new Error("Network Error"));
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "123456", "Password1"),
    ).rejects.toBeInstanceOf(ResetError);
  });

  it("treats a 2xx body with status failed as an error", async () => {
    post.mockResolvedValue({ data: { status: "failed", error: { code: 307 } } });
    await expect(
      confirmPasswordReset({ kind: "email", value: "a@b.co" }, "000000", "Password1"),
    ).rejects.toMatchObject({ messageKey: "ResetCodeInvalid" });
  });
});
