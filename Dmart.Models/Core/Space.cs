using System.Text.Json.Serialization;
using Dmart.Models.Enums;

namespace Dmart.Models.Core;

public sealed record Space
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
    public List<AclEntry>? Acl { get; init; }
    public Payload? Payload { get; init; }
    public List<Dictionary<string, object>>? Relationships { get; init; }
    public string? LastChecksumHistory { get; init; }
    public ResourceType ResourceType { get; set; } = ResourceType.Space;

    // ----- Spaces-specific -----
    public string RootRegistrationSignature { get; set; } = "";
    public string PrimaryWebsite { get; set; } = "";
    public bool IndexingEnabled { get; init; }
    public bool CaptureMisses { get; init; }
    public bool CheckHealth { get; init; }
    public List<Language> Languages { get; set; } = new();
    public string Icon { get; set; } = "";
    public List<string>? Mirrors { get; init; }
    public List<string>? HideFolders { get; init; }
    public bool? HideSpace { get; init; }
    public int? Ordinal { get; init; }
    [JsonIgnore]
    public List<string> QueryPolicies { get; set; } = new();
}
