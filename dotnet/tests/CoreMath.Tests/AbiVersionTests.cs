using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    [TestFixture]
    public sealed class AbiVersionTests
    {
        [Test]
        public void ValueTracksTheCAviVersionHeader()
        {
            string header = File.ReadAllText(TestPaths.ContractFile("contracts", "cg_types.h"));
            Match match = Regex.Match(header, @"#define\s+CG_ABI_VERSION\s+(?<version>\d+)");
            Assert.That(match.Success, Is.True, "contracts/cg_types.h must define CG_ABI_VERSION");
            int headerVersion = int.Parse(
                match.Groups["version"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture);

            Assert.That(
                AbiVersion.Value,
                Is.EqualTo(headerVersion),
                "AbiVersion.Value must mirror CG_ABI_VERSION so a managed handshake cannot claim a stale ABI");
        }
    }
}
