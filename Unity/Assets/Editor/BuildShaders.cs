using System.IO;
using UnityEditor;

// Builds the plugin's shader bundle: Unity.exe -batchmode -quit -projectPath Unity -executeMethod BuildShaders.Build
public static class BuildShaders
{
    const string Out = "../_build/bundle";

    public static void Build()
    {
        Directory.CreateDirectory(Out);
        var build = new AssetBundleBuild
        {
            assetBundleName = "nightvision.bundle",
            assetNames = new[] { "Assets/Shaders/NvgBinocular.shader", "Assets/Shaders/NvgDirect.shader", "Assets/Shaders/NvgLensMask.shader", "Assets/Shaders/NvgGlow.shader", "Assets/Shaders/NvgMeshRead.shader", "Assets/Shaders/NvgShade.shader", "Assets/Shaders/NvgIrLight.shader", "Assets/Shaders/Fisheye.shader", "Assets/Shaders/NvgObjective.shader" },
        };
        if (BuildPipeline.BuildAssetBundles(Out, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64) == null)
            throw new System.Exception("Build failed");
        UnityEngine.Debug.Log("SHADER_BUNDLE_SUCCESS");
    }
}
