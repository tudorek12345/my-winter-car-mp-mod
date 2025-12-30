using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

internal static class Program
{
    private static int Main(string[] args)
    {
        string targetPath = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : @"C:\Games\My.Winter.Car\game\MyWinterCar_Data\mainData";
        string unityVersion = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
            ? args[1]
            : string.Empty;
        string classDatabasePath = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
            ? args[2]
            : string.Empty;
        string classPackagePath = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3])
            ? args[3]
            : string.Empty;
        string productNameOverride = Environment.GetEnvironmentVariable("MWC_PRODUCT_NAME") ?? string.Empty;
        string companyNameOverride = Environment.GetEnvironmentVariable("MWC_COMPANY_NAME") ?? string.Empty;

        bool targetIsDirectory = Directory.Exists(targetPath);
        if (!targetIsDirectory && !File.Exists(targetPath))
        {
            Console.WriteLine("Target not found: " + targetPath);
            return 1;
        }

        List<string> targetFiles = targetIsDirectory
            ? GetCandidateFiles(targetPath)
            : new List<string> { targetPath };

        if (targetFiles.Count == 0)
        {
            Console.WriteLine("No asset files found in: " + targetPath);
            return 1;
        }

        var manager = new AssetsManager();

        if (!TryDetectUnityVersion(manager, targetFiles[0], out string detectedVersion, out bool typeTreeEnabled))
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(unityVersion))
        {
            unityVersion = detectedVersion;
        }

        Console.WriteLine("Unity version: " + detectedVersion);
        Console.WriteLine("Type tree enabled: " + typeTreeEnabled);

        bool hasClassDatabase = LoadClassDatabase(manager, unityVersion, classDatabasePath, classPackagePath);
        if (!hasClassDatabase && !typeTreeEnabled)
        {
            Console.WriteLine("Type tree is disabled and no class database was loaded.");
            return 5;
        }

        int filesScanned = 0;
        int filesUpdated = 0;
        int playerSettingsFound = 0;

        foreach (string filePath in targetFiles)
        {
            filesScanned++;
            if (ProcessFile(manager, filePath, hasClassDatabase, ref playerSettingsFound, productNameOverride, companyNameOverride))
            {
                filesUpdated++;
            }
        }

        Console.WriteLine("Files scanned: " + filesScanned);
        Console.WriteLine("PlayerSettings files: " + playerSettingsFound);
        Console.WriteLine("Files updated: " + filesUpdated);
        return 0;
    }

    private static bool TryDetectUnityVersion(AssetsManager manager, string path, out string unityVersion, out bool typeTreeEnabled)
    {
        unityVersion = string.Empty;
        typeTreeEnabled = false;

        try
        {
            AssetsFileInstance fileInstance = manager.LoadAssetsFile(path, true);
            AssetsFile assetsFile = fileInstance.file;
            unityVersion = assetsFile.Metadata.UnityVersion;
            typeTreeEnabled = assetsFile.Metadata.TypeTreeEnabled;
            manager.UnloadAssetsFile(fileInstance);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to read assets file: " + path);
            Console.WriteLine(ex.Message);
            return false;
        }
    }

    private static bool LoadClassDatabase(AssetsManager manager, string unityVersion, string classDatabasePath, string classPackagePath)
    {
        bool hasClassDatabase = false;
        string resolvedClassDatabasePath = ResolvePath(
            classDatabasePath,
            Path.Combine(AppContext.BaseDirectory, unityVersion + ".dat"),
            Path.Combine(Directory.GetCurrentDirectory(), unityVersion + ".dat"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "UnityPlayerSettingsPatcher", unityVersion + ".dat"));
        string resolvedClassPackagePath = ResolvePath(
            classPackagePath,
            Path.Combine(AppContext.BaseDirectory, "classdata.tpk"),
            Path.Combine(Directory.GetCurrentDirectory(), "classdata.tpk"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "UnityPlayerSettingsPatcher", "classdata.tpk"));

        if (!string.IsNullOrEmpty(resolvedClassDatabasePath) && File.Exists(resolvedClassDatabasePath))
        {
            try
            {
                manager.LoadClassDatabase(resolvedClassDatabasePath);
                hasClassDatabase = true;
                Console.WriteLine("Loaded class database: " + resolvedClassDatabasePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to load class database file: " + ex.Message);
                Console.WriteLine("Class database path: " + resolvedClassDatabasePath);
            }
        }

        if (!hasClassDatabase && !string.IsNullOrEmpty(resolvedClassPackagePath) && File.Exists(resolvedClassPackagePath))
        {
            try
            {
                manager.LoadClassPackage(resolvedClassPackagePath);
                manager.LoadClassDatabaseFromPackage(new UnityVersion(unityVersion));
                hasClassDatabase = true;
                Console.WriteLine("Loaded class database from package: " + resolvedClassPackagePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to load class database from package: " + ex.Message);
                Console.WriteLine("Class package path: " + resolvedClassPackagePath);
            }
        }

        if (!hasClassDatabase)
        {
            Console.WriteLine("No class database found.");
            Console.WriteLine("Expected .dat: " + (string.IsNullOrEmpty(classDatabasePath) ? unityVersion + ".dat" : classDatabasePath));
            Console.WriteLine("Or package: " + (string.IsNullOrEmpty(classPackagePath) ? "classdata.tpk" : classPackagePath));
        }

        return hasClassDatabase;
    }

    private static bool ProcessFile(AssetsManager manager, string path, bool hasClassDatabase, ref int playerSettingsFound, string productNameOverride, string companyNameOverride)
    {
        AssetsFileInstance fileInstance;
        AssetsFile assetsFile;
        try
        {
            fileInstance = manager.LoadAssetsFile(path, true);
            assetsFile = fileInstance.file;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load assets file: " + path);
            Console.WriteLine(ex.Message);
            return false;
        }

        if (!hasClassDatabase && !assetsFile.Metadata.TypeTreeEnabled)
        {
            Console.WriteLine("Skipping (no class database): " + path);
            manager.UnloadAssetsFile(fileInstance);
            return false;
        }

        var playerSettings = assetsFile.GetAssetsOfType(AssetClassID.PlayerSettings);
        if (playerSettings.Count == 0)
        {
            manager.UnloadAssetsFile(fileInstance);
            return false;
        }

        playerSettingsFound++;
        bool modified = false;
        AssetReadFlags readFlags = hasClassDatabase ? AssetReadFlags.ForceFromCldb : AssetReadFlags.None;

        foreach (AssetFileInfo info in playerSettings)
        {
            AssetTypeValueField baseField = manager.GetBaseField(fileInstance, info, readFlags);
            if (baseField == null)
            {
                continue;
            }

            AssetTypeValueField forceSingle = FindField(baseField, "forceSingleInstance");
            bool assetModified = false;
            if (forceSingle != null)
            {
                bool current = forceSingle.AsBool;
                Console.WriteLine("[" + Path.GetFileName(path) + "] forceSingleInstance: " + current);
                if (current)
                {
                    forceSingle.AsBool = false;
                    assetModified = true;
                }
            }

            AssetTypeValueField productName = FindField(baseField, "productName");
            if (productName != null)
            {
                Console.WriteLine("[" + Path.GetFileName(path) + "] productName: " + productName.AsString);
                if (!string.IsNullOrWhiteSpace(productNameOverride) &&
                    !string.Equals(productName.AsString, productNameOverride, StringComparison.Ordinal))
                {
                    productName.AsString = productNameOverride;
                    assetModified = true;
                }
            }

            AssetTypeValueField companyName = FindField(baseField, "companyName");
            if (companyName != null)
            {
                Console.WriteLine("[" + Path.GetFileName(path) + "] companyName: " + companyName.AsString);
                if (!string.IsNullOrWhiteSpace(companyNameOverride) &&
                    !string.Equals(companyName.AsString, companyNameOverride, StringComparison.Ordinal))
                {
                    companyName.AsString = companyNameOverride;
                    assetModified = true;
                }
            }

            if (assetModified)
            {
                byte[] updatedBytes = baseField.WriteToByteArray();
                info.Replacer = new ContentReplacerFromBuffer(updatedBytes);
                modified = true;
            }
        }

        if (!modified)
        {
            manager.UnloadAssetsFile(fileInstance);
            return false;
        }

        string backupPath = path + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        File.Copy(path, backupPath, true);

        string tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new AssetsFileWriter(stream))
        {
            assetsFile.Write(writer, 0);
        }

        manager.UnloadAssetsFile(fileInstance);
        File.Copy(tempPath, path, true);
        File.Delete(tempPath);

        Console.WriteLine("Updated: " + path);
        Console.WriteLine("Backup: " + backupPath);
        return true;
    }

    private static List<string> GetCandidateFiles(string directory)
    {
        var results = new List<string>();
        foreach (string path in Directory.GetFiles(directory))
        {
            string extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension) || string.Equals(extension, ".assets", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(path);
            }
        }

        return results;
    }

    private static AssetTypeValueField FindField(AssetTypeValueField root, string name)
    {
        if (root == null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (string.Equals(root.FieldName, name, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (root.Children == null)
        {
            return null;
        }

        foreach (AssetTypeValueField child in root.Children)
        {
            AssetTypeValueField match = FindField(child, name);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static string ResolvePath(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}
