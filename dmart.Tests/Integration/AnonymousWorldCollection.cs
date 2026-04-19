using Xunit;

namespace Dmart.Tests.Integration;

// Serializes any test that mutates the shared "anonymous" user row or the
// "world" permission. xUnit runs distinct test collections in parallel by
// default — two classes touching these reserved shortnames concurrently
// cause nondeterministic teardown clobbering. Grouping them into one
// collection forces sequential execution (a collection serializes its
// classes by xUnit's default behavior). Test classes tagged with
// [Collection(AnonymousWorldCollection.Name)] join this scope.
[CollectionDefinition(Name)]
public sealed class AnonymousWorldCollection
{
    public const string Name = "AnonymousWorld";
}
