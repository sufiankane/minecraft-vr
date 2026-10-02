using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// Synthetic provider configuration safety: the deterministic sample rate
    /// is clamped through <see cref="SyntheticPoseProvider.SetSampleRateHz"/>,
    /// including the serialized/inspector path clamped by <c>OnValidate</c>, so
    /// a 0 or huge value cannot produce a zero/huge step.
    /// </summary>
    public class SyntheticPoseProviderTests
    {
        private const float Tolerance = 1e-6f;

        private GameObject root;
        private SyntheticPoseProvider provider;

        [SetUp]
        public void CreateProvider()
        {
            root = new GameObject("SyntheticPoseProvider");
            provider = root.AddComponent<SyntheticPoseProvider>();
        }

        [TearDown]
        public void DestroyProvider()
        {
            Object.DestroyImmediate(root);
            root = null;
            provider = null;
        }

        [Test]
        public void SampleRateClampsZeroNegativeHugeAndNaN()
        {
            provider.SetSampleRateHz(0f);
            Assert.AreEqual(SyntheticPoseProvider.MinSampleRateHz, provider.SampleRateHz, Tolerance, "zero");

            provider.SetSampleRateHz(-12f);
            Assert.AreEqual(SyntheticPoseProvider.MinSampleRateHz, provider.SampleRateHz, Tolerance, "negative");

            provider.SetSampleRateHz(float.MaxValue);
            Assert.AreEqual(SyntheticPoseProvider.MaxSampleRateHz, provider.SampleRateHz, Tolerance, "huge");

            provider.SetSampleRateHz(float.NaN);
            Assert.AreEqual(SyntheticPoseProvider.DefaultSampleRateHz, provider.SampleRateHz, Tolerance, "NaN");

            provider.SetSampleRateHz(120f);
            Assert.AreEqual(120f, provider.SampleRateHz, Tolerance, "in range untouched");
            Assert.AreEqual(1f / 120f, provider.StepSeconds, 1e-9f, "step follows the rate");
        }

        [Test]
        public void OnValidateClampsASerializedOutOfRangeRate()
        {
            FieldInfo field = typeof(SyntheticPoseProvider).GetField(
                "sampleRateHz", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo onValidate = typeof(SyntheticPoseProvider).GetMethod(
                "OnValidate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "serialized sampleRateHz field exists");
            Assert.IsNotNull(onValidate, "OnValidate hook exists");

            field.SetValue(provider, 0.25f);
            onValidate.Invoke(provider, null);
            Assert.AreEqual(
                SyntheticPoseProvider.MinSampleRateHz,
                provider.SampleRateHz,
                Tolerance,
                "serialized low clamps up");

            field.SetValue(provider, 5000f);
            onValidate.Invoke(provider, null);
            Assert.AreEqual(
                SyntheticPoseProvider.MaxSampleRateHz,
                provider.SampleRateHz,
                Tolerance,
                "serialized high clamps down");
        }
    }
}
