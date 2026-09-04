using System.Text.Json.Serialization;
using Dmart.Models.Enums;

namespace Dmart.Models.Core;

public sealed record User
{
    // ----- Unique base -----
    public required string Shortname { get; init; }
    public required string SpaceName { get; init; }
    public required string Subpath { get; init; }

    // ----- Metas base -----
    public required string Uuid { get; init; }
    public bool IsActive { get; init; }
    public string? Slug { get; init; }
    public Translation? Displayname { get; init; }
    public Translation? Description { get; init; }
    public List<string> Tags { get; set; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public required string OwnerShortname { get; init; }
    public string? OwnerGroupShortname { get; init; }
    public Payload? Payload { get; init; }
    public string? LastChecksumHistory { get; init; }
    public ResourceType ResourceType { get; set; } = ResourceType.User;

    // ----- Users-specific -----
    [JsonIgnore]
    public string? Password { get; init; }    // hashed — never serialized to API responses
    public List<string> Roles { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public List<AclEntry>? Acl { get; init; }
    public List<Dictionary<string, object>>? Relationships { get; init; }
    public UserType Type { get; set; } = UserType.Web;
    public Language Language { get; set; } = Language.En;
    public string? Email { get; init; }
    public string? Msisdn { get; init; }
    public bool LockedToDevice { get; init; }
    public bool IsEmailVerified { get; init; }
    public bool IsMsisdnVerified { get; init; }
    public bool ForcePasswordChange { get; init; }
    public string? DeviceId { get; init; }
    public string? GoogleId { get; init; }
    public string? FacebookId { get; init; }
    public string? AppleId { get; init; }
    public string? SocialAvatarUrl { get; init; }
    public int? AttemptCount { get; init; }
    public Dictionary<string, object>? LastLogin { get; init; }
    // Timestamp (naive) of the most recent failed/blocked login attempt. Anchors
    // the auto-unlock cool-down (LockoutCooldownSeconds); null when clean. See
    // UserService.RejectIfAttemptLockedAsync.
    public DateTime? LastFailedLogin { get; init; }
    public string? Notes { get; init; }
    [JsonIgnore]
    public List<string> QueryPolicies { get; set; } = new();

    // Soft-delete state. Irreversible once set — see UserService.DeleteUserAsync.
    // A soft-deleted row keeps its shortname/uuid (so entries.owner_shortname etc.
    // keep resolving) but has Email/Msisdn/Password cleared.
    public bool IsDeleted { get; init; }
    public DateTime? DeletedAt { get; init; }

    // Single gate every auth/session check should read instead of IsActive alone:
    // IsActive is also flipped by login lockout and admin deactivation, both of
    // which are reversible — IsDeleted is not, and must never be masked by either.
    [JsonIgnore]
    public bool IsUsable => IsActive && !IsDeleted;
}
