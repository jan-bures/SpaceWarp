﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using I2.Loc;
using SpaceWarpPatcher;
using Newtonsoft.Json;
using SpaceWarp.API.Assets;
using SpaceWarp.API.Loading;
using SpaceWarp.API.Mods;
using SpaceWarp.API.Mods.JSON;
using SpaceWarp.API.UI.Appbar;
using SpaceWarp.Backend.UI.Appbar;
using SpaceWarp.UI;
using UnityEngine;

namespace SpaceWarp;

/// <summary>
///     Handles all the SpaceWarp initialization and mod processing.
/// </summary>
internal static class SpaceWarpManager
{
    internal static ManualLogSource Logger;
    internal static ConfigurationManager.ConfigurationManager ConfigurationManager;

    internal static string SpaceWarpFolder;
    internal static IReadOnlyList<BaseSpaceWarpPlugin> SpaceWarpPlugins;
    internal static IReadOnlyList<BaseUnityPlugin> NonSpaceWarpPlugins;
    internal static IReadOnlyList<(BaseUnityPlugin, ModInfo)> NonSpaceWarpInfos;

    internal static IReadOnlyList<(PluginInfo, ModInfo)> DisabledInfoPlugins;
    internal static IReadOnlyList<PluginInfo> DisabledNonInfoPlugins;
    internal static IReadOnlyList<(string, bool)> PluginGuidEnabledStatus;

    internal static readonly Dictionary<string, bool> ModsOutdated = new();
    internal static readonly Dictionary<string, bool> ModsUnsupported = new();

    private static GUISkin _skin;

    public static ModListUI ModListUI { get; internal set; }

    public static GUISkin Skin
    {
        get
        {
            if (!_skin)
            {
                AssetManager.TryGetAsset("spacewarp/swconsoleui/spacewarpconsole.guiskin", out _skin);
            }

            return _skin;
        }
    }

    internal static void GetSpaceWarpPlugins()
    {
        var pluginGuidEnabledStatus = new List<(string, bool)>();
        // obsolete warning for Chainloader.Plugins, is fine since we need ordered list
        // to break this we would likely need to upgrade to BIE 6, which isn't happening
#pragma warning disable CS0618
        var spaceWarpPlugins = Chainloader.Plugins.OfType<BaseSpaceWarpPlugin>().ToList();
        SpaceWarpPlugins = spaceWarpPlugins;

        foreach (var plugin in SpaceWarpPlugins.ToArray())
        {
            var folderPath = Path.GetDirectoryName(plugin.Info.Location);
            plugin.PluginFolderPath = folderPath;
            if (Path.GetFileName(folderPath) == "plugins")
            {
                Logger.LogError(
                    $"Found Space Warp mod {plugin.Info.Metadata.Name} in the BepInEx/plugins directory. This mod will not be initialized.");
                spaceWarpPlugins.Remove(plugin);
                continue;
            }

            var modInfoPath = Path.Combine(folderPath!, "swinfo.json");
            if (!File.Exists(modInfoPath))
            {
                Logger.LogError(
                    $"Found Space Warp plugin {plugin.Info.Metadata.Name} without a swinfo.json next to it. This mod will not be initialized.");
                spaceWarpPlugins.Remove(plugin);
                continue;
            }

            pluginGuidEnabledStatus.Add((plugin.Info.Metadata.GUID, true));
            plugin.SpaceWarpMetadata = JsonConvert.DeserializeObject<ModInfo>(File.ReadAllText(modInfoPath));
        }

        var allPlugins = Chainloader.Plugins.ToList();
        List<BaseUnityPlugin> nonSWPlugins = new();
        List<(BaseUnityPlugin, ModInfo)> nonSWInfos = new();
        foreach (var plugin in allPlugins)
        {
            if (spaceWarpPlugins.Contains(plugin as BaseSpaceWarpPlugin))
            {
                continue;
            }

            var folderPath = Path.GetDirectoryName(plugin.Info.Location);
            var modInfoPath = Path.Combine(folderPath!, "swinfo.json");
            if (File.Exists(modInfoPath))
            {
                var info = JsonConvert.DeserializeObject<ModInfo>(File.ReadAllText(modInfoPath));
                nonSWInfos.Add((plugin, info));
            }
            else
            {
                nonSWPlugins.Add(plugin);
            }
            pluginGuidEnabledStatus.Add((plugin.Info.Metadata.GUID, true));
        }
#pragma warning restore CS0618
        NonSpaceWarpPlugins = nonSWPlugins;
        NonSpaceWarpInfos = nonSWInfos;

        var disabledInfoPlugins = new List<(PluginInfo, ModInfo)>();
        var disabledNonInfoPlugins = new List<PluginInfo>();

        var disabledPlugins = ChainloaderPatch.DisabledPlugins;
        foreach (var plugin in disabledPlugins)
        {
            var folderPath = Path.GetDirectoryName(plugin.Location);
            var swInfoPath = Path.Combine(folderPath!, "swinfo.json");
            if (Path.GetFileName(folderPath) != "plugins" && File.Exists(swInfoPath))
            {
                var swInfo = JsonConvert.DeserializeObject<ModInfo>(File.ReadAllText(swInfoPath));
                disabledInfoPlugins.Add((plugin, swInfo));
            }
            else
            {
                disabledNonInfoPlugins.Add(plugin);
            }
            pluginGuidEnabledStatus.Add((plugin.Metadata.GUID, false));
        }

        DisabledInfoPlugins = disabledInfoPlugins;
        DisabledNonInfoPlugins = disabledNonInfoPlugins;
        PluginGuidEnabledStatus = pluginGuidEnabledStatus;
    }

    public static void Initialize(SpaceWarpPlugin spaceWarpPlugin)
    {
        Logger = spaceWarpPlugin.Logger;

        SpaceWarpFolder = Path.GetDirectoryName(spaceWarpPlugin.Info.Location);

        AppbarBackend.AppBarInFlightSubscriber.AddListener(Appbar.LoadAllButtons);
        AppbarBackend.AppBarOABSubscriber.AddListener(Appbar.LoadOABButtons);
    }


    internal static void CheckKspVersions()
    {
        var kspVersion = typeof(VersionID).GetField("VERSION_TEXT", BindingFlags.Static | BindingFlags.Public)
            ?.GetValue(null) as string;
        foreach (var plugin in SpaceWarpPlugins)
        {
            ModsUnsupported[plugin.SpaceWarpMetadata.ModID] =
                !plugin.SpaceWarpMetadata.SupportedKsp2Versions.IsSupported(kspVersion);
        }

        foreach (var info in NonSpaceWarpInfos)
        {
            ModsUnsupported[info.Item2.ModID] = !info.Item2.SupportedKsp2Versions.IsSupported(kspVersion);
        }

        foreach (var info in DisabledInfoPlugins)
        {
            ModsUnsupported[info.Item2.ModID] = !info.Item2.SupportedKsp2Versions.IsSupported(kspVersion);
        }
    }

    private static List<(string name, UnityObject asset)> AssetBundleLoadingAction(string internalPath, string filename)
    {
        var assetBundle = AssetBundle.LoadFromFile(filename);
        if (assetBundle == null)
        {
            throw new Exception(
                $"Failed to load AssetBundle {internalPath}");
        }

        internalPath = internalPath.Replace(".bundle", "");
        var names = assetBundle.GetAllAssetNames();
        List<(string name, UnityObject asset)> assets = new();
        foreach (var name in names)
        {
            var assetName = name;

            if (assetName.ToLower().StartsWith("assets/"))
            {
                assetName = assetName["assets/".Length..];
            }

            if (assetName.ToLower().StartsWith(internalPath + "/"))
            {
                assetName = assetName[(internalPath.Length + 1)..];
            }

            var path = internalPath + "/" + assetName;
            path = path.ToLower();
            var asset = assetBundle.LoadAsset(name);
            assets.Add((path, asset));
        }

        return assets;
    }

    private static List<(string name, UnityObject asset)> ImageLoadingAction(string internalPath, string filename)
    {
        var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false)
        {
            filterMode = FilterMode.Point 
        };
        var fileData = File.ReadAllBytes(filename);
        tex.LoadImage(fileData); // Will automatically resize
        List<(string name, UnityObject asset)> assets = new();
        assets.Add(($"images/{internalPath}",tex));
        return assets;
    }

    internal static void InitializeSpaceWarpsLoadingActions()
    {
        Loading.AddAssetLoadingAction("bundles","loading asset bundles",AssetBundleLoadingAction,"bundle");
        Loading.AddAssetLoadingAction("images","loading images",ImageLoadingAction);
    }
}