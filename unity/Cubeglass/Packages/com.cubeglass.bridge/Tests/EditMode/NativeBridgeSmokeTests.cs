using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Bridge.Tests
{
    public class NativeBridgeSmokeTests
    {
        [Test]
        public void PublishedHeadSample_RoundTripsThroughTheNativeBridge()
        {
            string pluginPath = Path.Combine(Application.dataPath, "Plugins", "win-x64", "cg_unity_bridge.dll");
            if (!File.Exists(pluginPath))
            {
                Assert.Fail(
                    $"native bridge plugin not found at '{pluginPath}'; build it first " +
                    "(cpp: cmake --build --preset windows-msvc) and copy it to Assets/Plugins/win-x64 " +
                    "(scripts/ci-local.ps1 does this before the Unity lane)");
            }

            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_create(), "cg_test_writer_create");
            try
            {
                var published = new BridgeHeadSample
                {
                    HostTime = 987654321L,
                    Pose = new BridgePose
                    {
                        Position = new BridgeVec3 { X = 1.5f, Y = -2.5f, Z = 3.5f },
                        Rotation = new BridgeQuat { W = 1f, X = 0f, Y = 0f, Z = 0f },
                    },
                    State = TrackState.Stable,
                    Sequence = 42u,
                };
                Assert.AreEqual(
                    BridgeStatus.Ok,
                    NativeBridge.cg_test_writer_publish_head(ref published),
                    "cg_test_writer_publish_head");

                Assert.IsTrue(BridgeClient.TryOpen(out BridgeClient client), "BridgeClient.TryOpen");
                using (client)
                {
                    Assert.IsTrue(client.TryReadHead(out BridgeHeadSample read), "BridgeClient.TryReadHead");
                    Assert.AreEqual(42u, read.Sequence);
                    Assert.AreEqual(987654321L, read.HostTime);
                    Assert.AreEqual(1.5f, read.Pose.Position.X);
                    Assert.AreEqual(-2.5f, read.Pose.Position.Y);
                    Assert.AreEqual(3.5f, read.Pose.Position.Z);
                    Assert.AreEqual(1f, read.Pose.Rotation.W);
                }
            }
            finally
            {
                NativeBridge.cg_test_writer_close();
            }
        }
    }
}
