using System.IO;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the path → (subpath, shortname) decoder used by the import path.
// The on-disk path is the source of truth for both fields; the meta JSON's
// shortname is overridden on import. These tests cover root, single-level,
// deeply-nested, and folder-vs-non-folder cases.
public sealed class ImportExportPathTests
{
    [Theory]
    // Non-folder at the space root.
    [InlineData("space/.dm/readme/meta.content.json",                       "/",                  "readme")]
    // Non-folder, single level deep.
    [InlineData("space/products/.dm/sku-1/meta.content.json",               "/products",          "sku-1")]
    // Non-folder, three levels deep — the regression scenario.
    [InlineData("space/a/b/c/.dm/leaf/meta.content.json",                   "/a/b/c",             "leaf")]
    // Non-folder, four levels deep.
    [InlineData("space/region/category/sub/group/.dm/item/meta.ticket.json", "/region/category/sub/group", "item")]
    // Folder at the space root.
    [InlineData("space/products/.dm/meta.folder.json",                      "/",                  "products")]
    // Folder, two levels deep.
    [InlineData("space/products/widgets/.dm/meta.folder.json",              "/products",          "widgets")]
    // Folder, four levels deep.
    [InlineData("space/region/cat/sub/leaf/.dm/meta.folder.json",           "/region/cat/sub",    "leaf")]
    public void DecodeEntryPath_Returns_Path_Authoritative_Subpath_And_Shortname(
        string zipPath, string expectedSubpath, string expectedShortname)
    {
        var (subpath, shortname) = ImportExportService.DecodeEntryPath(zipPath);

        subpath.ShouldBe(expectedSubpath);
        shortname.ShouldBe(expectedShortname);
    }

    [Theory]
    [InlineData("meta.simple.json",          "simple")]
    [InlineData("meta.with-dashes.json",     "with-dashes")]
    [InlineData("meta.with.inner.dots.json", "with.inner.dots")]
    public void DecodeAttachmentShortname_Strips_Meta_Prefix_And_Json_Suffix(
        string fname, string expected) =>
        ImportExportService.DecodeAttachmentShortname(fname).ShouldBe(expected);

    [Theory]
    // Wrong prefix — not "meta.".
    [InlineData("simple.json")]
    [InlineData("attachment.simple.json")]
    // Wrong suffix — not ".json".
    [InlineData("meta.simple.txt")]
    [InlineData("meta.simple")]
    public void DecodeAttachmentShortname_Throws_On_Malformed(string fname) =>
        Should.Throw<InvalidDataException>(() => ImportExportService.DecodeAttachmentShortname(fname));
}
