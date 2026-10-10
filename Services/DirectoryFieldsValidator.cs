using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// The write-time rules for a user's mailbox, mail_aliases and services
// (docs/user-directory-fields.md). Returns a message for the first rule broken,
// or null. Callers pass values already normalized by DirectoryFields.
//
// The uniqueness checks here exist to give a readable error. They are not what
// makes uniqueness hold: two writers racing for the same alias are settled by
// the user_addresses primary key, inside the user write's own transaction.
public sealed class DirectoryFieldsValidator(
    UserRepository users, RegexPatternsConfig regexConfig, PermissionService perms, IOptions<DmartSettings> settings)
{
    // Per-service delegation (USER_SERVICE_GRANTERS), on the model of a role's
    // grantable_by. Only what the write CHANGES is checked: an actor trusted
    // with `mail` can turn mail on or off for a user who also has `gitea`
    // without being trusted with gitea. A global admin may change anything.
    // Returns a message naming the services the actor may not change, or null.
    public async Task<string?> CheckServiceGrantsAsync(
        string actor, IReadOnlyCollection<string> before, IReadOnlyCollection<string> after,
        CancellationToken ct = default)
    {
        var changed = before.Except(after, StringComparer.Ordinal)
            .Concat(after.Except(before, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (changed.Count == 0) return null;
        if (await perms.IsGlobalAdminAsync(actor, ct)) return null;

        var granters = settings.Value.ParseUserServiceGranters(out _);
        var ownRoles = (await users.GetByShortnameAsync(actor, ct))?.Roles ?? new List<string>();
        var denied = changed.Where(service =>
            !granters.TryGetValue(service, out var roles) || !ownRoles.Any(roles.Contains)).ToList();
        return denied.Count == 0 ? null : $"not permitted to grant or revoke service: {string.Join(", ", denied)}";
    }

    public async Task<string?> ValidateAsync(
        string shortname, string? mailbox, IReadOnlyList<string> aliases, IReadOnlyList<string> services,
        CancellationToken ct = default)
    {
        var s = settings.Value;

        if (aliases.Count > DirectoryFields.MaxAliases)
            return $"mail_aliases holds at most {DirectoryFields.MaxAliases} addresses";
        if (services.Count > DirectoryFields.MaxServices)
            return $"services holds at most {DirectoryFields.MaxServices} entries";
        // An alias delivers INTO a mailbox; without one it has nowhere to go.
        if (aliases.Count > 0 && mailbox is null)
            return "mail_aliases need a mailbox to deliver to";
        if (mailbox is not null && aliases.Contains(mailbox, StringComparer.Ordinal))
            return $"{mailbox} is already the mailbox; it cannot also be an alias";

        var allowedDomains = s.ParseUserMailDomains();
        var addresses = mailbox is null ? aliases : [mailbox, .. aliases];
        foreach (var address in addresses)
        {
            if (regexConfig.ValidateEmailFormat(address) is { } formatError)
                return $"{address}: {formatError}";
            if (allowedDomains.Length > 0
                && !allowedDomains.Contains(DirectoryFields.DomainOf(address), StringComparer.Ordinal))
                return $"{address} is not in a mail domain this deployment hosts ({string.Join(", ", allowedDomains)})";
        }

        var allowedServices = s.ParseUserServices();
        foreach (var service in services)
        {
            if (!DirectoryFields.IsValidService(service))
                return $"'{service}' is not a valid service name (lowercase letters, digits, '-' and '_')";
            if (allowedServices.Length > 0 && !allowedServices.Contains(service, StringComparer.Ordinal))
                return $"'{service}' is not a service this deployment offers ({string.Join(", ", allowedServices)})";
        }

        foreach (var address in addresses)
        {
            // Deliberately does not say WHO holds it: the caller may be allowed
            // to edit this user without being allowed to see the other one.
            if (await users.FindAddressOwnerAsync(address, ct) is { } owner
                && !string.Equals(owner.Shortname, shortname, StringComparison.Ordinal))
                return $"{address} is already in use";
            // An address that is ALSO someone else's contact email would make an
            // LDAP `mail=` lookup match two people, and Dovecot refuses a login
            // that resolves to more than one entry.
            if (await users.GetByEmailAsync(address, ct) is { IsDeleted: false } contact
                && !string.Equals(contact.Shortname, shortname, StringComparison.Ordinal))
                return $"{address} is already in use";
        }
        return null;
    }
}
