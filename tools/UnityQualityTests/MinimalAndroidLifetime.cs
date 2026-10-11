// No AndroidAssetPacks, JNI, Crest, URP, plugins, input code or render diagnostics.
using System.Collections;
using UnityEngine;
public sealed class MinimalAndroidLifetime : MonoBehaviour
{
    IEnumerator Start()
    {
        Debug.Log("MINIMAL_ANDROID_CONTROL_BEGIN " + Application.unityVersion + " " + SystemInfo.graphicsDeviceType);
        yield return new WaitForSecondsRealtime(20);
        Debug.Log("MINIMAL_ANDROID_CONTROL_COMPLETE");
        Application.Quit();
    }
}
