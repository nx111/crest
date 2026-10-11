// Project compatibility fix for the Unity 6000.6.2f1 Android startup probe.
// This file is subject to the MIT License in the repository root.

#if UNITY_ANDROID
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor.Android;
using UnityEngine;

namespace Crest.Development
{
    class AndroidAssetPackDependency : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 100;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // This version probes AssetPackManager and logs an error even for an ordinary APK.
            // Keep the workaround limited to the engine version verified on the device.
            if (Application.unityVersion != "6000.6.2f1") return;

            var gradle = Path.Combine(path, "build.gradle");
            var text = File.ReadAllText(gradle);
            if (Regex.IsMatch(text, @"(?m)^\s*(?:api|implementation)\s*(?:\(\s*)?[""']com\.google\.android\.play:asset-delivery:"))
                return;

            File.AppendAllText(gradle, "\n// Unity 6000.6.2f1 Android startup compatibility.\n"
                + "dependencies {\n    implementation 'com.google.android.play:asset-delivery:2.3.0'\n}\n");
            Debug.Log("Android startup compatibility: added Play Asset Delivery 2.3.0 for Unity 6000.6.2f1.");
        }
    }
}
#endif
