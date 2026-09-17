using Microsoft.Extensions.Options;

namespace Dmart.Config;

// Validates DmartSettings at startup. A misconfiguration (negative port, zero
// pool size, empty DB host, etc.) should fail the process loudly rather than
// producing obscure runtime errors later. Hooked up via
// builder.Services.AddSingleton<IValidateOptions<DmartSettings>, DmartSettingsValidator>()
// plus .ValidateOnStart() on the options registration.
internal sealed class DmartSettingsValidator : IValidateOptions<DmartSettings>
{
    public ValidateOptionsResult Validate(string? name, DmartSettings s)
    {
        var failures = new List<string>();

        if (s.ListeningPort is < 1 or > 65535)
            failures.Add($"ListeningPort must be 1-65535 (got {s.ListeningPort})");

        // Fail on an unrecognized driver rather than defaulting. A typo'd
        // DATABASE_DRIVER that silently ran on PostgreSQL would only surface
        // as "why is my SQLite file empty" long after deployment.
        // TryResolve, not TryParse: an ABSENT driver is inferred from whether a
        // PostgreSQL connection is configured, and the host/name rules below
        // must apply to the driver that inference actually selects.
        if (!Dmart.DataAdapters.Sql.DatabaseDriverParser.TryResolve(s, out var driver, out _))
        {
            failures.Add(
                $"DatabaseDriver '{s.DatabaseDriver}' is not recognized "
                + $"(supported: {Dmart.DataAdapters.Sql.DatabaseDriverParser.Supported})");
        }

        if (s.DatabasePort is < 1 or > 65535)
            failures.Add($"DatabasePort must be 1-65535 (got {s.DatabasePort})");
        if (s.DatabasePoolSize <= 0)
            failures.Add($"DatabasePoolSize must be > 0 (got {s.DatabasePoolSize})");
        if (s.DatabasePoolTimeout <= 0)
            failures.Add($"DatabasePoolTimeout must be > 0 (got {s.DatabasePoolTimeout})");
        if (s.DatabaseMaxOverflow < 0)
            failures.Add($"DatabaseMaxOverflow must be >= 0 (got {s.DatabaseMaxOverflow})");
        // The DATABASE_* connection settings describe PostgreSQL only; the
        // SQLite backend is configured by SqlitePath instead, so requiring them
        // in that mode would make a valid SQLite deployment refuse to start.
        var usingSqlite = driver == Dmart.DataAdapters.Sql.DatabaseDriver.Sqlite;
        if (!usingSqlite)
        {
            if (string.IsNullOrWhiteSpace(s.DatabaseHost) && string.IsNullOrWhiteSpace(s.PostgresConnection))
                failures.Add("DatabaseHost (or PostgresConnection) must be configured");
            if (string.IsNullOrWhiteSpace(s.DatabaseName) && string.IsNullOrWhiteSpace(s.PostgresConnection))
                failures.Add("DatabaseName (or PostgresConnection) must be configured");
        }
        else if (string.IsNullOrWhiteSpace(s.SqlitePath))
        {
            failures.Add("SqlitePath must be set when DatabaseDriver is 'sqlite'");
        }
        if (s.JwtAccessExpires <= 0)
            failures.Add($"JwtAccessExpires must be > 0 (got {s.JwtAccessExpires})");
        if (s.JwtRefreshDays <= 0)
            failures.Add($"JwtRefreshDays must be > 0 (got {s.JwtRefreshDays})");
        if (string.IsNullOrWhiteSpace(s.JwtSecret) || s.JwtSecret.Length < 32)
            failures.Add("JwtSecret must be at least 32 bytes (HS256 signing key)");
        else if (s.JwtSecret.Contains("change-me", StringComparison.OrdinalIgnoreCase))
            // The built-in default and config.env.sample placeholder are long
            // enough to pass the length floor but are publicly known — booting
            // on them means anyone can forge an admin JWT. Refuse to start.
            failures.Add("JwtSecret is the built-in placeholder ('change-me-…') — set a real random JWT_SECRET (e.g. `openssl rand -hex 32`); a known signing key lets anyone forge admin tokens");
        if (s.MaxFailedLoginAttempts < 0)
            failures.Add($"MaxFailedLoginAttempts must be >= 0 (got {s.MaxFailedLoginAttempts})");
        if (s.LockoutCooldownSeconds < 0)
            failures.Add($"LockoutCooldownSeconds must be >= 0 (got {s.LockoutCooldownSeconds})");
        if (s.AuthRateLimitPerMinute < 1)
            failures.Add($"AuthRateLimitPerMinute must be >= 1 (got {s.AuthRateLimitPerMinute})");
        // Argon2id creation parameters. Verification is unaffected by these —
        // it reads m/t/p from the stored hash — so a bad value here breaks new
        // passwords only, which is exactly the kind of failure that should stop
        // the process rather than surface at the first signup.
        if (s.PasswordHashIterations < 1)
            failures.Add($"PasswordHashIterations must be >= 1 (got {s.PasswordHashIterations})");
        if (s.PasswordHashParallelism < 1)
            failures.Add($"PasswordHashParallelism must be >= 1 (got {s.PasswordHashParallelism})");
        if (s.PasswordHashParallelism > 64)
            failures.Add($"PasswordHashParallelism must be <= 64 (got {s.PasswordHashParallelism})");
        // 7168 KiB is OWASP's floor for Argon2id at t=3/p=1; below it the memory
        // hardness stops being the thing protecting the hash.
        if (s.PasswordHashMemoryKb < 7168)
            failures.Add($"PasswordHashMemoryKb must be >= 7168 (got {s.PasswordHashMemoryKb})");
        // Argon2 requires m >= 8*p — fewer blocks than that and the lanes have
        // nothing to work on. libargon2 rejects it at hash time with
        // ARGON2_MEMORY_TOO_LITTLE; catch it at boot instead.
        if (s.PasswordHashMemoryKb < 8 * s.PasswordHashParallelism)
            failures.Add(
                $"PasswordHashMemoryKb must be >= 8 * PasswordHashParallelism "
                + $"(got {s.PasswordHashMemoryKb} for p={s.PasswordHashParallelism})");
        if (s.PasswordHashMemoryBudgetMb < 0)
            failures.Add($"PasswordHashMemoryBudgetMb must be >= 0, 0 meaning auto (got {s.PasswordHashMemoryBudgetMb})");
        if (s.PasswordHashQueueTimeoutSeconds < 1)
            failures.Add($"PasswordHashQueueTimeoutSeconds must be >= 1 (got {s.PasswordHashQueueTimeoutSeconds})");

        if (s.MaxQueryLimit < 1)
            failures.Add($"MaxQueryLimit must be >= 1 (got {s.MaxQueryLimit})");
        if (s.JqMaxConcurrency < 1)
            failures.Add($"JqMaxConcurrency must be >= 1 (got {s.JqMaxConcurrency})");
        if (s.JqQueueTimeoutSeconds < 1)
            failures.Add($"JqQueueTimeoutSeconds must be >= 1 (got {s.JqQueueTimeoutSeconds})");
        // 0 is meaningful (unlimited); negative is not.
        if (s.QueryTotalCap < 0)
            failures.Add($"QueryTotalCap must be >= 0, 0 meaning unlimited (got {s.QueryTotalCap})");
        if (s.RequestTimeout <= 0)
            failures.Add($"RequestTimeout must be > 0 (got {s.RequestTimeout})");
        if (!string.Equals(s.UserDeletionMode, "soft", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(s.UserDeletionMode, "hard", StringComparison.OrdinalIgnoreCase))
            failures.Add($"UserDeletionMode must be \"soft\" or \"hard\" (got \"{s.UserDeletionMode}\")");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
