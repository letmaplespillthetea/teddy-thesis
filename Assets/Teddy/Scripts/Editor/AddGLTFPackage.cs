using UnityEditor;
using UnityEditor.PackageManager;

public static class AddGLTFPackage {
    [MenuItem("Tools/Add GLTF Package")]
    public static void AddPackage() {
        Client.Add("com.unity.cloud.gltfast");
    }
}
