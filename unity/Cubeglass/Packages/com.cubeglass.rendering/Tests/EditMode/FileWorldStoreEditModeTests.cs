using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Review M-11 / TD-062: world names are single directory names under the
    /// save root, so names Windows cannot create (reserved device names,
    /// trailing dots or spaces) must be rejected at construction instead of
    /// aliasing or failing later. World names are case-insensitive on
    /// Windows/macOS (<c>Default</c> and <c>default</c> are the same
    /// directory), which the store documents rather than duplicating here.
    /// Also pins the delta size cap and the reparse-point refusal.
    /// </summary>
    public class FileWorldStoreEditModeTests
    {
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("a/b")]
        [TestCase("a\\b")]
        [TestCase("name.")]
        [TestCase("name ")]
        [TestCase("name..")]
        [TestCase("CON")]
        [TestCase("con")]
        [TestCase("CON.txt")]
        [TestCase("NUL")]
        [TestCase("LPT1")]
        [TestCase("COM9.log")]
        [TestCase("COM0")]
        [TestCase("LPT0")]
        [TestCase("AUX")]
        [TestCase("PRN")]
        public void RejectsUnsafeWorldNames(string worldName)
        {
            Assert.Throws<ArgumentException>(() => new FileWorldStore(worldName));
        }

        [Test]
        public void AcceptsAnOrdinaryNameAndCreatesTheWorldDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "cg-store-name-" + Guid.NewGuid().ToString("N"));
            var store = new FileWorldStore("Default-2", root);
            try
            {
                Assert.IsTrue(Directory.Exists(store.WorldDirectory), "the world directory is created");
            }
            finally
            {
                store.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void TransientSwapFailuresAreRetriedThenSucceed()
        {
            string root = Path.Combine(Path.GetTempPath(), "cg-store-retry-" + Guid.NewGuid().ToString("N"));
            var store = new FileWorldStore("retry", root);
            store.WriteRetryDelay = TimeSpan.Zero;
            try
            {
                var coord = new ChunkCoord(0, 0, 0);
                store.SaveAsync(coord, DeltaFor(coord, new Int3(1, 2, 3), new BlockId(5)), CancellationToken.None);
                Assert.IsTrue(
                    store.WaitForPendingWrites(TimeSpan.FromSeconds(10)).Succeeded,
                    "the fixture write must land before the swap path is exercised");

                int attempts = 0;
                FileWorldStore.SwapAttemptForTests = (temp, path) =>
                {
                    if (++attempts < 3)
                    {
                        return false;
                    }

                    File.Replace(temp, path, null);
                    return true;
                };
                store.SaveAsync(coord, DeltaFor(coord, new Int3(4, 5, 6), new BlockId(6)), CancellationToken.None);
                FlushResult result = store.WaitForPendingWrites(TimeSpan.FromSeconds(10));

                Assert.IsTrue(result.Succeeded, "the third attempt must succeed: {0}", result);
                Assert.AreEqual(3, attempts, "two transient refusals then one success (TD-021)");
                Assert.AreEqual(2, store.RetriedWrites, "both retries are counted");
                Assert.AreEqual(0, store.FailedWrites, "a retried-through write is not a failure");

                ChunkDelta loaded = store
                    .LoadAsync(coord, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                Assert.IsNotNull(loaded, "the retried payload must be loadable");
                Assert.AreEqual(new BlockId(6), loaded.Edits[new Int3(4, 5, 6)], "the retried content landed");
            }
            finally
            {
                FileWorldStore.SwapAttemptForTests = null;
                store.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void OversizedDeltaFileIsRejectedWithAClearCounter()
        {
            string root = Path.Combine(Path.GetTempPath(), "cg-store-cap-" + Guid.NewGuid().ToString("N"));
            var store = new FileWorldStore("cap", root);
            try
            {
                var coord = new ChunkCoord(0, 0, 0);
                File.WriteAllBytes(store.ChunkPath(coord), new byte[FileWorldStore.MaxDeltaFileBytes + 1]);

                ChunkDelta loaded = store
                    .LoadAsync(coord, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                Assert.IsNull(loaded, "an oversized delta must not be loaded");
                Assert.AreEqual(1, store.OversizedLoads, "the rejection is counted (TD-062)");
                Assert.AreEqual(0, store.RejectedLoads, "the codec was never reached");
            }
            finally
            {
                store.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void WorldDirectoryReparsePointIsRefused()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor
                && Application.platform != RuntimePlatform.WindowsPlayer
                && Application.platform != RuntimePlatform.WindowsServer)
            {
                Assert.Ignore("directory junctions are created through mklink (Windows-only test)");
            }

            string root = Path.Combine(Path.GetTempPath(), "cg-store-link-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            string link = Path.Combine(root, "linked");

            var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                Assert.AreEqual(0, process.ExitCode, "fixture: the junction must be created");
            }

            try
            {
                Assert.Throws<IOException>(
                    () => new FileWorldStore("linked", root),
                    "a world directory that is a reparse point must be refused (TD-062)");
            }
            finally
            {
                try
                {
                    Directory.Delete(link, false);
                }
                catch (Exception)
                {
                    // Deleting a junction never follows it; a failure here is
                    // harmless for the unique temp directory.
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static ChunkDelta DeltaFor(ChunkCoord coord, Int3 local, BlockId block)
        {
            return new ChunkDelta(coord, new Dictionary<Int3, BlockId> { { local, block } });
        }

        [Test]
        public void DeltaPathReparsePointIsRefusedOnLoad()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor
                && Application.platform != RuntimePlatform.WindowsPlayer
                && Application.platform != RuntimePlatform.WindowsServer)
            {
                Assert.Ignore("directory junctions are created through mklink (Windows-only test)");
            }

            string root = Path.Combine(Path.GetTempPath(), "cg-store-reparse-" + Guid.NewGuid().ToString("N"));
            var store = new FileWorldStore("reparse", root);
            var coord = new ChunkCoord(0, 0, 0);
            try
            {
                store.SaveAsync(coord, DeltaFor(coord, new Int3(1, 2, 3), new BlockId(5)), CancellationToken.None);
                Assert.IsTrue(
                    store.WaitForPendingWrites(TimeSpan.FromSeconds(10)).Succeeded,
                    "fixture delta must write");

                // A junction exercises the same reparse-point refusal as a file
                // symlink but is created with mklink /J, which needs neither
                // Developer Mode nor elevation; a bare mklink for a file
                // symlink needs SeCreateSymbolicLinkPrivilege, which headless
                // CI does not have (TD-062). A junction planted at the delta
                // path carries FILE_ATTRIBUTE_REPARSE_POINT even though
                // File.Exists reports it as absent.
                string path = store.ChunkPath(coord);
                File.Delete(path);
                string target = Path.Combine(root, "target");
                Directory.CreateDirectory(target);

                var start = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c mklink /J \"" + path + "\" \"" + target + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                using (Process link = Process.Start(start))
                {
                    link.WaitForExit();
                    Assert.AreEqual(0, link.ExitCode, "fixture: the junction must be created");
                }

                ChunkDelta loaded = store
                    .LoadAsync(coord, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                Assert.IsNull(loaded, "a delta reparse point must never be followed (TD-062)");
                Assert.AreEqual(1, store.RefusedLoads, "the refusal is counted");
            }
            finally
            {
                store.Dispose();
                try
                {
                    Directory.Delete(store.ChunkPath(coord), false);
                }
                catch (Exception)
                {
                    // Deleting a junction never follows it; a failure here is
                    // harmless for the unique temp directory.
                }

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }
}

