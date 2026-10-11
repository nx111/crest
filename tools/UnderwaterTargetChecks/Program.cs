// Managed descriptor/identity substitutes for production SetUpVolumeTextures. No Unity/GPU emulation.
using System;
using UnityEngine;

namespace UnityEngine
{
    public enum RenderTextureFormat { DefaultHDR, Depth }
    public struct RenderTextureDescriptor
    {
        public int width, height, volumeDepth, depthBufferBits, msaaSamples;
        public bool useDynamicScale, bindMS;
        public RenderTextureFormat colorFormat;
    }
    public class RenderTexture
    {
        RenderTextureDescriptor _descriptor = new RenderTextureDescriptor { volumeDepth = 1, msaaSamples = 1 };
        public int Releases, DescriptorWrites;
        public bool Created;
        public int width => _descriptor.width;
        public int height => _descriptor.height;
        public int volumeDepth => _descriptor.volumeDepth;
        public bool useDynamicScale => _descriptor.useDynamicScale;
        public RenderTextureDescriptor descriptor
        {
            get => _descriptor;
            set
            {
                if (Created) throw new Exception("Target descriptor changed before releasing the native allocation");
                _descriptor = value;
                DescriptorWrites++;
            }
        }
        public void Release() { Releases++; Created = false; }
    }
}

namespace Crest
{
    // Identity only: a wrapper of the same RenderTexture remains valid when its descriptor changes.
    public sealed class RTHandleReference
    {
        public readonly RenderTexture Texture;
        public RTHandleReference(RenderTexture texture) { Texture = texture; }
    }

    public partial class UnderwaterRenderer
    {
        public enum Mode { FullScreen, Portal, Volume, VolumeFlyThrough }
        public Mode _mode;
        RenderTexture _volumeFrontFaceRT = new RenderTexture(), _volumeBackFaceRT = new RenderTexture();
        public RenderTexture Front => _volumeFrontFaceRT;
        public RenderTexture Back => _volumeBackFaceRT;
        public readonly RTHandleReference FrontHandle, BackHandle;
        public UnderwaterRenderer()
        {
            FrontHandle = new RTHandleReference(Front);
            BackHandle = new RTHandleReference(Back);
        }
        public void Setup(Mode mode, RenderTextureDescriptor descriptor)
        {
            _mode = mode;
            SetUpVolumeTextures(descriptor);
        }
    }

    static class Program
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static RenderTextureDescriptor Descriptor(int width = 1280, int height = 720, int slices = 1, bool dynamic = false)
            => new RenderTextureDescriptor
            {
                width = width, height = height, volumeDepth = slices, useDynamicScale = dynamic,
                colorFormat = RenderTextureFormat.DefaultHDR, depthBufferBits = 0, msaaSamples = 4, bindMS = true,
            };

        static void AssertTarget(RenderTexture target, RenderTextureDescriptor expected)
        {
            var actual = target.descriptor;
            Require(actual.width == expected.width && actual.height == expected.height,
                $"Target size {actual.width}x{actual.height} does not match {expected.width}x{expected.height}");
            Require(actual.volumeDepth == expected.volumeDepth && actual.useDynamicScale == expected.useDynamicScale,
                "Target retains a stale array depth or dynamic-resolution descriptor");
            Require(actual.colorFormat == RenderTextureFormat.Depth && actual.depthBufferBits == 24,
                "Required target has not been configured as a depth/stencil texture");
            Require(actual.msaaSamples == 1 && !actual.bindMS, "Sampled volume depth must be single-sample");
        }

        static void PortalToVolume(UnderwaterRenderer.Mode mode)
        {
            var renderer = new UnderwaterRenderer();
            var descriptor = Descriptor();
            renderer.Setup(UnderwaterRenderer.Mode.Portal, descriptor);
            AssertTarget(renderer.Front, descriptor);
            renderer.Front.Created = true;
            var front = renderer.Front;
            int frontReleases = front.Releases;
            renderer.Setup(mode, descriptor);
            AssertTarget(renderer.Back, descriptor);
            Require(ReferenceEquals(renderer.Front, front) && front.Created && front.Releases == frontReleases,
                "Configuring a newly required back target disturbed the matching front target");
        }

        static void ChangeDescriptorThroughPortal(RenderTextureDescriptor next)
        {
            foreach (var mode in new[] { UnderwaterRenderer.Mode.Volume, UnderwaterRenderer.Mode.VolumeFlyThrough })
            {
                var renderer = new UnderwaterRenderer();
                var first = Descriptor();
                renderer.Setup(mode, first);
                renderer.Front.Created = renderer.Back.Created = true;
                renderer.Setup(UnderwaterRenderer.Mode.Portal, next);
                AssertTarget(renderer.Front, next);
                AssertTarget(renderer.Back, first);
                renderer.Front.Created = true;
                int frontReleases = renderer.Front.Releases;
                renderer.Setup(mode, next);
                AssertTarget(renderer.Front, next);
                AssertTarget(renderer.Back, next);
                Require(renderer.Front.Created && renderer.Front.Releases == frontReleases,
                    "Updating the stale back target unnecessarily released the current front target");
            }
        }

        static int Main()
        {
            var cases = new (string Name, Action Test)[]
            {
                ("Portal to Volume initializes the previously unused back target at the same size",
                    () => PortalToVolume(UnderwaterRenderer.Mode.Volume)),
                ("Portal to VolumeFlyThrough initializes the back target at the same size",
                    () => PortalToVolume(UnderwaterRenderer.Mode.VolumeFlyThrough)),
                ("Resizing during Portal does not leave an older-sized back target on return to Volume",
                    () => ChangeDescriptorThroughPortal(Descriptor(1920, 1080))),
                ("Same-size array-depth and dynamic-resolution changes reach the newly required back target", () =>
                {
                    ChangeDescriptorThroughPortal(Descriptor(slices: 2));
                    ChangeDescriptorThroughPortal(Descriptor(dynamic: true));
                }),
                ("Portal only configures and updates the front target", () =>
                {
                    var renderer = new UnderwaterRenderer();
                    renderer.Setup(UnderwaterRenderer.Mode.Portal, Descriptor());
                    renderer.Setup(UnderwaterRenderer.Mode.Portal, Descriptor(1920, 1080));
                    Require(renderer.Back.Releases == 0 && renderer.Back.DescriptorWrites == 0,
                        "An unused Portal back target was configured or released");
                }),
                ("Matching targets keep native allocations and RenderTexture/RTHandle identity", () =>
                {
                    var renderer = new UnderwaterRenderer();
                    var front = renderer.Front; var back = renderer.Back;
                    var frontHandle = renderer.FrontHandle; var backHandle = renderer.BackHandle;
                    var descriptor = Descriptor();
                    renderer.Setup(UnderwaterRenderer.Mode.Volume, descriptor);
                    front.Created = back.Created = true;
                    int frontReleases = front.Releases, backReleases = back.Releases;
                    int frontWrites = front.DescriptorWrites, backWrites = back.DescriptorWrites;
                    for (int frame = 0; frame < 8; frame++)
                        renderer.Setup(frame % 2 == 0 ? UnderwaterRenderer.Mode.Volume : UnderwaterRenderer.Mode.VolumeFlyThrough, descriptor);
                    Require(front.Created && back.Created && front.Releases == frontReleases && back.Releases == backReleases
                        && front.DescriptorWrites == frontWrites && back.DescriptorWrites == backWrites,
                        "A matching target was released or reconfigured");
                    Require(ReferenceEquals(front, renderer.Front) && ReferenceEquals(back, renderer.Back)
                        && ReferenceEquals(frontHandle, renderer.FrontHandle) && ReferenceEquals(backHandle, renderer.BackHandle)
                        && ReferenceEquals(frontHandle.Texture, front) && ReferenceEquals(backHandle.Texture, back),
                        "Setup replaced a RenderTexture object or its existing wrapper");
                }),
            };
            int passed = 0;
            foreach (var test in cases)
            {
                try { test.Test(); passed++; Console.WriteLine("PASS " + test.Name); }
                catch (Exception error) { Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
            }
            Console.WriteLine($"{passed}/{cases.Length} underwater target checks passed.");
            return passed == cases.Length ? 0 : 1;
        }
    }
}
