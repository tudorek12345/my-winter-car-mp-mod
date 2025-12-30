using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;
namespace Doorstop
{
    public static class Entrypoint
    {
        private static bool _bootstrapped;
        private static readonly string LogPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "BepInExBootstrap.log");

        public static void Start()
        {
            Log("Doorstop entrypoint start.");
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromBepInExCore;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (IsUnityEngineAssembly(assembly))
                {
                    Bootstrap();
                    return;
                }
            }

            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (IsUnityEngineAssembly(args.LoadedAssembly))
            {
                Log("UnityEngine loaded; bootstrapping.");
                Bootstrap();
            }
        }

        private static bool IsUnityEngineAssembly(Assembly assembly)
        {
            return assembly != null && string.Equals(assembly.GetName().Name, "UnityEngine", StringComparison.Ordinal);
        }

        private static Assembly ResolveFromBepInExCore(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name + ".dll";
                string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string bepinex = Path.Combine(root, "BepInEx");
                string candidate = Path.Combine(Path.Combine(bepinex, "core"), name);
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }
            catch (Exception ex)
            {
                Log("Resolve failed: " + ex.Message);
            }
            return null;
        }

        private static void Bootstrap()
        {
            if (_bootstrapped)
            {
                return;
            }
            _bootstrapped = true;

            try
            {
                Log("Bootstrap begin.");
            }
            catch (Exception ex)
            {
                Log("Bootstrap failed: " + ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        internal static object CreateLogEventList()
        {
            try
            {
                Type logEventArgs = Type.GetType("BepInEx.Logging.LogEventArgs, BepInEx");
                if (logEventArgs == null)
                {
                    return null;
                }

                Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(logEventArgs);
                return Activator.CreateInstance(listType);
            }
            catch (Exception ex)
            {
                Log("CreateLogEventList failed: " + ex.Message);
                return null;
            }
        }
    }

    public static class RuntimeBootstrap
    {
        [RuntimeInitializeOnLoadMethod]
        private static void OnRuntimeLoaded()
        {
            Log("RuntimeInitializeOnLoadMethod fired.");
            try
            {
                Type chainloader = Type.GetType("BepInEx.Bootstrap.Chainloader, BepInEx");
                if (chainloader == null)
                {
                    Log("Chainloader not found.");
                    return;
                }

                MethodInfo initialize = chainloader.GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo start = chainloader.GetMethod("Start", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (initialize != null)
                {
                    Log("Chainloader.Initialize");
                    string exePath = Process.GetCurrentProcess().MainModule.FileName;
                    object logEvents = Entrypoint.CreateLogEventList();
                    initialize.Invoke(null, new object[] { exePath, false, logEvents });
                    Log("Chainloader.Initialize done");
                }
                if (start != null)
                {
                    Log("Chainloader.Start");
                    start.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                Log("Chainloader failed: " + ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(root, "BepInExBootstrap.log");
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
