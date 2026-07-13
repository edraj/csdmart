using Dmart.Models.Api;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Contracts;

public class ExceptionHierarchyTests
{
    [Fact]
    public void Subtypes_Are_DmartException_With_Expected_Status()
    {
        new DmartNotFoundException("x").ShouldBeAssignableTo<DmartException>();
        new DmartNotFoundException("x").StatusCode.ShouldBe(404);
        new DmartConflictException("x").StatusCode.ShouldBe(409);
        new DmartValidationException("x").StatusCode.ShouldBe(422);

        var denied = new DmartPermissionDeniedException("alice", "update", "app", "/a", "e1", "content");
        denied.ShouldBeAssignableTo<DmartException>();
        denied.StatusCode.ShouldBe(403);
        denied.Actor.ShouldBe("alice");
        denied.Action.ShouldBe("update");
    }

    [Fact]
    public void Base_Preserves_StatusCode_And_Error()
    {
        var ex = new DmartException(500, new Error("internal", 430, "boom", null));
        ex.StatusCode.ShouldBe(500);
        ex.Error.Message.ShouldBe("boom");
    }
}
