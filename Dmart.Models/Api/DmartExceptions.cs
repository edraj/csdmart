namespace Dmart.Models.Api;

// The entry / space / user could not be found (HTTP 404).
public class DmartNotFoundException : DmartException
{
    public DmartNotFoundException(int statusCode, Error error) : base(statusCode, error) { }
    public DmartNotFoundException(string message)
        : base(404, new Error(ErrorTypes.Db, InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, message, null)) { }
}

// A write conflicts with an existing row / unique constraint (HTTP 409).
public class DmartConflictException : DmartException
{
    public DmartConflictException(int statusCode, Error error) : base(statusCode, error) { }
    public DmartConflictException(string message)
        : base(409, new Error(ErrorTypes.Db, InternalErrorCode.SHORTNAME_ALREADY_EXIST, message, null)) { }
    public DmartConflictException(string message, Exception? innerException)
        : base(409, new Error(ErrorTypes.Db, InternalErrorCode.SHORTNAME_ALREADY_EXIST, message, null), innerException) { }
}

// Caller supplied invalid input (HTTP 422).
public class DmartValidationException : DmartException
{
    public DmartValidationException(int statusCode, Error error) : base(statusCode, error) { }
    public DmartValidationException(string message)
        : base(422, new Error(ErrorTypes.Request, InternalErrorCode.INVALID_DATA, message, null)) { }
}

// The actor lacks permission for the requested action (HTTP 403). Carries the
// action + target so handlers can map it to a 403 response. The (statusCode,
// error) ctor is used by the HTTP client, where the wire response doesn't carry
// the actor/action detail (those fields are left empty).
public class DmartPermissionDeniedException : DmartException
{
    public string Actor { get; }
    public string Action { get; }
    public string SpaceName { get; }
    public string Subpath { get; }
    public string Shortname { get; }
    public string ResourceType { get; }

    public DmartPermissionDeniedException(int statusCode, Error error) : base(statusCode, error)
    {
        Actor = Action = SpaceName = Subpath = Shortname = ResourceType = string.Empty;
    }

    public DmartPermissionDeniedException(string actor, string action,
        string spaceName, string subpath, string shortname, string resourceType)
        : base(403, new Error(ErrorTypes.Auth, InternalErrorCode.NOT_ALLOWED,
            $"Permission denied for actor '{actor}': {action} on " +
            $"{spaceName}{subpath}/{shortname} ({resourceType})", null))
    {
        Actor = actor;
        Action = action;
        SpaceName = spaceName;
        Subpath = subpath;
        Shortname = shortname;
        ResourceType = resourceType;
    }
}
