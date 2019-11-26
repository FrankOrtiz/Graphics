using UnityEngine;
using System;
using System.Linq;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEditorInternal.VR;
using UnityEditor.SceneManagement;
using System.Linq.Expressions;
using System.Reflection;
using System.Collections.Generic;
using System.IO;

namespace UnityEditor.Rendering.HighDefinition
{
    enum InclusiveScope
    {
        HDRPAsset = 1 << 0,
        HDRP = HDRPAsset | 1 << 1, //HDRPAsset is inside HDRP and will be indented
        VR = 1 << 2,
        DXR = 1 << 3,
    }

    static class InclusiveScopeExtention
    {
        public static bool Contains(this InclusiveScope thisScope, InclusiveScope scope)
            => ((~thisScope) & scope) == 0;
    }

    partial class HDWizard
    {
        #region REFLECTION

        static Func<BuildTarget, bool> WillEditorUseFirstGraphicsAPI;

        static void LoadReflectionMethods()
        {
            var buildTargetParameter = Expression.Parameter(typeof(BuildTarget), "platform");
            var willEditorUseFirstGraphicsAPIInfo = typeof(PlayerSettingsEditor).GetMethod("WillEditorUseFirstGraphicsAPI", BindingFlags.Static | BindingFlags.NonPublic);
            var willEditorUseFirstGraphicsAPILambda = Expression.Lambda<Func<BuildTarget, bool>>(Expression.Call(null, willEditorUseFirstGraphicsAPIInfo, buildTargetParameter), buildTargetParameter);
            WillEditorUseFirstGraphicsAPI = willEditorUseFirstGraphicsAPILambda.Compile();
        }

        #endregion

        #region Entry

        struct Entry
        {
            public delegate bool Checker();
            public delegate void Fixer(bool fromAsync);

            public readonly InclusiveScope scope;
            public readonly Style.ConfigStyle configStyle;
            public readonly Checker check;
            public readonly Fixer fix;
            public readonly int indent;
            public Entry(InclusiveScope scope, Style.ConfigStyle configStyle, Checker check, Fixer fix)
            {
                this.scope = scope;
                this.configStyle = configStyle;
                this.check = check;
                this.fix = fix;
                indent = scope == InclusiveScope.HDRPAsset ? 1 : 0;
            }
        }

        //To add elements in the Wizard configuration checker,
        //add your new checks in this array at the right position.
        //Both "Fix All" button and UI drawing will use it.
        //Indentation is computed in Entry if you use certain subscope.
        Entry[] m_Entries;
        Entry[] entries
        {
            get
            {
                // due to functor, cannot static link directly in an array and need lazy init
                if (m_Entries == null)
                    m_Entries = new[]
                    {
                        new Entry(InclusiveScope.HDRP, Style.hdrpColorSpace, IsColorSpaceCorrect, FixColorSpace),
                        new Entry(InclusiveScope.HDRP, Style.hdrpLightmapEncoding, IsLightmapCorrect, FixLightmap),
                        new Entry(InclusiveScope.HDRP, Style.hdrpShadowmask, IsShadowmaskCorrect, FixShadowmask),
                        new Entry(InclusiveScope.HDRP, Style.hdrpAsset, IsHdrpAssetCorrect, FixHdrpAsset),
                        new Entry(InclusiveScope.HDRPAsset, Style.hdrpAssetAssigned, IsHdrpAssetUsedCorrect, FixHdrpAssetUsed),
                        new Entry(InclusiveScope.HDRPAsset, Style.hdrpAssetRuntimeResources, IsHdrpAssetRuntimeResourcesCorrect, FixHdrpAssetRuntimeResources),
                        new Entry(InclusiveScope.HDRPAsset, Style.hdrpAssetEditorResources, IsHdrpAssetEditorResourcesCorrect, FixHdrpAssetEditorResources),
                        new Entry(InclusiveScope.HDRPAsset, Style.hdrpBatcher, IsSRPBatcherCorrect, FixSRPBatcher),
                        new Entry(InclusiveScope.HDRPAsset, Style.hdrpAssetDiffusionProfile, IsHdrpAssetDiffusionProfileCorrect, FixHdrpAssetDiffusionProfile),
                        new Entry(InclusiveScope.HDRP, Style.hdrpScene, IsDefaultSceneCorrect, FixDefaultScene),
                        new Entry(InclusiveScope.HDRP, Style.hdrpVolumeProfile, IsDefaultVolumeProfileAssigned, FixDefaultVolumeProfileAssigned),

                        new Entry(InclusiveScope.VR, Style.vrActivated, IsVRSupportedForCurrentBuildTargetGroupCorrect, FixVRSupportedForCurrentBuildTargetGroup),

                        new Entry(InclusiveScope.DXR, Style.dxrAutoGraphicsAPI, IsDXRAutoGraphicsAPICorrect, FixDXRAutoGraphicsAPI),
                        new Entry(InclusiveScope.DXR, Style.dxrD3D12, IsDXRDirect3D12Correct, FixDXRDirect3D12),
                        new Entry(InclusiveScope.DXR, Style.dxrStaticBatching, IsDXRStaticBatchingCorrect, FixDXRStaticBatching),
                        new Entry(InclusiveScope.DXR, Style.dxrScreenSpaceShadow, IsDXRScreenSpaceShadowCorrect, FixDXRScreenSpaceShadow),
                        new Entry(InclusiveScope.DXR, Style.dxrReflections, IsDXRReflectionsCorrect, FixDXRReflections),
                        new Entry(InclusiveScope.DXR, Style.dxrActivated, IsDXRActivationCorrect, FixDXRActivation),
                        new Entry(InclusiveScope.DXR, Style.dxrResources, IsDXRAssetCorrect, FixDXRAsset),
                        new Entry(InclusiveScope.DXR, Style.dxrShaderConfig, IsDXRShaderConfigCorrect, FixDXRShaderConfig),
                        new Entry(InclusiveScope.DXR, Style.dxrScene, IsDXRDefaultSceneCorrect, FixDXRDefaultScene),
                    };
                return m_Entries;
            }
        }

        // Utility that grab all check within the scope or in sub scope included and check if everything is correct
        bool IsAllEntryCorrectInScope(InclusiveScope scope)
        {
            IEnumerable<Entry.Checker> checks = entries.Where(e => scope.Contains(e.scope)).Select(e => e.check);
            if (checks.Count() == 0)
                return true;

            IEnumerator<Entry.Checker> enumerator = checks.GetEnumerator();
            enumerator.MoveNext();
            bool result = enumerator.Current();
            if (enumerator.MoveNext())
                for (; result && enumerator.MoveNext();)
                    result &= enumerator.Current();
            return result;
        }

        // Utility that grab all check and fix within the scope or in sub scope included and performe fix if check return incorrect
        void FixAllEntryInScope(InclusiveScope scope)
        {
            IEnumerable<(Entry.Checker, Entry.Fixer)> pairs = entries.Where(e => scope.Contains(e.scope)).Select(e => (e.check, e.fix));
            if (pairs.Count() == 0)
                return;

            foreach ((Entry.Checker check, Entry.Fixer fix) in pairs)
                m_Fixer.Add(() =>
                {
                    if (!check())
                        fix(fromAsync: true);
                });
        }

        #endregion 

        #region Queue

        class QueuedLauncher
        {
            Queue<Action> m_Queue = new Queue<Action>();
            bool m_Running = false;
            bool m_StopRequested = false;

            public void Stop() => m_StopRequested = true;

            void Start()
            {
                m_Running = true;
                EditorApplication.update += Run;
            }

            void End()
            {
                EditorApplication.update -= Run;
                m_Running = false;
            }

            void Run()
            {
                if (m_StopRequested)
                {
                    m_Queue.Clear();
                    m_StopRequested = false;
                }
                if (m_Queue.Count > 0)
                    m_Queue.Dequeue()?.Invoke();
                else
                    End();
            }

            public void Add(Action function)
            {
                m_Queue.Enqueue(function);
                if (!m_Running)
                    Start();
            }

            public void Add(params Action[] functions)
            {
                foreach (Action function in functions)
                    Add(function);
            }
        }
        QueuedLauncher m_Fixer = new QueuedLauncher();

        #endregion

        #region HDRP_FIXES

        bool IsHDRPAllCorrect()
            => IsAllEntryCorrectInScope(InclusiveScope.HDRP);
        void FixHDRPAll()
            => FixAllEntryInScope(InclusiveScope.HDRP);

        bool IsHdrpAssetCorrect()
            => IsAllEntryCorrectInScope(InclusiveScope.HDRPAsset);
        void FixHdrpAsset(bool fromAsyncUnused)
            => FixAllEntryInScope(InclusiveScope.HDRPAsset);

        bool IsColorSpaceCorrect()
            => PlayerSettings.colorSpace == ColorSpace.Linear;
        void FixColorSpace(bool fromAsyncUnused)
            => PlayerSettings.colorSpace = ColorSpace.Linear;

        bool IsLightmapCorrect()
        {
            // Shame alert: plateform supporting Encodement are partly hardcoded
            // in editor (Standalone) and for the other part, it is all in internal code.
            return PlayerSettings.GetLightmapEncodingQualityForPlatformGroup(BuildTargetGroup.Standalone) == LightmapEncodingQuality.High
                && PlayerSettings.GetLightmapEncodingQualityForPlatformGroup(BuildTargetGroup.Android) == LightmapEncodingQuality.High
                && PlayerSettings.GetLightmapEncodingQualityForPlatformGroup(BuildTargetGroup.Lumin) == LightmapEncodingQuality.High
                && PlayerSettings.GetLightmapEncodingQualityForPlatformGroup(BuildTargetGroup.WSA) == LightmapEncodingQuality.High;
        }
        void FixLightmap(bool fromAsyncUnused)
        {
            PlayerSettings.SetLightmapEncodingQualityForPlatformGroup(BuildTargetGroup.Standalone, LightmapEncodingQuality.High);
            PlayerSettings.SetLightmapEncodingQualityForPlatformGroup(BuildTargetGroup.Android, LightmapEncodingQuality.High);
            PlayerSettings.SetLightmapEncodingQualityForPlatformGroup(BuildTargetGroup.Lumin, LightmapEncodingQuality.High);
            PlayerSettings.SetLightmapEncodingQualityForPlatformGroup(BuildTargetGroup.WSA, LightmapEncodingQuality.High);
        }

        bool IsShadowmaskCorrect()
            //QualitySettings.SetQualityLevel.set quality is too costy to be use at frame
            => QualitySettings.shadowmaskMode == ShadowmaskMode.DistanceShadowmask;
        void FixShadowmask(bool fromAsyncUnused)
        {
            int currentQuality = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; ++i)
            {
                QualitySettings.SetQualityLevel(i, applyExpensiveChanges: false);
                QualitySettings.shadowmaskMode = ShadowmaskMode.DistanceShadowmask;
            }
            QualitySettings.SetQualityLevel(currentQuality, applyExpensiveChanges: false);
        }

        bool IsHdrpAssetUsedCorrect()
            => GraphicsSettings.renderPipelineAsset != null && GraphicsSettings.renderPipelineAsset is HDRenderPipelineAsset;
        void FixHdrpAssetUsed(bool fromAsync)
        {
            if (ObjectSelectorUtility.opened)
                return;
            CreateOrLoad<HDRenderPipelineAsset>(fromAsync
                ? () => m_Fixer.Stop()
            : (Action)null,
                asset => GraphicsSettings.renderPipelineAsset = asset);
        }

        bool IsHdrpAssetRuntimeResourcesCorrect()
            => IsHdrpAssetUsedCorrect()
            && HDRenderPipeline.defaultAsset.renderPipelineResources != null;
        void FixHdrpAssetRuntimeResources(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);

            var hdrpAsset = HDRenderPipeline.defaultAsset;
            if (hdrpAsset == null)
                return;

            hdrpAsset.renderPipelineResources
                = AssetDatabase.LoadAssetAtPath<RenderPipelineResources>(HDUtils.GetHDRenderPipelinePath() + "Runtime/RenderPipelineResources/HDRenderPipelineResources.asset");
            ResourceReloader.ReloadAllNullIn(HDRenderPipeline.defaultAsset.renderPipelineResources, HDUtils.GetHDRenderPipelinePath());
        }

        bool IsHdrpAssetEditorResourcesCorrect()
            => IsHdrpAssetUsedCorrect()
            && HDRenderPipeline.defaultAsset.renderPipelineEditorResources != null;
        void FixHdrpAssetEditorResources(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);

            var hdrpAsset = HDRenderPipeline.defaultAsset;
            if (hdrpAsset == null)
                return;

            hdrpAsset.renderPipelineEditorResources
                = AssetDatabase.LoadAssetAtPath<HDRenderPipelineEditorResources>(HDUtils.GetHDRenderPipelinePath() + "Editor/RenderPipelineResources/HDRenderPipelineEditorResources.asset");
            ResourceReloader.ReloadAllNullIn(HDRenderPipeline.defaultAsset.renderPipelineEditorResources, HDUtils.GetHDRenderPipelinePath());
        }

        bool IsSRPBatcherCorrect()
            => IsHdrpAssetUsedCorrect() && HDRenderPipeline.currentAsset.enableSRPBatcher;
        void FixSRPBatcher(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);

            var hdrpAsset = HDRenderPipeline.defaultAsset;
            if (hdrpAsset == null)
                return;

            hdrpAsset.enableSRPBatcher = true;
            EditorUtility.SetDirty(hdrpAsset);
        }

        bool IsHdrpAssetDiffusionProfileCorrect()
        {
            var profileList = HDRenderPipeline.defaultAsset?.diffusionProfileSettingsList;
            return IsHdrpAssetUsedCorrect() && profileList.Length != 0 && profileList.Any(p => p != null);
        }
        void FixHdrpAssetDiffusionProfile(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);

            var hdrpAsset = HDRenderPipeline.defaultAsset;
            if (hdrpAsset == null)
                return;

            var defaultAssetList = hdrpAsset.renderPipelineEditorResources.defaultDiffusionProfileSettingsList;
            hdrpAsset.diffusionProfileSettingsList = new DiffusionProfileSettings[0]; // clear the diffusion profile list

            foreach (var diffusionProfileAsset in defaultAssetList)
            {
                string defaultDiffusionProfileSettingsPath = "Assets/" + HDProjectSettings.projectSettingsFolderPath + "/" + diffusionProfileAsset.name + ".asset";
                AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(diffusionProfileAsset), defaultDiffusionProfileSettingsPath);

                var userAsset = AssetDatabase.LoadAssetAtPath<DiffusionProfileSettings>(defaultDiffusionProfileSettingsPath);
                hdrpAsset.AddDiffusionProfile(userAsset);
            }

            EditorUtility.SetDirty(hdrpAsset);
        }

        bool IsDefaultSceneCorrect()
            => HDProjectSettings.defaultScenePrefab != null;
        void FixDefaultScene(bool fromAsync)
        {
            if (ObjectSelectorUtility.opened)
                return;
            CreateOrLoadDefaultScene(fromAsync ? () => m_Fixer.Stop() : (Action)null, scene => HDProjectSettings.defaultScenePrefab = scene, forDXR: false);
            m_DefaultScene.SetValueWithoutNotify(HDProjectSettings.defaultScenePrefab);
        }

        bool IsDefaultVolumeProfileAssigned()
        {
            if (!IsHdrpAssetUsedCorrect())
                return false;

            var hdAsset = HDRenderPipeline.currentAsset;
            return hdAsset.defaultVolumeProfile != null && !hdAsset.defaultVolumeProfile.Equals(null);
        }
        void FixDefaultVolumeProfileAssigned(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);

            var hdrpAsset = HDRenderPipeline.currentAsset;
            if (hdrpAsset == null)
                return;

            EditorDefaultSettings.GetOrAssignDefaultVolumeProfile(hdrpAsset);
            EditorUtility.SetDirty(hdrpAsset);
        }

        #endregion

        #region HDRP_VR_FIXES

        bool IsVRAllCorrect()
            => IsAllEntryCorrectInScope(InclusiveScope.VR);
        void FixVRAll()
            => FixAllEntryInScope(InclusiveScope.VR);

        bool IsVRSupportedForCurrentBuildTargetGroupCorrect()
            => VREditor.GetVREnabledOnTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
        void FixVRSupportedForCurrentBuildTargetGroup(bool fromAsyncUnused)
            => VREditor.SetVREnabledOnTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup, true);

        #endregion

        #region HDRP_DXR_FIXES

        bool IsDXRAllCorrect()
            => IsAllEntryCorrectInScope(InclusiveScope.DXR);

        void FixDXRAll()
            => FixAllEntryInScope(InclusiveScope.DXR);

        bool IsDXRAutoGraphicsAPICorrect()
            => !PlayerSettings.GetUseDefaultGraphicsAPIs(EditorUserBuildSettingsUtils.CalculateSelectedBuildTarget());
        void FixDXRAutoGraphicsAPI(bool fromAsyncUnused)
            => PlayerSettings.SetUseDefaultGraphicsAPIs(EditorUserBuildSettingsUtils.CalculateSelectedBuildTarget(), false);

        bool IsDXRDirect3D12Correct()
            => PlayerSettings.GetGraphicsAPIs(EditorUserBuildSettingsUtils.CalculateSelectedBuildTarget()).FirstOrDefault() == GraphicsDeviceType.Direct3D12;
        void FixDXRDirect3D12(bool fromAsync)
        {
            if (PlayerSettings.GetSupportedGraphicsAPIs(EditorUserBuildSettingsUtils.CalculateSelectedBuildTarget()).Contains(GraphicsDeviceType.Direct3D12))
            {
                var buidTarget = EditorUserBuildSettingsUtils.CalculateSelectedBuildTarget();
                if (PlayerSettings.GetGraphicsAPIs(buidTarget).Contains(GraphicsDeviceType.Direct3D12))
                {
                    PlayerSettings.SetGraphicsAPIs(
                        buidTarget,
                        new[] { GraphicsDeviceType.Direct3D12 }
                            .Concat(
                                PlayerSettings.GetGraphicsAPIs(buidTarget)
                                    .Where(x => x != GraphicsDeviceType.Direct3D12))
                            .ToArray());
                }
                else
                {
                    PlayerSettings.SetGraphicsAPIs(
                        buidTarget,
                        new[] { GraphicsDeviceType.Direct3D12 }
                            .Concat(PlayerSettings.GetGraphicsAPIs(buidTarget))
                            .ToArray());
                }
                if (fromAsync)
                    m_Fixer.Stop();
                ChangedFirstGraphicAPI(buidTarget);
            }
        }

        void ChangedFirstGraphicAPI(BuildTarget target)
        {
            //It seams that the 64 version is not check for restart for a strange reason
            if (target == BuildTarget.StandaloneWindows64)
                target = BuildTarget.StandaloneWindows;

            // If we're changing the first API for relevant editor, this will cause editor to switch: ask for scene save & confirmation
            if (WillEditorUseFirstGraphicsAPI(target))
            {
                if (EditorUtility.DisplayDialog("Changing editor graphics device",
                    "You've changed the active graphics API. This requires a restart of the Editor. After restarting finish fixing DXR configuration by launching the wizard again.",
                    "Restart Editor", "Not now"))
                {
                    if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    {
                        EditorApplication.RequestCloseAndRelaunchWithCurrentArguments();
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        bool IsDXRAssetCorrect()
            => HDRenderPipeline.defaultAsset != null
            && HDRenderPipeline.defaultAsset.renderPipelineRayTracingResources != null;
        void FixDXRAsset(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);
            HDRenderPipeline.defaultAsset.renderPipelineRayTracingResources
                = AssetDatabase.LoadAssetAtPath<HDRenderPipelineRayTracingResources>(HDUtils.GetHDRenderPipelinePath() + "Runtime/RenderPipelineResources/HDRenderPipelineRayTracingResources.asset");
            ResourceReloader.ReloadAllNullIn(HDRenderPipeline.defaultAsset.renderPipelineRayTracingResources, HDUtils.GetHDRenderPipelinePath());
        }
        
        bool IsDXRShaderConfigCorrect()
        {
            if (!lastPackageConfigInstalledCheck)
                return false;
            
            bool found = false;
            using (StreamReader streamReader = new StreamReader("LocalPackages/com.unity.render-pipelines.high-definition-config/Runtime/ShaderConfig.cs.hlsl"))
            {
                while (!streamReader.EndOfStream && !found)
                    found = streamReader.ReadLine().Contains("#define SHADEROPTIONS_RAYTRACING (1)");
            }
            return found;
        }
        void FixDXRShaderConfig(bool fromAsyncUnused)
        {
            Debug.Log("Fixing DXRShaderConfig");
            if (!lastPackageConfigInstalledCheck)
            {
                InstallLocalConfigurationPackage(() => FixDXRShaderConfig(false));
            }
            else
            {
                // Then we want to make sure that the shader config value is set to 1
                string[] lines = System.IO.File.ReadAllLines("LocalPackages/com.unity.render-pipelines.high-definition-config/Runtime/ShaderConfig.cs.hlsl");
                for (int lineIdx = 0; lineIdx < lines.Length; ++lineIdx)
                {
                    if (lines[lineIdx].Contains("SHADEROPTIONS_RAYTRACING"))
                    {
                        lines[lineIdx] = "#define SHADEROPTIONS_RAYTRACING (1)";
                        break;
                    }
                }
                File.WriteAllLines("LocalPackages/com.unity.render-pipelines.high-definition-config/Runtime/ShaderConfig.cs.hlsl", lines);
            }
        }

        bool IsDXRScreenSpaceShadowCorrect()
            => HDRenderPipeline.currentAsset != null
            && HDRenderPipeline.currentAsset.currentPlatformRenderPipelineSettings.hdShadowInitParams.supportScreenSpaceShadows;
        void FixDXRScreenSpaceShadow(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);
            //as property returning struct make copy, use serializedproperty to modify it
            var serializedObject = new SerializedObject(HDRenderPipeline.currentAsset);
            var propertySupportScreenSpaceShadow = serializedObject.FindProperty("m_RenderPipelineSettings.hdShadowInitParams.supportScreenSpaceShadows");
            propertySupportScreenSpaceShadow.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        bool IsDXRReflectionsCorrect()
            => HDRenderPipeline.currentAsset != null
            && HDRenderPipeline.currentAsset.currentPlatformRenderPipelineSettings.supportSSR;
        void FixDXRReflections(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);
            //as property returning struct make copy, use serializedproperty to modify it
            var serializedObject = new SerializedObject(HDRenderPipeline.currentAsset);
            var propertySSR = serializedObject.FindProperty("m_RenderPipelineSettings.supportSSR");
            propertySSR.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        bool IsDXRStaticBatchingCorrect()
        {
            int staticBatching, dynamicBatching;
            PlayerSettings.GetBatchingForPlatform(EditorUserBuildSettingsUtils.CalculateSelectedBuildTarget(), out staticBatching, out dynamicBatching);
            return staticBatching != 1;
        }
        void FixDXRStaticBatching(bool fromAsyncUnused)
        {
            int staticBatching, dynamicBatching;
            BuildTarget target = EditorUserBuildSettingsUtils.CalculateSelectedBuildTarget();
            PlayerSettings.GetBatchingForPlatform(target, out staticBatching, out dynamicBatching);
            PlayerSettings.SetBatchingForPlatform(target, 0, dynamicBatching);
        }

        bool IsDXRActivationCorrect()
            => HDRenderPipeline.currentAsset != null
            && HDRenderPipeline.currentAsset.currentPlatformRenderPipelineSettings.supportRayTracing;
        void FixDXRActivation(bool fromAsyncUnused)
        {
            if (!IsHdrpAssetUsedCorrect())
                FixHdrpAssetUsed(fromAsync: false);
            //as property returning struct make copy, use serializedproperty to modify it
            var serializedObject = new SerializedObject(HDRenderPipeline.currentAsset);
            var propertySupportRayTracing = serializedObject.FindProperty("m_RenderPipelineSettings.supportRayTracing");
            propertySupportRayTracing.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        bool IsDXRDefaultSceneCorrect()
            => HDProjectSettings.defaultDXRScenePrefab != null;
        void FixDXRDefaultScene(bool fromAsync)
        {
            if (ObjectSelectorUtility.opened)
                return;
            CreateOrLoadDefaultScene(fromAsync ? () => m_Fixer.Stop() : (Action)null, scene => HDProjectSettings.defaultDXRScenePrefab = scene, forDXR: true);
            m_DefaultDXRScene.SetValueWithoutNotify(HDProjectSettings.defaultDXRScenePrefab);
        }

        #endregion

        #region Packman

        const string k_HdrpPackageName = "com.unity.render-pipelines.high-definition";
        const string k_HdrpConfigPackageName = "com.unity.render-pipelines.high-definition-config";
        const string k_LocalHdrpConfigPackagePath = "LocalPackages/com.unity.render-pipelines.high-definition-config";
        bool lastPackageConfigInstalledCheck = false;
        void IsLocalConfigurationPackageInstalledAsync(Action<bool> callback)
        {
            if (!Directory.Exists(k_LocalHdrpConfigPackagePath))
            {
                callback?.Invoke(lastPackageConfigInstalledCheck = false);
                return;
            }
            
            m_UsedPackageRetriever.ProcessAsync(
                k_HdrpConfigPackageName,
                info =>
                {
                    DirectoryInfo directoryInfo = new DirectoryInfo(info.resolvedPath);
                    string recomposedPath = $"{directoryInfo.Parent.Name}{Path.DirectorySeparatorChar}{directoryInfo.Name}";
                    lastPackageConfigInstalledCheck =
                        info.source == PackageManager.PackageSource.Local
                        && info.resolvedPath.EndsWith(recomposedPath);
                    callback?.Invoke(lastPackageConfigInstalledCheck);
                });
        }

        void InstallLocalConfigurationPackage(Action onCompletion)
            => m_UsedPackageRetriever.ProcessAsync(
                k_HdrpConfigPackageName,
                info =>
                {
                    if (!Directory.Exists(k_LocalHdrpConfigPackagePath))
                    {
                        CopyFolder(info.resolvedPath, k_LocalHdrpConfigPackagePath);
                    }
                    
                    PackageManager.Client.Add($"file:../{k_LocalHdrpConfigPackagePath}");
                    lastPackageConfigInstalledCheck = true;
                    onCompletion?.Invoke();
                });
        
        void RefreshDisplayOfConfigPackageArea()
        {
            if (!m_UsedPackageRetriever.isRunning)
                IsLocalConfigurationPackageInstalledAsync(present => UpdateDisplayOfConfigPackageArea(present ? ConfigPackageState.Present : ConfigPackageState.Missing));
        }

        static void CopyFolder(string sourceFolder, string destFolder)
        {
            if (!Directory.Exists(destFolder))
                Directory.CreateDirectory(destFolder);
            string[] files = Directory.GetFiles(sourceFolder);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string dest = Path.Combine(destFolder, name);
                File.Copy(file, dest);
            }
            string[] folders = Directory.GetDirectories(sourceFolder);
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                string dest = Path.Combine(destFolder, name);
                CopyFolder(folder, dest);
            }
        }

        class UsedPackageRetriever
        {
            PackageManager.Requests.ListRequest m_CurrentRequest;
            Action<PackageManager.PackageInfo> m_CurrentAction;
            string m_CurrentPackageName;

            Queue<(string packageName, Action<PackageManager.PackageInfo> action)> m_Queue = new Queue<(string packageName, Action<PackageManager.PackageInfo> action)>();
            
            bool isCurrentInProgress => m_CurrentRequest != null && !m_CurrentRequest.Equals(null) && !m_CurrentRequest.IsCompleted;

            public bool isRunning => isCurrentInProgress || m_Queue.Count() > 0;

            public void ProcessAsync(string packageName, Action<PackageManager.PackageInfo> action)
            {
                if (isCurrentInProgress)
                    m_Queue.Enqueue((packageName, action));
                else
                    Start(packageName, action);
            }

            void Start(string packageName, Action<PackageManager.PackageInfo> action)
            {
                m_CurrentAction = action;
                m_CurrentPackageName = packageName;
                m_CurrentRequest = PackageManager.Client.List(offlineMode: true, includeIndirectDependencies: true);
                EditorApplication.update += Progress;
            }

            void Progress()
            {
                //Can occures on Wizard close or if scripts reloads
                if (m_CurrentRequest == null || m_CurrentRequest.Equals(null))
                {
                    EditorApplication.update -= Progress;
                    return;
                }

                if (m_CurrentRequest.IsCompleted)
                    Finished();
            }

            void Finished()
            {
                EditorApplication.update -= Progress;
                if (m_CurrentRequest.Status == PackageManager.StatusCode.Success)
                {
                    var filteredResults = m_CurrentRequest.Result.Where(info => info.name == m_CurrentPackageName);
                    if (filteredResults.Count() == 0)
                        Debug.LogError($"Failed to find package {m_CurrentPackageName}");
                    else
                    {
                        PackageManager.PackageInfo result = filteredResults.First();
                        m_CurrentAction?.Invoke(result);
                    }
                }
                else if (m_CurrentRequest.Status >= PackageManager.StatusCode.Failure)
                    Debug.LogError($"Failed to find package {m_CurrentPackageName}. Reason: {m_CurrentRequest.Error.message}");
                else
                    Debug.LogError("Unsupported progress state " + m_CurrentRequest.Status);

                m_CurrentRequest = null;

                if (m_Queue.Count > 0)
                {
                    (string packageIdOrName, Action<PackageManager.PackageInfo> action) = m_Queue.Dequeue();
                    EditorApplication.delayCall += () => Start(packageIdOrName, action);
                }
            }
        }
        UsedPackageRetriever m_UsedPackageRetriever = new UsedPackageRetriever();
        
        class LastAvailablePackageVersionRetriever
        {
            PackageManager.Requests.SearchRequest m_CurrentRequest;
            Action<string> m_CurrentAction;
            string m_CurrentPackageName;

            Queue<(string packageName, Action<string> action)> m_Queue = new Queue<(string packageName, Action<string> action)>();

            bool isCurrentInProgress => m_CurrentRequest != null && !m_CurrentRequest.Equals(null) && !m_CurrentRequest.IsCompleted;

            public bool isRunning => isCurrentInProgress || m_Queue.Count() > 0;

            public void ProcessAsync(string packageName, Action<string> action)
            {
                if (isCurrentInProgress)
                    m_Queue.Enqueue((packageName, action));
                else
                    Start(packageName, action);
            }

            void Start(string packageName, Action<string> action)
            {
                m_CurrentAction = action;
                m_CurrentPackageName = packageName;
                m_CurrentRequest = PackageManager.Client.Search(packageName, offlineMode: false);
                EditorApplication.update += Progress;
            }

            void Progress()
            {
                //Can occures on Wizard close or if scripts reloads
                if (m_CurrentRequest == null || m_CurrentRequest.Equals(null))
                {
                    EditorApplication.update -= Progress;
                    return;
                }

                if (m_CurrentRequest.IsCompleted)
                    Finished();
            }

            void Finished()
            {
                EditorApplication.update -= Progress;
                if (m_CurrentRequest.Status == PackageManager.StatusCode.Success)
                {
                    string lastVersion = m_CurrentRequest.Result[0].versions.latestCompatible;
                    m_CurrentAction?.Invoke(lastVersion);
                }
                else if (m_CurrentRequest.Status >= PackageManager.StatusCode.Failure)
                    Debug.LogError($"Failed to find package {m_CurrentPackageName}. Reason: {m_CurrentRequest.Error.message}");
                else
                    Debug.LogError("Unsupported progress state " + m_CurrentRequest.Status);

                m_CurrentRequest = null;

                if (m_Queue.Count > 0)
                {
                    (string packageIdOrName, Action<string> action) = m_Queue.Dequeue();
                    EditorApplication.delayCall += () => Start(packageIdOrName, action);
                }
            }
        }
        LastAvailablePackageVersionRetriever m_LastAvailablePackageRetriever = new LastAvailablePackageVersionRetriever();
        #endregion
    }
}
