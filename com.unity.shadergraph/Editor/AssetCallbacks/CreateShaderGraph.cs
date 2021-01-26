using UnityEngine;

namespace UnityEditor.ShaderGraph
{
    static class CreateShaderGraph
    {
        [MenuItem("Assets/Create/Shader Graph/Blank Shader Graph", false, 500)]
        public static void CreateBlankShaderGraph()
        {
            GraphUtil.CreateNewGraph();
        }
    }
}
