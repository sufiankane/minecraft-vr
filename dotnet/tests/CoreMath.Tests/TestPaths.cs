using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    /// <summary>
    /// Locates shared repository contract files from the test output directory.
    /// </summary>
    internal static class TestPaths
    {
        /// <summary>Maximum number of parent directories to walk up from the test output directory.</summary>
        private const int MaxLevelsUp = 10;

        /// <summary>
        /// Walks up from <see cref="AppContext.BaseDirectory"/> at most
        /// <see cref="MaxLevelsUp"/> levels and returns the first
        /// <c>contracts/golden/transforms.json</c> found. When the fixture is
        /// absent the test fails with every searched path; a missing fixture
        /// must never silently skip the golden run.
        /// </summary>
        internal static string GoldenFixturePath()
        {
            string? directory = AppContext.BaseDirectory;
            List<string> searched = new List<string>();
            for (int level = 0; level <= MaxLevelsUp && !string.IsNullOrEmpty(directory); ++level)
            {
                string candidate = Path.Combine(directory, "contracts", "golden", "transforms.json");
                searched.Add(candidate);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = Path.GetDirectoryName(directory);
            }

            Assert.Fail(
                "cannot find contracts/golden/transforms.json; searched:" + Environment.NewLine +
                string.Join(Environment.NewLine, searched));
            return string.Empty;
        }
    }
}
