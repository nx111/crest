// Optional actual-game-frame recorder. Install outside Editor in a disposable project.
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public sealed partial class PlayerAudit
{
    sealed class TemporalFrame
    {
        public AsyncGPUReadbackRequest request;
        public string metadata;
        public int frame;
    }

    readonly Queue<TemporalFrame> _temporalPending = new Queue<TemporalFrame>();
    readonly List<string> _temporalMetadata = new List<string>();
    RenderTexture _temporalScreen, _temporalSmall;
    Texture2D _temporalImage;
    ZipArchive _temporalArchive;
    bool _temporalRecording;
    string _temporalPhase = "startup";
    int _temporalCaptured, _temporalSkipped, _temporalErrors;

    partial void StartTemporalAudit()
    {
        if (System.Array.IndexOf(Arguments(), "-crestTemporalAudit") < 0) return;
        _temporalSmall = new RenderTexture(320, 180, 0, RenderTextureFormat.ARGB32);
        _temporalSmall.Create();
        _temporalImage = new Texture2D(320, 180, TextureFormat.RGBA32, false);
        _temporalArchive = new ZipArchive(File.Create(Path.Combine(_output, "game-frames.zip")), ZipArchiveMode.Create);
        _temporalMetadata.Add("frame,seconds,scene,phase,camera_x,camera_y,camera_z,camera_qx,camera_qy,camera_qz,camera_qw");
        _temporalRecording = true;
        StartCoroutine(RecordTemporalFrames());
    }

    partial void MarkTemporalPhase(string phase) { _temporalPhase = phase; }

    IEnumerator RecordTemporalFrames()
    {
        while (_temporalRecording)
        {
            yield return new WaitForEndOfFrame();
            if (!_temporalRecording) yield break;
            DrainTemporalFrames(false);
            if (_temporalPending.Count >= 16) { _temporalSkipped++; continue; }
            if (_temporalScreen == null || _temporalScreen.width != Screen.width || _temporalScreen.height != Screen.height)
            {
                if (_temporalScreen != null) { _temporalScreen.Release(); Destroy(_temporalScreen); }
                _temporalScreen = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
                _temporalScreen.Create();
            }
            // Sample the displayed framebuffer, not another camera render request.
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_temporalScreen);
            Graphics.Blit(_temporalScreen, _temporalSmall);
            var camera = Camera.main;
            var position = camera != null ? camera.transform.position : Vector3.zero;
            var rotation = camera != null ? camera.transform.rotation : Quaternion.identity;
            _temporalPending.Enqueue(new TemporalFrame
            {
                request = AsyncGPUReadback.Request(_temporalSmall, 0, TextureFormat.RGBA32),
                frame = Time.frameCount,
                metadata = string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2},{3},{4:R},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R}",
                    Time.frameCount, Time.realtimeSinceStartup, SceneManager.GetActiveScene().name, _temporalPhase,
                    position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w),
            });
        }
    }

    void DrainTemporalFrames(bool finish)
    {
        while (_temporalPending.Count > 0)
        {
            var sample = _temporalPending.Peek();
            if (finish && !sample.request.done && !sample.request.hasError) sample.request.WaitForCompletion();
            if (!sample.request.done && !sample.request.hasError) break;
            _temporalPending.Dequeue();
            if (sample.request.hasError) { _temporalErrors++; continue; }
            _temporalImage.LoadRawTextureData(sample.request.GetData<byte>());
            _temporalImage.Apply(false);
            var entry = _temporalArchive.CreateEntry("frame-" + sample.frame.ToString("D7") + ".jpg", System.IO.Compression.CompressionLevel.NoCompression);
            using (var stream = entry.Open())
            {
                var bytes = _temporalImage.EncodeToJPG(85);
                stream.Write(bytes, 0, bytes.Length);
            }
            _temporalMetadata.Add(sample.metadata);
            _temporalCaptured++;
        }
    }

    partial void FinishTemporalAudit(ref int code)
    {
        if (_temporalArchive == null) return;
        _temporalRecording = false;
        DrainTemporalFrames(true);
        using (var stream = new StreamWriter(_temporalArchive.CreateEntry("frames.csv").Open()))
            foreach (var line in _temporalMetadata) stream.WriteLine(line);
        _temporalArchive.Dispose();
        _temporalArchive = null;
        if (_temporalScreen != null) { _temporalScreen.Release(); Destroy(_temporalScreen); }
        _temporalSmall.Release(); Destroy(_temporalSmall); Destroy(_temporalImage);
        bool passed = _temporalCaptured > 360 && _temporalSkipped == 0 && _temporalErrors == 0;
        _results.Add((passed ? "PASS: " : "FAIL: ") + "Actual-game continuous frame recording: captured=" + _temporalCaptured
            + "; skipped=" + _temporalSkipped + "; readback errors=" + _temporalErrors
            + ". 320x180 JPEG previews with camera/scene/phase metadata; diagnostic recording cost is not a performance measurement.");
        if (!passed) code = 1;
    }
}
