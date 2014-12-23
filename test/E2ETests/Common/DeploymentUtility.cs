﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.Runtime.Infrastructure;

namespace E2ETests
{
    internal class DeploymentUtility
    {
        private static string GetIISExpressPath(KreArchitecture architecture)
        {
            // Get path to program files
            var iisExpressPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "IIS Express", "iisexpress.exe");

            // Get path to 64 bit of IIS Express
            if (architecture == KreArchitecture.amd64)
            {
                iisExpressPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IIS Express", "iisexpress.exe");

                // If process is 32 bit, the path points to x86. Replace path to point to x64
                iisExpressPath = Environment.Is64BitProcess ? iisExpressPath : iisExpressPath.Replace(" (x86)", "");
            }

            if (!File.Exists(iisExpressPath))
            {
                throw new Exception("Unable to find IISExpress on the machine");
            }

            return iisExpressPath;
        }

        /// <summary>
        /// Copy AspNet.Loader.dll to bin folder
        /// </summary>
        /// <param name="applicationPath"></param>
        private static void CopyAspNetLoader(string applicationPath)
        {
            var libraryManager = (ILibraryManager)CallContextServiceLocator.Locator.ServiceProvider.GetService(typeof(ILibraryManager));
            var interopLibrary = libraryManager.GetLibraryInformation("Microsoft.AspNet.Loader.IIS.Interop");

            var aspNetLoaderSrcPath = Path.Combine(interopLibrary.Path, "tools", "AspNet.Loader.dll");
            var aspNetLoaderDestPath = Path.Combine(applicationPath, "bin", "AspNet.Loader.dll");

            if (!File.Exists(aspNetLoaderDestPath))
            {
                File.Copy(aspNetLoaderSrcPath, aspNetLoaderDestPath);
            }
        }

        private static string APP_RELATIVE_PATH = Path.Combine("..", "..", "src", "MusicStore");

        public static Process StartApplication(StartParameters startParameters, string identityDbName)
        {
            startParameters.ApplicationPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, APP_RELATIVE_PATH));

            //To avoid the KRE_DEFAULT_LIB of the test process flowing into Helios, set it to empty
            var backupKreDefaultLibPath = Environment.GetEnvironmentVariable("KRE_DEFAULT_LIB");
            Environment.SetEnvironmentVariable("KRE_DEFAULT_LIB", string.Empty);

            if (!string.IsNullOrWhiteSpace(startParameters.EnvironmentName))
            {
                if (startParameters.ServerType != ServerType.IISNativeModule &&
                    startParameters.ServerType != ServerType.IIS)
                {
                    // To choose an environment based Startup. 
                    Environment.SetEnvironmentVariable("ASPNET_ENV", startParameters.EnvironmentName);
                }
                else
                {
                    // Cannot override with environment in case of IIS. Pack and write a Microsoft.AspNet.Hosting.ini file.
                    startParameters.PackApplicationBeforeStart = true;
                }
            }

            Process hostProcess = null;

            if (startParameters.KreFlavor == KreFlavor.Mono)
            {
                hostProcess = StartMonoHost(startParameters);
            }
            else
            {
                //Tweak the %PATH% to the point to the right KREFLAVOR
                startParameters.Kre = SwitchPathToKreFlavor(startParameters.KreFlavor, startParameters.KreArchitecture);

                //Reason to do pack here instead of in a common place is use the right KRE to do the packing. Previous line switches to use the right KRE.
                if (startParameters.PackApplicationBeforeStart)
                {
                    if (startParameters.ServerType == ServerType.IISNativeModule ||
                        startParameters.ServerType == ServerType.IIS)
                    {
                        // Pack to IIS root\application folder.
                        KpmPack(startParameters, Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") + @"\", @"inetpub\wwwroot"));

                        // Drop a Microsoft.AspNet.Hosting.ini with ASPNET_ENV information.
                        Console.WriteLine("Creating Microsoft.AspNet.Hosting.ini file with ASPNET_ENV.");
                        var iniFile = Path.Combine(startParameters.ApplicationPath, "Microsoft.AspNet.Hosting.ini");
                        File.WriteAllText(iniFile, string.Format("ASPNET_ENV={0}", startParameters.EnvironmentName));

                        // Can't use localdb with IIS. Setting an override to use InMemoryStore.
                        Console.WriteLine("Creating configoverride.json file to override default config.");
                        var overrideConfig = Path.Combine(startParameters.ApplicationPath, "..", "approot", "src", "MusicStore", "configoverride.json");
                        overrideConfig = Path.GetFullPath(overrideConfig);
                        File.WriteAllText(overrideConfig, "{\"UseInMemoryStore\": \"true\"}");

                        if (startParameters.ServerType == ServerType.IISNativeModule)
                        {
                            Console.WriteLine("Turning runAllManagedModulesForAllRequests=true in web.config.");
                            // Set runAllManagedModulesForAllRequests=true
                            var webConfig = Path.Combine(startParameters.ApplicationPath, "web.config");
                            var configuration = new XmlDocument();
                            configuration.LoadXml(File.ReadAllText(webConfig));

                            // https://github.com/aspnet/Helios/issues/77
                            var rammfarAttribute = configuration.CreateAttribute("runAllManagedModulesForAllRequests");
                            rammfarAttribute.Value = "true";
                            var modulesNode = configuration.CreateElement("modules");
                            modulesNode.Attributes.Append(rammfarAttribute);
                            var systemWebServerNode = configuration.CreateElement("system.webServer");
                            systemWebServerNode.AppendChild(modulesNode);
                            configuration.SelectSingleNode("//configuration").AppendChild(systemWebServerNode);
                            configuration.Save(webConfig);
                        }

                        Console.WriteLine("Successfully finished IIS application directory setup.");

                        Thread.Sleep(1 * 1000);
                    }
                    else
                    {
                        KpmPack(startParameters);
                    }
                }

                if (startParameters.ServerType == ServerType.IISNativeModule ||
                    startParameters.ServerType == ServerType.IIS)
                {
                    startParameters.IISApplication = new IISApplication(startParameters);
                    startParameters.IISApplication.SetupApplication();
                }
                else if (startParameters.ServerType == ServerType.IISExpress)
                {
                    hostProcess = StartHeliosHost(startParameters);
                }
                else
                {
                    hostProcess = StartSelfHost(startParameters, identityDbName);
                }
            }

            //Restore the KRE_DEFAULT_LIB after starting the host process
            Environment.SetEnvironmentVariable("KRE_DEFAULT_LIB", backupKreDefaultLibPath);
            Environment.SetEnvironmentVariable("ASPNET_ENV", string.Empty);
            return hostProcess;
        }

        private static Process StartMonoHost(StartParameters startParameters)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            var kreBin = path.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries).Where(c => c.Contains("KRE-Mono")).FirstOrDefault();

            if (string.IsNullOrWhiteSpace(kreBin))
            {
                throw new Exception("KRE not detected on the machine.");
            }

            if (startParameters.PackApplicationBeforeStart)
            {
                // We use full path to KRE to pack.
                startParameters.Kre = new DirectoryInfo(kreBin).Parent.FullName;
                KpmPack(startParameters);
            }

            //Mono does not have a way to pass in a --appbase switch. So it will be an environment variable. 
            Environment.SetEnvironmentVariable("KRE_APPBASE", startParameters.ApplicationPath);
            Console.WriteLine("Setting the KRE_APPBASE to {0}", startParameters.ApplicationPath);

            var monoPath = "mono";
            var klrMonoManaged = Path.Combine(kreBin, "klr.mono.managed.dll");
            var applicationHost = Path.Combine(kreBin, "Microsoft.Framework.ApplicationHost");

            Console.WriteLine(string.Format("Executing command: {0} {1} {2} {3}", monoPath, klrMonoManaged, applicationHost, startParameters.ServerType.ToString()));

            var startInfo = new ProcessStartInfo
            {
                FileName = monoPath,
                Arguments = string.Format("{0} {1} {2}", klrMonoManaged, applicationHost, startParameters.ServerType.ToString()),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            };

            var hostProcess = Process.Start(startInfo);
            Console.WriteLine("Started {0}. Process Id : {1}", hostProcess.MainModule.FileName, hostProcess.Id);
            Thread.Sleep(5 * 1000);

            //Clear the appbase so that it does not create issues with successive runs
            Environment.SetEnvironmentVariable("KRE_APPBASE", string.Empty);
            return hostProcess;
        }

        private static Process StartHeliosHost(StartParameters startParameters)
        {
            if (!string.IsNullOrWhiteSpace(startParameters.ApplicationHostConfigTemplateContent))
            {
                startParameters.ApplicationHostConfigTemplateContent =
                    startParameters.ApplicationHostConfigTemplateContent.Replace("[ApplicationPhysicalPath]", startParameters.ApplicationPath);
            }

            CopyAspNetLoader(startParameters.ApplicationPath);

            if (!string.IsNullOrWhiteSpace(startParameters.ApplicationHostConfigTemplateContent))
            {
                //Pass on the applicationhost.config to iis express. With this don't need to pass in the /path /port switches as they are in the applicationHost.config
                //We take a copy of the original specified applicationHost.Config to prevent modifying the one in the repo.

                var tempApplicationHostConfig = Path.GetTempFileName();
                File.WriteAllText(tempApplicationHostConfig, startParameters.ApplicationHostConfigTemplateContent.Replace("[ApplicationPhysicalPath]", startParameters.ApplicationPath));
                startParameters.ApplicationHostConfigLocation = tempApplicationHostConfig;
            }

            var parameters = string.IsNullOrWhiteSpace(startParameters.ApplicationHostConfigLocation) ?
                            string.Format("/port:5001 /path:{0}", startParameters.ApplicationPath) :
                            string.Format("/site:{0} /config:{1}", startParameters.SiteName, startParameters.ApplicationHostConfigLocation);

            var iisExpressPath = GetIISExpressPath(startParameters.KreArchitecture);

            Console.WriteLine("Executing command : {0} {1}", iisExpressPath, parameters);

            var startInfo = new ProcessStartInfo
            {
                FileName = iisExpressPath,
                Arguments = parameters,
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var hostProcess = Process.Start(startInfo);
            Console.WriteLine("Started iisexpress. Process Id : {0}", hostProcess.Id);

            return hostProcess;
        }

        private static Process StartSelfHost(StartParameters startParameters, string identityDbName)
        {
            Console.WriteLine(string.Format("Executing klr.exe --appbase {0} \"Microsoft.Framework.ApplicationHost\" {1}", startParameters.ApplicationPath, startParameters.ServerType.ToString()));

            var startInfo = new ProcessStartInfo
            {
                FileName = "klr.exe",
                Arguments = string.Format("--appbase {0} \"Microsoft.Framework.ApplicationHost\" {1}", startParameters.ApplicationPath, startParameters.ServerType.ToString()),
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var hostProcess = Process.Start(startInfo);
            //Sometimes reading MainModule returns null if called immediately after starting process.
            Thread.Sleep(1 * 1000);

            try
            {
                Console.WriteLine("Started {0}. Process Id : {1}", hostProcess.MainModule.FileName, hostProcess.Id);
            }
            catch (Win32Exception win32Exception)
            {
                Console.WriteLine("Cannot access 64 bit modules from a 32 bit process. Failed with following message : {0}", win32Exception.Message);
            }

            WaitTillDbCreated(identityDbName);

            return hostProcess;
        }

        private static string SwitchPathToKreFlavor(KreFlavor kreFlavor, KreArchitecture kreArchitecture)
        {
            var pathValue = Environment.GetEnvironmentVariable("PATH");
            Console.WriteLine();
            Console.WriteLine("Current %PATH% value : {0}", pathValue);

            var replaceStr = new StringBuilder().
                Append("KRE").
                Append((kreFlavor == KreFlavor.CoreClr) ? "-CoreCLR" : "-CLR").
                Append((kreArchitecture == KreArchitecture.x86) ? "-x86" : "-amd64").
                ToString();

            pathValue = Regex.Replace(pathValue, "KRE-(CLR|CoreCLR)-(x86|amd64)", replaceStr, RegexOptions.IgnoreCase);

            var startIndex = pathValue.IndexOf(replaceStr); // First instance of this KRE name.
            var kreName = pathValue.Substring(startIndex, pathValue.IndexOf(';', startIndex) - startIndex);
            kreName = kreName.Substring(0, kreName.IndexOf('\\')); // Trim the \bin from the path.

            // Tweak the %PATH% to the point to the right KREFLAVOR.
            Environment.SetEnvironmentVariable("PATH", pathValue);

            Console.WriteLine();
            Console.WriteLine("Changing to use KRE : {0}", kreName);
            return kreName;
        }

        private static void KpmPack(StartParameters startParameters, string packRoot = null)
        {
            startParameters.PackedApplicationRootPath = Path.Combine(packRoot ?? Path.GetTempPath(), Guid.NewGuid().ToString());

            var parameters = string.Format("pack {0} -o {1} --runtime {2}", startParameters.ApplicationPath, startParameters.PackedApplicationRootPath, startParameters.Kre);
            Console.WriteLine(string.Format("Executing command kpm {0}", parameters));

            var startInfo = new ProcessStartInfo
            {
                FileName = "kpm",
                Arguments = parameters,
                UseShellExecute = true,
                CreateNoWindow = true
            };

            var hostProcess = Process.Start(startInfo);
            hostProcess.WaitForExit(60 * 1000);

            startParameters.ApplicationPath =
                (startParameters.ServerType == ServerType.IISExpress ||
                startParameters.ServerType == ServerType.IISNativeModule ||
                startParameters.ServerType == ServerType.IIS) ?
                Path.Combine(startParameters.PackedApplicationRootPath, "wwwroot") :
                Path.Combine(startParameters.PackedApplicationRootPath, "approot", "src", "MusicStore");

            Console.WriteLine("kpm pack finished with exit code : {0}", hostProcess.ExitCode);
        }

        //In case of self-host application activation happens immediately unlike iis where activation happens on first request.
        //So in self-host case, we need a way to block the first request until the application is initialized. In MusicStore application's case, 
        //identity DB creation is pretty much the last step of application setup. So waiting on this event will help us wait efficiently.
        private static void WaitTillDbCreated(string identityDbName)
        {
            var identityDBFullPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), identityDbName + ".mdf");
            if (File.Exists(identityDBFullPath))
            {
                Console.WriteLine("Database file '{0}' exists. Proceeding with the tests.", identityDBFullPath);
                return;
            }

            Console.WriteLine("Watching for the DB file '{0}'", identityDBFullPath);
            var dbWatch = new FileSystemWatcher(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), identityDbName + ".mdf");
            dbWatch.EnableRaisingEvents = true;

            try
            {
                if (!File.Exists(identityDBFullPath))
                {
                    //Wait for a maximum of 1 minute assuming the slowest cold start.
                    var watchResult = dbWatch.WaitForChanged(WatcherChangeTypes.Created, 60 * 1000);
                    if (watchResult.ChangeType == WatcherChangeTypes.Created)
                    {
                        //This event is fired immediately after the localdb file is created. Give it a while to finish populating data and start the application.
                        Thread.Sleep(5 * 1000);
                        Console.WriteLine("Database file created '{0}'. Proceeding with the tests.", identityDBFullPath);
                    }
                    else
                    {
                        Console.WriteLine("Database file '{0}' not created", identityDBFullPath);
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("Received this exception while watching for Database file {0}", exception);
            }
            finally
            {
                dbWatch.Dispose();
            }
        }

        public static void CleanUpApplication(StartParameters startParameters, Process hostProcess, string musicStoreDbName)
        {
            if (startParameters.ServerType == ServerType.IISNativeModule ||
                startParameters.ServerType == ServerType.IIS)
            {
                // Stop & delete the application pool.
                if (startParameters.IISApplication != null)
                {
                    startParameters.IISApplication.StopAndDeleteAppPool();
                }
            }
            else if (hostProcess != null && !hostProcess.HasExited)
            {
                //Shutdown the host process
                hostProcess.Kill();
                hostProcess.WaitForExit(5 * 1000);
                if (!hostProcess.HasExited)
                {
                    Console.WriteLine("Unable to terminate the host process with process Id '{0}", hostProcess.Id);
                }
                else
                {
                    Console.WriteLine("Successfully terminated host process with process Id '{0}'", hostProcess.Id);
                }
            }
            else
            {
                Console.WriteLine("Host process already exited or never started successfully.");
            }

            if (!Helpers.RunningOnMono)
            {
                //Mono uses InMemoryStore
                DbUtils.DropDatabase(musicStoreDbName);
            }

            if (!string.IsNullOrWhiteSpace(startParameters.ApplicationHostConfigLocation))
            {
                //Delete the temp applicationHostConfig that we created
                if (File.Exists(startParameters.ApplicationHostConfigLocation))
                {
                    try
                    {
                        File.Delete(startParameters.ApplicationHostConfigLocation);
                    }
                    catch (Exception exception)
                    {
                        //Ignore delete failures - just write a log
                        Console.WriteLine("Failed to delete '{0}'. Exception : {1}", startParameters.ApplicationHostConfigLocation, exception.Message);
                    }
                }
            }

            if (startParameters.PackApplicationBeforeStart)
            {
                try
                {
                    //We've originally packed the application in a temp folder. We need to delete it. 
                    Directory.Delete(startParameters.PackedApplicationRootPath, true);
                }
                catch (Exception exception)
                {
                    Console.WriteLine("Failed to delete directory '{0}'. Exception message: {1}", startParameters.PackedApplicationRootPath, exception.Message);
                }
            }
        }
    }
}