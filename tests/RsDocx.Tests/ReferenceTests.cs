using RsDocx.Core.Package;
using RsDocx.Core.Xt;

namespace RsDocx.Tests;

/// <summary>SpaceClaim-written reference files: must parse exactly, roundtrip byte-identically and pass the validator.</summary>
public class ReferenceTests
{
    public static TheoryData<string> References()
    {
        var data = new TheoryData<string>();
        var dir = Path.GetDirectoryName(KubusXtTests.TestData("kubus.rsdocx"))!;
        foreach (var f in Directory.GetFiles(dir, "*.rsdocx").Order()) data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(References))]
    public void Reference_parses_roundtrips_and_validates(string name)
    {
        var pkg = RsDocxPackage.Open(KubusXtTests.TestData(name));
        foreach (var part in pkg.Entries.Where(e => e.Path.EndsWith(".x_b")))
        {
            var bytes = pkg.GetPart(part.Path);
            var xt = XtReader.Read(bytes, out int used);
            Assert.Equal(bytes.Length, used);
            Assert.Equal(bytes, XtWriter.Write(xt));
            Assert.Empty(XtValidator.Validate(xt, strict: false));
        }
    }
}
