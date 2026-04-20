global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using Dmart.Utils;

// Expose `internal` helpers (e.g. PermissionService.BuildSubpathWalk) to the test
// project so unit tests can exercise pure logic without going through the public DI
// surface or reflection.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("dmart.Tests")]
