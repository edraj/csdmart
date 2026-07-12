using Dmart.Client;
using Dmart.Models.Contracts;
using Dmart.SqlAdapter;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Contracts;

public class IDmartDataConformanceTests
{
    [Fact]
    public void Both_Backends_Implement_IDmartData()
    {
        typeof(IDmartData).IsAssignableFrom(typeof(DmartClient)).ShouldBeTrue();
        typeof(IDmartData).IsAssignableFrom(typeof(DmartSqlAdapter)).ShouldBeTrue();
    }

    [Fact]
    public void Client_Usable_Through_Interface_Reference()
    {
        IDmartData backend = new DmartClient("https://example.test");
        backend.ShouldNotBeNull();
        ((DmartClient)backend).Dispose();
    }
}
