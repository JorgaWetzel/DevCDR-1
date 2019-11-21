﻿using DevCDRAgent.Modules;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RuckZuck.Base;
using RZUpdate;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Xml;


namespace DevCDRAgent
{
    public partial class Service1 : ServiceBase
    {
        private System.Timers.Timer tReCheck = new System.Timers.Timer(61000); //1min
        private System.Timers.Timer tReInit = new System.Timers.Timer(120100); //2min
        private DateTime tLastStatus = new DateTime();
        private DateTime tLastPSAsync = new DateTime();
        private long lConnectCount = 0;

        private static string Hostname = Environment.MachineName;
        private static HubConnection connection;
        private static string sScriptResult = "";
        private static X509AgentCert xAgent;
        private bool isconnected = false;
        private bool isstopping = false;
        public string Uri { get; set; } = Properties.Settings.Default.Endpoint;

        static readonly object _locker = new object();

        public Service1(string Host)
        {
            if (!string.IsNullOrEmpty(Host))
                Hostname = Host;

            InitializeComponent();
        }

        internal void TReInit_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                Random rnd = new Random();
                tReInit.Interval = 150100 + rnd.Next(1, 30000); //randomize ReInit intervall

                if (connection != null && isconnected)
                {
                    if (string.IsNullOrEmpty(Properties.Settings.Default.AgentSignature))
                        connection.SendAsync("Init", Hostname);
                    else
                    {
                        connection.SendAsync("InitCert", Hostname, Properties.Settings.Default.AgentSignature);
                    }


                    if (Hostname == Environment.MachineName) //No Inventory or Healthcheck if agent is running as user or with custom Name
                    {
                        if (Properties.Settings.Default.InventoryCheckHours > 0) //Inventory is enabled
                        {
                            var tLastCheck = DateTime.Now - Properties.Settings.Default.InventorySuccess;

                            //Run Inventory every x Hours
                            if (tLastCheck.TotalHours >= Properties.Settings.Default.InventoryCheckHours)
                            {
                                lock (_locker)
                                {
                                    Properties.Settings.Default.InventorySuccess = DateTime.Now;

                                    try
                                    {
                                        Trace.WriteLine(DateTime.Now.ToString() + " starting Inventory...");
                                        Trace.Flush();
                                    }
                                    catch { }

                                    connection.SendAsync("Inventory", Hostname);

                                    Properties.Settings.Default.Save();
                                    Properties.Settings.Default.Reload();
                                }
                            }
                        }

                        if (Properties.Settings.Default.HealtchCheckMinutes > 0) //Healthcheck is enabled
                        {
                            var tLastCheck = DateTime.Now - Properties.Settings.Default.HealthCheckSuccess;

                            //Run HealthChekc every x Minutes
                            if (tLastCheck.TotalMinutes >= Properties.Settings.Default.HealtchCheckMinutes)
                            {
                                lock (_locker)
                                {
                                    Properties.Settings.Default.HealthCheckSuccess = DateTime.Now;

                                    try
                                    {
                                        Trace.WriteLine(DateTime.Now.ToString() + " starting HealthCheck...");
                                        Trace.Flush();
                                    }
                                    catch { }

                                    connection.SendAsync("HealthCheck", Hostname);

                                    Properties.Settings.Default.Save();
                                    Properties.Settings.Default.Reload();
                                }
                            }
                        }
                    }

                }

                if (!isconnected)
                {
                    OnStart(new string[0]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Trace.Write(DateTime.Now.ToString() + " ERROR ReInit: " + ex.Message);
                OnStart(null);
            }
        }

        private void TReCheck_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                if (!isconnected)
                {
                    OnStart(null);
                }
            }
            catch { }
        }

        protected override void OnStart(string[] args)
        {
            isstopping = false;
            sScriptResult = DateTime.Now.ToString();
            tReCheck.Elapsed -= TReCheck_Elapsed;
            tReCheck.Elapsed += TReCheck_Elapsed;
            tReCheck.Enabled = true;
            tReCheck.AutoReset = true;

            tReInit.Elapsed -= TReInit_Elapsed;
            tReInit.Elapsed += TReInit_Elapsed;
            tReInit.Enabled = true;
            tReInit.AutoReset = true;

            if (connection != null)
            {
                try
                {
                    connection.DisposeAsync().Wait(1000);
                }
                catch { }
            }
            connection = new HubConnectionBuilder().WithUrl(Uri).Build();

            connection.Closed += async (error) =>
            {
                if (!isstopping)
                {
                    try
                    {
                        await Task.Delay(new Random().Next(0, 5) * 1000); // wait 0-5s
                        await connection.StartAsync();
                        isconnected = true;
                        Console.WriteLine("Connected with " + Uri);
                        Properties.Settings.Default.LastConnection = DateTime.Now;
                        Properties.Settings.Default.ConnectionErrors = 0;
                        Properties.Settings.Default.Save();
                        Properties.Settings.Default.Reload();
                        Connect();

                    }
                    catch (Exception ex)
                    {
                        isconnected = false;
                        Console.WriteLine(ex.Message);
                        Trace.WriteLine("\tError: " + ex.Message + " " + DateTime.Now.ToString());
                        Random rnd = new Random();
                        tReInit.Interval = 10000 + rnd.Next(1, 90000); //randomize ReInit intervall
                        Program.MinimizeFootprint();
                    }
                }
            };

            try
            {
                connection.StartAsync().Wait();
                isconnected = true;
                Console.WriteLine("Connected with " + Uri);
                Trace.WriteLine("Connected with " + Uri + " " + DateTime.Now.ToString());
                Properties.Settings.Default.LastConnection = DateTime.Now;
                Properties.Settings.Default.ConnectionErrors = 0;
                Properties.Settings.Default.Save();
                Properties.Settings.Default.Reload();
                Connect();
            }
            catch (Exception ex)
            {
                isconnected = false;
                Console.WriteLine(ex.Message);
                Trace.WriteLine("\tError: " + ex.Message + " " + DateTime.Now.ToString());

                Properties.Settings.Default.ConnectionErrors++;
                Properties.Settings.Default.Save();
                Properties.Settings.Default.Reload();

                //Only fallback if we have internet...
                if (IsConnectedToInternet())
                {

                    if(Properties.Settings.Default.ConnectionErrors > 5)
                    {
                        string sDeviceID = Properties.Settings.Default.HardwareID;
                        if (string.IsNullOrEmpty(sDeviceID))
                        {
                            //Get DeviceID from PSStatus-Script
                            string sResult = "{}";
                            using (PowerShell PowerShellInstance = PowerShell.Create())
                            {
                                try
                                {
                                    PowerShellInstance.AddScript(Properties.Settings.Default.PSStatus);
                                    var PSResult = PowerShellInstance.Invoke();
                                    if (PSResult.Count() > 0)
                                    {
                                        sResult = PSResult.Last().BaseObject.ToString();
                                        sResult = sResult.Replace(Environment.MachineName, Hostname);
                                        JObject jRes = JObject.Parse(sResult);

                                        if (Properties.Settings.Default.HardwareID != jRes["id"].Value<string>())
                                        {
                                            Properties.Settings.Default.HardwareID = jRes["id"].Value<string>();
                                            Properties.Settings.Default.Save();
                                            Properties.Settings.Default.Reload();
                                            sDeviceID = jRes["id"].Value<string>();
                                        }
                                    }
                                }
                                catch (Exception er)
                                {
                                    Console.WriteLine(" There was an error: {0}", er.Message);
                                }
                            }
                        }

                        xAgent = new X509AgentCert(sDeviceID);

                        if(!string.IsNullOrEmpty(xAgent.EndpointURL))
                            Uri = xAgent.EndpointURL;

                        //string sCutomerID = SignatureVerification.findIssuingCA(Properties.Settings.Default.RootCA);
                        //X509Certificate2 custcert = SignatureVerification.GetRootCert(sCutomerID);
                        //string sDNSName = custcert.GetNameInfo(X509NameType.DnsFromAlternativeName, false);

                        //if (!string.IsNullOrEmpty(sDNSName))
                        //{
                        //    if (custcert.Issuer != custcert.Subject) //do not add Endpoint from root ca
                        //    {
                        //        if ($"https://{sDNSName}/chat" != Properties.Settings.Default.Endpoint)
                        //        {
                        //            Uri = $"https://{sDNSName}/chat";
                        //        }
                        //    }
                        //}
                    }
                    //Fallback to default endpoint after 1Days and 15 Errors
                    if (((DateTime.Now - Properties.Settings.Default.LastConnection).TotalDays > 1) && (Properties.Settings.Default.ConnectionErrors >= 15))
                    {
                        if (!string.IsNullOrEmpty(xAgent.FallbackURL))
                            Uri = xAgent.FallbackURL;
                        else
                            Uri = Properties.Settings.Default.FallbackEndpoint;
                        
                        Hostname = Environment.MachineName + "_BAD";
                    }
                }
                else
                {
                    //No Internet, lets ignore connection errors...
                    Properties.Settings.Default.ConnectionErrors = 0;
                    Properties.Settings.Default.Save();
                    Properties.Settings.Default.Reload();
                }

                Random rnd = new Random();
                tReInit.Interval = 10000 + rnd.Next(1, 90000); //randomize ReInit intervall
                Program.MinimizeFootprint();
            }



        }

        private void Connect()
        {
            try
            {
                connection.On<string, string>("returnPS", (s1, s2) =>
                {
                    lock (_locker)
                    {
                        TimeSpan timeout = new TimeSpan(0, 5, 0); //default timeout = 5min
                        DateTime dStart = DateTime.Now;
                        TimeSpan dDuration = DateTime.Now - dStart;

                        using (PowerShell PowerShellInstance = PowerShell.Create())
                        {
                            Trace.WriteLine(DateTime.Now.ToString() + "\t run PS... " + s1);
                            try
                            {
                                PowerShellInstance.AddScript(s1);
                                PSDataCollection<PSObject> outputCollection = new PSDataCollection<PSObject>();

                                outputCollection.DataAdding += ConsoleOutput;
                                PowerShellInstance.Streams.Error.DataAdding += ConsoleError;

                                IAsyncResult async = PowerShellInstance.BeginInvoke<PSObject, PSObject>(null, outputCollection);
                                while (async.IsCompleted == false && dDuration < timeout)
                                {
                                    Thread.Sleep(200);
                                    dDuration = DateTime.Now - dStart;
                                    //if (tReInit.Interval > 5000)
                                    //    tReInit.Interval = 2000;
                                }

                                if (tReInit.Interval > 5000)
                                    tReInit.Interval = 2000;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("There was an error: {0}", ex.Message);
                            }
                        }

                        Program.MinimizeFootprint();
                    }
                });

                //New 0.9.0.6
                connection.On<string, string>("returnPSAsync", (s1, s2) =>
                {
                    if ((DateTime.Now - tLastPSAsync).TotalSeconds >= 2)
                    {
                        lock (_locker)
                        {
                            Trace.WriteLine(DateTime.Now.ToString() + "\t run PS async... " + s1);
                            tLastPSAsync = DateTime.Now;
                            var tSWScan = Task.Run(() =>
                            {
                                using (PowerShell PowerShellInstance = PowerShell.Create())
                                {
                                    try
                                    {
                                        PowerShellInstance.AddScript(s1);
                                        var PSResult = PowerShellInstance.Invoke();
                                        if (PSResult.Count() > 0)
                                        {
                                            string sResult = PSResult.Last().BaseObject.ToString();

                                            if (!string.IsNullOrEmpty(sResult)) //Do not return empty results
                                            {
                                                if (sResult != sScriptResult)
                                                {
                                                    sScriptResult = sResult;
                                                    Random rnd = new Random();
                                                    tReInit.Interval = rnd.Next(200, Properties.Settings.Default.StatusDelay); //wait max Xs to ReInit
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine("There was an error: {0}", ex.Message);
                                    }
                                }

                                Program.MinimizeFootprint();
                            });
                        }
                    }
                });

                connection.On<string>("init", (s1) =>
                {
                    try
                    {
                        Trace.Write(DateTime.Now.ToString() + "\t Agent init... ");
                        if (string.IsNullOrEmpty(Properties.Settings.Default.AgentSignature))
                        {
                            connection.SendAsync("Init", Hostname).ContinueWith(task1 =>
                            {
                            });
                        }
                        else
                        {
                            connection.SendAsync("InitCert", Hostname, Properties.Settings.Default.AgentSignature).ContinueWith(task1 =>
                            {
                            });
                        }

                        Trace.WriteLine(" done.");
                    }
                    catch { }
                    try
                    {
                        if (string.IsNullOrEmpty(Properties.Settings.Default.AgentSignature))
                        {
                            foreach (string sGroup in Properties.Settings.Default.Groups.Split(';'))
                            {
                                connection.SendAsync("JoinGroup", sGroup).ContinueWith(task1 =>
                                {
                                });
                            }
                        }
                        else
                        {
                            connection.InvokeAsync("JoinGroupCert", Properties.Settings.Default.AgentSignature).ContinueWith(task2 =>
                            {
                            });
                        }

                        Program.MinimizeFootprint();
                    }
                    catch { }
                });

                connection.On<string>("reinit", (s1) =>
                {
                    try
                    {
                        //Properties.Settings.Default.InventorySuccess = new DateTime();
                        //Properties.Settings.Default.HealthCheckSuccess = new DateTime();
                        //Properties.Settings.Default.Save();

                        Random rnd = new Random();
                        tReInit.Interval = rnd.Next(200, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                    }
                    catch { }
                });

                connection.On<string>("status", (s1) =>
                {
                    try
                    {
                        lock (_locker) //prevent parallel status
                        {
                            if (lConnectCount == 0)
                                tLastStatus = DateTime.Now;

                            lConnectCount++;

                            if ((DateTime.Now - tLastStatus).TotalSeconds <= 60)
                            {
                                if (lConnectCount >= 20) //max 20 status per minute
                                {
                                    Trace.Write(DateTime.Now.ToString() + "\t restarting service as Agent is looping...");
                                    RestartService();
                                    return;
                                }
                            }
                            else
                            {
                                tLastStatus = DateTime.Now;
                                lConnectCount = 0;
                            }

                            Trace.Write(DateTime.Now.ToString() + "\t send status...");
                            string sResult = "{}";
                            using (PowerShell PowerShellInstance = PowerShell.Create())
                            {
                                try
                                {
                                    PowerShellInstance.AddScript(Properties.Settings.Default.PSStatus);
                                    var PSResult = PowerShellInstance.Invoke();
                                    if (PSResult.Count() > 0)
                                    {
                                        sResult = PSResult.Last().BaseObject.ToString();
                                        sResult = sResult.Replace(Environment.MachineName, Hostname);
                                        JObject jRes = JObject.Parse(sResult);

                                        if (Properties.Settings.Default.HardwareID != jRes["id"].Value<string>())
                                        {
                                            Properties.Settings.Default.HardwareID = jRes["id"].Value<string>();
                                            Properties.Settings.Default.Save();
                                            Properties.Settings.Default.Reload();
                                        }

                                        jRes.Add("ScriptResult", sScriptResult);
                                        if (string.IsNullOrEmpty(Properties.Settings.Default.AgentSignature))
                                            jRes.Add("Groups", Properties.Settings.Default.Groups);
                                        else
                                            jRes.Add("Groups", xAgent.IssuingCA);
                                        sResult = jRes.ToString();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine(" There was an error: {0}", ex.Message);
                                }
                            }

                            //connection.InvokeAsync("Status", new object[] { Hostname, sResult }).ContinueWith(task1 =>
                            //{
                            //});
                            connection.InvokeAsync("Status", Hostname, sResult).Wait(1000);
                            Trace.WriteLine(" done.");
                            Program.MinimizeFootprint();
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.Write(DateTime.Now.ToString() + " ERROR: " + ex.Message);
                    }
                });

                connection.On<string>("version", (s1) =>
                {
                    try
                    {
                        lock (_locker)
                        {
                            Trace.Write(DateTime.Now.ToString() + "\t Get Version... ");
                            //Get File-Version
                            sScriptResult = (FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location)).FileVersion.ToString();
                            Trace.WriteLine(sScriptResult);

                            Random rnd = new Random();
                            tReInit.Interval = rnd.Next(200, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.Write(DateTime.Now.ToString() + " ERROR: " + ex.Message);
                    }
                });

                connection.On<string>("wol", (s1) =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(s1))
                        {
                            foreach (string sMAC in s1.Split(';'))
                            {
                                try
                                {
                                    WOL.WakeUp(sMAC); //Send Broadcast

                                    //Send to local Gateway
                                    foreach (NetworkInterface f in NetworkInterface.GetAllNetworkInterfaces())
                                        if (f.OperationalStatus == OperationalStatus.Up)
                                            foreach (GatewayIPAddressInformation d in f.GetIPProperties().GatewayAddresses)
                                            {
                                                //Only use IPv4
                                                if (d.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                                {
                                                    WOL.WakeUp(d.Address, 9, sMAC);
                                                }
                                            }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                });

                connection.On<string>("setinstance", (s1) =>
                {
                    Trace.WriteLine(DateTime.Now.ToString() + "\t Set instance: " + s1);
                    try
                    {
                        lock (_locker)
                        {
                            if (!string.IsNullOrEmpty(s1))
                            {
                                string sConfig = Assembly.GetExecutingAssembly().Location + ".config";
                                XmlDocument doc = new XmlDocument();
                                doc.Load(sConfig);
                                doc.SelectSingleNode("/configuration/applicationSettings/DevCDRAgent.Properties.Settings/setting[@name='Instance']/value").InnerText = s1;
                                doc.Save(sConfig);
                                RestartService();

                                //Update Advanced Installer Persistent Properties
                                RegistryKey myKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Zander Tools\\{54F5CC06-300A-4DD4-94D9-0E18B2BE8DF1}", true);
                                if (myKey != null)
                                {
                                    myKey.SetValue("INSTANCE", s1.Trim(), RegistryValueKind.String);
                                    myKey.Close();
                                }
                            }
                        }
                    }
                    catch { }
                });

                connection.On<string>("setendpoint", (s1) =>
                {
                    Trace.WriteLine(DateTime.Now.ToString() + "\t Set Endpoint: " + s1);
                    try
                    {
                        lock (_locker)
                        {
                            if (!string.IsNullOrEmpty(s1))
                            {
                                if (s1.StartsWith("https://"))
                                {
                                    string sConfig = Assembly.GetExecutingAssembly().Location + ".config";
                                    XmlDocument doc = new XmlDocument();
                                    doc.Load(sConfig);
                                    doc.SelectSingleNode("/configuration/applicationSettings/DevCDRAgent.Properties.Settings/setting[@name='Endpoint']/value").InnerText = s1;
                                    doc.Save(sConfig);

                                    //Update Advanced Installer Persistent Properties
                                    RegistryKey myKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Zander Tools\\{54F5CC06-300A-4DD4-94D9-0E18B2BE8DF1}", true);
                                    if (myKey != null)
                                    {
                                        myKey.SetValue("ENDPOINT", s1.Trim(), RegistryValueKind.String);
                                        myKey.Close();
                                    }

                                    RestartService();
                                }
                            }
                        }
                    }
                    catch { }
                });

                connection.On<string>("setgroups", (s1) =>
                {
                    Trace.WriteLine(DateTime.Now.ToString() + "\t Set Groups: " + s1);
                    try
                    {
                        lock (_locker)
                        {
                            if (!string.IsNullOrEmpty(s1))
                            {
                                string sConfig = Assembly.GetExecutingAssembly().Location + ".config";
                                XmlDocument doc = new XmlDocument();
                                doc.Load(sConfig);
                                doc.SelectSingleNode("/configuration/applicationSettings/DevCDRAgent.Properties.Settings/setting[@name='Groups']/value").InnerText = s1;
                                doc.Save(sConfig);

                                RestartService();

                                //Update Advanced Installer Persistent Properties
                                RegistryKey myKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Zander Tools\\{54F5CC06-300A-4DD4-94D9-0E18B2BE8DF1}", true);
                                if (myKey != null)
                                {
                                    myKey.SetValue("GROUPS", s1.Trim(), RegistryValueKind.String);
                                    myKey.Close();
                                }
                            }
                        }
                    }
                    catch { }
                });

                connection.On<string>("getgroups", (s1) =>
                {
                    try
                    {
                        lock (_locker)
                        {
                            if (!string.IsNullOrEmpty(s1))
                            {
                                sScriptResult = Properties.Settings.Default.Groups;

                                Random rnd = new Random();
                                tReInit.Interval = rnd.Next(200, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                            }
                        }
                    }
                    catch { }
                });

                connection.On<string>("restartservice", (s1) =>
                {
                    try
                    {
                        RestartService();
                        sScriptResult = "restart Agent...";
                    }
                    catch { }
                });

                connection.On<string>("rzinstall", (s1) =>
                {
                    RZInst(s1);
                });

                connection.On<string>("rzupdate", (s1) =>
                {
                    var tSWScan = Task.Run(() =>
                    {
                        try
                        {
                            sScriptResult = "Detecting RZ updates...";
                            Random rnd = new Random();
                            tReInit.Interval = rnd.Next(200, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit

                            RZRestAPIv2.sURL = ""; //Enforce reloading RZ URL
                            RZUpdater oUpdate = new RZUpdater();
                            RZScan oScan = new RZScan(false, false);

                            lock (_locker)
                            {
                                oScan.GetSWRepository().Wait(60000);
                                oScan.SWScan().Wait(60000);
                                oScan.CheckUpdates(null).Wait(60000);
                            }


                            if (string.IsNullOrEmpty(s1))
                            {
                                sScriptResult = oScan.NewSoftwareVersions.Count.ToString() + " RZ updates found";
                                rnd = new Random();
                                tReInit.Interval = rnd.Next(200, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                            }

                            List<string> lSW = new List<string>();
                            foreach (var oSW in oScan.NewSoftwareVersions)
                            {
                                if (string.IsNullOrEmpty(s1) || s1 == "HUB")
                                {
                                    RZInst(oSW.ShortName);
                                }
                                else
                                {
                                    var SWList = s1.Split(';');
                                    if (SWList.Contains(oSW.ShortName))
                                        RZInst(oSW.ShortName);
                                }
                            }
                        }
                        catch { }
                    });
                });

                connection.On<string>("rzscan", (s1) =>
                {

                    var tSWScan = Task.Run(() =>
                    {

                        try
                        {
                            lock (_locker)
                            {
                                sScriptResult = "Detecting updates...";
                                Random rnd = new Random();
                                tReInit.Interval = rnd.Next(2000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit

                                RZRestAPIv2.sURL = ""; //Enforce reloading RZ URL
                                RZUpdater oUpdate = new RZUpdater();
                                RZScan oScan = new RZScan(false, false);


                                oScan.GetSWRepository().Wait(30000);
                                oScan.SWScan().Wait(30000);
                                oScan.CheckUpdates(null).Wait(30000);


                                List<string> lSW = new List<string>();
                                foreach (var SW in oScan.NewSoftwareVersions)
                                {
                                    lSW.Add(SW.ShortName + " " + SW.ProductVersion + " (old:" + SW.MSIProductID + ")");
                                }

                                sScriptResult = JsonConvert.SerializeObject(lSW);
                                rnd = new Random();
                                tReInit.Interval = rnd.Next(2000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                            }
                        }
                        catch { }

                    });

                });

                connection.On<string>("inject", (s1) =>
                {
                    var tSWScan = Task.Run(() =>
                    {
                        try
                        {
                            sScriptResult = "Inject external code...";
                            try
                            {
                                ManagedInjection.Inject(s1);
                                sScriptResult = "External code executed.";
                            }
                            catch (Exception ex)
                            {
                                sScriptResult = "Injection error:" + ex.Message;
                            }
                        }
                        catch { }
                    });
                });

                connection.On<string, string>("userprocess", (cmd, arg) =>
                {
                    var tSWScan = Task.Run(() =>
                    {
                        if (string.IsNullOrEmpty(cmd))
                        {
                            cmd = Assembly.GetExecutingAssembly().Location;
                            arg = Environment.MachineName + ":" + "%USERNAME%";
                        }

                        try
                        {
                            if (string.IsNullOrEmpty(arg))
                            {
                                ProcessExtensions.StartProcessAsCurrentUser(cmd, null, null, false);
                            }
                            else
                            {
                                ProcessExtensions.StartProcessAsCurrentUser(null, cmd + " " + arg, null, false);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                    });

                });

                connection.On<string>("setAgentSignature", (s1) =>
                {
                    Trace.WriteLine(DateTime.Now.ToString() + "\t Set AgentSignature: " + s1);
                    try
                    {
                        if(string.IsNullOrEmpty(Properties.Settings.Default.CustomerID) && !string.IsNullOrEmpty(s1))
                        {
                            //Properties.Settings.Default.CustomerID = s1.Trim();
                            Trace.WriteLine(DateTime.Now.ToString() + "\t New CustomerID ?! " + s1.Trim());
                        }

                        if (!string.IsNullOrEmpty(Properties.Settings.Default.CustomerID)) //CustomerID is required !!!
                        {
                            if (!string.IsNullOrEmpty(Properties.Settings.Default.HardwareID))
                            {
                                xAgent = new X509AgentCert(Properties.Settings.Default.HardwareID);

                                if (xAgent.Certificate == null)
                                {
                                    //request machine cert...
                                    Trace.WriteLine(DateTime.Now.ToString() + "\t Requesting Machine Certificate... ");
                                    connection.InvokeAsync("GetMachineCert", Properties.Settings.Default.CustomerID, Properties.Settings.Default.HardwareID).Wait(2000); //MachineCert
                                    Thread.Sleep(2000);
                                }

                                if (xAgent.Certificate != null)
                                {
                                    if (xAgent.Exists && xAgent.Valid && xAgent.HasPrivateKey && !string.IsNullOrEmpty(xAgent.Signature))
                                    {
                                        if (Properties.Settings.Default.AgentSignature != xAgent.Signature)
                                        {
                                            Trace.WriteLine(DateTime.Now.ToString() + "\t Updating Signature... ");
                                            Properties.Settings.Default.AgentSignature = xAgent.Signature;
                                            Properties.Settings.Default.Save();
                                        }

                                        if (!string.IsNullOrEmpty(xAgent.EndpointURL))
                                        {
                                            if (Uri != xAgent.EndpointURL)
                                            {
                                                Uri = xAgent.EndpointURL;
                                                Trace.WriteLine(DateTime.Now.ToString() + "\t Endpoint URL to:" + xAgent.EndpointURL);
                                                Properties.Settings.Default.Endpoint = xAgent.EndpointURL;
                                                Properties.Settings.Default.Save();
                                            }
                                        }

                                        if (!string.IsNullOrEmpty(xAgent.FallbackURL))
                                        {
                                            if (Properties.Settings.Default.FallbackEndpoint != xAgent.FallbackURL)
                                            {
                                                Trace.WriteLine(DateTime.Now.ToString() + "\t Fallback URL to:" + xAgent.FallbackURL);
                                                Properties.Settings.Default.FallbackEndpoint = xAgent.FallbackURL;
                                                Properties.Settings.Default.Save();
                                            }
                                        }
                                    }
                                    else
                                    {
                                        try
                                        {
                                            if (string.IsNullOrEmpty(xAgent.RootCA))
                                            {
                                                Trace.WriteLine(DateTime.Now.ToString() + "\t Requesting Public Key for:" + Properties.Settings.Default.RootCA);
                                                connection.InvokeAsync("GetCert", Properties.Settings.Default.RootCA, false).Wait(1000); //request root cert
                                                Thread.Sleep(1500);
                                            }

                                            if (string.IsNullOrEmpty(xAgent.IssuingCA))
                                            {
                                                Trace.WriteLine(DateTime.Now.ToString() + "\t Requesting Public Key for:" + xAgent.Certificate.Issuer.Split('=')[1]);
                                                connection.InvokeAsync("GetCert", xAgent.Certificate.Issuer.Split('=')[1], false).Wait(1000); //request issuer cert
                                                Thread.Sleep(1500);
                                            }
                                        }
                                        catch { }

                                        Trace.WriteLine(DateTime.Now.ToString() + "\t Clearing Signature... ");
                                        Properties.Settings.Default.AgentSignature = "";
                                        Properties.Settings.Default.Save();
                                    }
                                }
                                else
                                {
                                    //request machine cert...
                                    Thread.Sleep(2000);
                                    Trace.WriteLine(DateTime.Now.ToString() + "\t Requesting Machine Certificate... ");
                                    connection.InvokeAsync("GetMachineCert", Properties.Settings.Default.CustomerID, Properties.Settings.Default.HardwareID).Wait(2000); //MachineCert
                                    Thread.Sleep(2000);
                                }
                            }
                        }

                        //string sDeviceID = Properties.Settings.Default.HardwareID;
                        //if (string.IsNullOrEmpty(sDeviceID))
                        //{
                        //    //Get DeviceID from PSStatus-Script
                        //    string sResult = "{}";
                        //    using (PowerShell PowerShellInstance = PowerShell.Create())
                        //    {
                        //        try
                        //        {
                        //            PowerShellInstance.AddScript(Properties.Settings.Default.PSStatus);
                        //            var PSResult = PowerShellInstance.Invoke();
                        //            if (PSResult.Count() > 0)
                        //            {
                        //                sResult = PSResult.Last().BaseObject.ToString();
                        //                sResult = sResult.Replace(Environment.MachineName, Hostname);
                        //                JObject jRes = JObject.Parse(sResult);

                        //                if (Properties.Settings.Default.HardwareID != jRes["id"].Value<string>())
                        //                {
                        //                    Properties.Settings.Default.HardwareID = jRes["id"].Value<string>();
                        //                    Properties.Settings.Default.Save();
                        //                    Properties.Settings.Default.Reload();
                        //                    sDeviceID = jRes["id"].Value<string>();
                        //                }
                        //            }
                        //        }
                        //        catch (Exception ex)
                        //        {
                        //            Console.WriteLine(" There was an error: {0}", ex.Message);
                        //        }
                        //    }
                        //}

                        //xAgent = new X509AgentCert(sDeviceID);


                        //string sCutomerID = Properties.Settings.Default.CustomerID;

                        //Trace.WriteLine(DateTime.Now.ToString() + "\t CustomerID = " + sCutomerID);
                        //if (string.IsNullOrEmpty(sCutomerID))
                        //{
                        //    if (string.IsNullOrEmpty(s1))
                        //        sCutomerID = SignatureVerification.findIssuingCA(Properties.Settings.Default.RootCA); //DEVCDR is the default root CA, lets find child CA's of DEVCDR
                        //    else
                        //        sCutomerID = s1;

                        //    //Write CustomerID to config File....
                        //    if (!string.IsNullOrEmpty(sCutomerID))
                        //    {
                        //        try
                        //        {
                        //            string sConfig = Assembly.GetExecutingAssembly().Location + ".config";
                        //            XmlDocument doc = new XmlDocument();
                        //            doc.Load(sConfig);
                        //            doc.SelectSingleNode("/configuration/applicationSettings/DevCDRAgent.Properties.Settings/setting[@name='CustomerID']/value").InnerText = sCutomerID;
                        //            doc.Save(sConfig);
                        //        }
                        //        catch { }
                        //    }
                        //    else
                        //    {
                        //        return;
                        //    }
                        //}

                        //Trace.WriteLine(DateTime.Now.ToString() + "\t DeviceID = " + sDeviceID);
                        //string sMSG = SignatureVerification.CreateSignature(sDeviceID, $"{sDeviceID}");

                        //if (string.IsNullOrEmpty(sMSG))
                        //{
                        //    sMSG = SignatureVerification.CreateSignature(sDeviceID, $"{sDeviceID}");
                        //    if (string.IsNullOrEmpty(sMSG))
                        //    {
                        //        //signature still missing...
                        //        Trace.WriteLine(DateTime.Now.ToString() + "\t Signature still missing... Requesting Machine Certificate.");
                        //        connection.InvokeAsync("GetMachineCert", sCutomerID, sDeviceID).Wait(2000); //MachineCert
                        //    }
                        //    else
                        //    {
                        //        Trace.WriteLine(DateTime.Now.ToString() + "\t Updating AgentSignature...");
                        //        Properties.Settings.Default.AgentSignature = sMSG;
                        //        Properties.Settings.Default.Save();
                        //        Properties.Settings.Default.Reload();
                        //    }
                        //}
                        //else
                        //{
                        //    if (Properties.Settings.Default.AgentSignature != sMSG)
                        //    {
                        //        Trace.WriteLine(DateTime.Now.ToString() + "\t Updating AgentSignature..");
                        //        Properties.Settings.Default.AgentSignature = sMSG;
                        //        Properties.Settings.Default.Save();
                        //        Properties.Settings.Default.Reload();
                        //    }
                        //}

                        Random rnd = new Random();
                        tReInit.Interval = rnd.Next(100, 5000); //wait max 1s to ReInit
                    }
                    catch { }
                });

                connection.On<string>("setCert", (s1) =>
                {
                    if (!string.IsNullOrEmpty(s1))
                    {
                        X509Certificate2 cert = new X509Certificate2();
                        try
                        {
                            cert = new X509Certificate2(Convert.FromBase64String(s1));
                        }
                        catch 
                        {
                            try
                            {
                                cert = new X509Certificate2(Convert.FromBase64String(s1), Properties.Settings.Default.HardwareID);
                            }
                            catch { }
                        }

                        if (cert.HasPrivateKey)
                        {
                            SignatureVerification.addCertToStore(cert, StoreName.My, StoreLocation.LocalMachine);

                            xAgent = new X509AgentCert(Properties.Settings.Default.HardwareID);
                            ////Check if Certificate chain is valid
                            //X509Chain ch = new X509Chain(true);
                            //ch.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            //if (!ch.Build(cert)) //validate chain
                            //{
                            //    //not valid:
                            //    //request root and issuing certs
                            //    connection.InvokeAsync("GetCert", Properties.Settings.Default.CustomerID, true); //request issuer cert
                            //    connection.InvokeAsync("GetCert", Properties.Settings.Default.RootCA, false); //request root cert

                            //}
                        }
                        else
                        {
                            //var DNSName = cert.GetNameInfo(X509NameType.DnsFromAlternativeName, false);
                            //if (!string.IsNullOrEmpty(DNSName))
                            //{
                            //    if(cert.Issuer != cert.Subject) //do not add Endpoint from root ca
                            //    {
                            //        if ($"https://{DNSName}/chat" != Properties.Settings.Default.Endpoint)
                            //        {
                            //            //update ENDPOINT URL
                            //            lock (_locker)
                            //            {
                            //                string sConfig = Assembly.GetExecutingAssembly().Location + ".config";
                            //                XmlDocument doc = new XmlDocument();
                            //                doc.Load(sConfig);
                            //                doc.SelectSingleNode("/configuration/applicationSettings/DevCDRAgent.Properties.Settings/setting[@name='Endpoint']/value").InnerText = $"https://{DNSName}/chat";
                            //                doc.Save(sConfig);

                            //                //Update Advanced Installer Persistent Properties
                            //                RegistryKey myKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Zander Tools\\{54F5CC06-300A-4DD4-94D9-0E18B2BE8DF1}", true);
                            //                if (myKey != null)
                            //                {
                            //                    myKey.SetValue("ENDPOINT", $"https://{DNSName}/chat", RegistryValueKind.String);
                            //                    myKey.Close();
                            //                }

                            //                Thread.Sleep(5000);

                            //                Trace.WriteLine(DateTime.Now.ToString() + "\t restarting Service becuase of ENDPOINT URL change...");
                            //                Uri = $"https://{DNSName}/chat";
                            //                //RestartService();
                            //            }
                            //        }
                            //    }
                            //}
                            
                            SignatureVerification.addCertToStore(cert, StoreName.Root, StoreLocation.LocalMachine);
                            xAgent = new X509AgentCert(Properties.Settings.Default.HardwareID);

                        }

                        Random rnd = new Random();
                        tReInit.Interval = rnd.Next(2000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                    }
                });

                //Get HardwareID
                if (string.IsNullOrEmpty(Properties.Settings.Default.HardwareID))
                {
                    //Get DeviceID from PSStatus-Script
                    string sResult = "{}";
                    using (PowerShell PowerShellInstance = PowerShell.Create())
                    {
                        try
                        {
                            PowerShellInstance.AddScript(Properties.Settings.Default.PSStatus);
                            var PSResult = PowerShellInstance.Invoke();
                            if (PSResult.Count() > 0)
                            {
                                sResult = PSResult.Last().BaseObject.ToString();
                                sResult = sResult.Replace(Environment.MachineName, Hostname);
                                JObject jRes = JObject.Parse(sResult);

                                if (Properties.Settings.Default.HardwareID != jRes["id"].Value<string>())
                                {
                                    Properties.Settings.Default.HardwareID = jRes["id"].Value<string>();
                                    Properties.Settings.Default.Save();
                                    Properties.Settings.Default.Reload();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(" There was an error: {0}", ex.Message);
                        }
                    }
                }

                //initial initialization...
                if (!string.IsNullOrEmpty(Properties.Settings.Default.CustomerID)) //CustomerID is required !!!
                {
                    if (!string.IsNullOrEmpty(Properties.Settings.Default.HardwareID))
                    {
                        xAgent = new X509AgentCert(Properties.Settings.Default.HardwareID);

                        if(xAgent.Certificate == null)
                        {
                            //request machine cert...
                            Trace.WriteLine(DateTime.Now.ToString() + "\t Requesting Machine Certificate... ");
                            connection.InvokeAsync("GetMachineCert", Properties.Settings.Default.CustomerID, Properties.Settings.Default.HardwareID).Wait(2000); //MachineCert
                            Thread.Sleep(2000);
                        }

                        if (xAgent.Certificate != null)
                        {
                            if (xAgent.Exists && xAgent.Valid && xAgent.HasPrivateKey && !string.IsNullOrEmpty(xAgent.Signature))
                            {
                                if (Properties.Settings.Default.AgentSignature != xAgent.Signature)
                                {
                                    Trace.WriteLine(DateTime.Now.ToString() + "\t Updating Signature... ");
                                    Properties.Settings.Default.AgentSignature = xAgent.Signature;
                                    Properties.Settings.Default.Save();
                                }

                                if (!string.IsNullOrEmpty(xAgent.EndpointURL))
                                {
                                    if (Uri != xAgent.EndpointURL)
                                    {
                                        Uri = xAgent.EndpointURL;
                                        Trace.WriteLine(DateTime.Now.ToString() + "\t Endpoint URL to:" + xAgent.EndpointURL);
                                        Properties.Settings.Default.Endpoint = xAgent.EndpointURL;
                                        Properties.Settings.Default.Save();
                                    }
                                }

                                if (!string.IsNullOrEmpty(xAgent.FallbackURL))
                                {
                                    if (Properties.Settings.Default.FallbackEndpoint != xAgent.FallbackURL)
                                    {
                                        Trace.WriteLine(DateTime.Now.ToString() + "\t Fallback URL to:" + xAgent.FallbackURL);
                                        Properties.Settings.Default.FallbackEndpoint = xAgent.FallbackURL;
                                        Properties.Settings.Default.Save();
                                    }
                                }
                            }
                            else
                            {
                                try
                                {
                                    if (string.IsNullOrEmpty(xAgent.RootCA))
                                    {
                                        Trace.WriteLine(DateTime.Now.ToString() + "\t Requesting Public Key for:" + Properties.Settings.Default.RootCA);
                                        connection.InvokeAsync("GetCert", Properties.Settings.Default.RootCA, false).Wait(1000); //request root cert
                                        Thread.Sleep(1500);
                                    }

                                    if (string.IsNullOrEmpty(xAgent.IssuingCA))
                                    {
                                        Trace.WriteLine(DateTime.Now.ToString() + "\t Requesting Public Key for:" + xAgent.Certificate.Issuer.Split('=')[1]);
                                        connection.InvokeAsync("GetCert", xAgent.Certificate.Issuer.Split('=')[1], false).Wait(1000); //request issuer cert
                                        Thread.Sleep(1500);
                                    }
                                }
                                catch { }

                                Trace.WriteLine(DateTime.Now.ToString() + "\t Clearing Signature... ");
                                Properties.Settings.Default.AgentSignature = "";
                                Properties.Settings.Default.Save();
                            }
                        }
                        else
                        {
                            //request machine cert...
                            Thread.Sleep(2000);
                            Trace.WriteLine(DateTime.Now.ToString() + "\t Requesting Machine Certificate... ");
                            connection.InvokeAsync("GetMachineCert", Properties.Settings.Default.CustomerID, Properties.Settings.Default.HardwareID).Wait(2000); //MachineCert
                            Thread.Sleep(2000);
                        }
                    }
                }

                if (string.IsNullOrEmpty(Properties.Settings.Default.AgentSignature + Properties.Settings.Default.CustomerID))
                {
                    Trace.WriteLine(DateTime.Now.ToString() + "\t AgentSignature and CustomerID missing... Starting legacy mode.");
                    Console.WriteLine("AgentSignature and CustomerID missing... Starting legacy mode.");
                    //Legacy Init
                    connection.InvokeAsync("Init", Hostname).ContinueWith(task1 =>
                    {
                        try
                        {
                            if (task1.IsFaulted)
                            {
                                Console.WriteLine("There was an error calling send: {0}", task1.Exception.GetBaseException());
                            }
                            else
                            {
                                try
                                {
                                    foreach (string sGroup in Properties.Settings.Default.Groups.Split(';'))
                                    {
                                        connection.InvokeAsync("JoinGroup", sGroup).ContinueWith(task2 =>
                                        {
                                        });
                                    }
                                    Program.MinimizeFootprint();
                                }
                                catch { }
                            }
                        }
                        catch { }
                    });

                    return;
                }

                if (string.IsNullOrEmpty(Properties.Settings.Default.AgentSignature))
                {
                    Trace.WriteLine(DateTime.Now.ToString() + "\t AgentSignature missing... Initializing Certificate handshake.");
                    Console.WriteLine("AgentSignature missing... Initializing Certificate handshake.");
                    connection.InvokeAsync("InitCert", Environment.MachineName, Properties.Settings.Default.AgentSignature); //request root and issuing cert
                }
                else
                {
                    Console.WriteLine("AgentSignature exists... Starting Signature verification.");
                    
                    string sSignature = Properties.Settings.Default.AgentSignature;
                    connection.InvokeAsync("InitCert", Hostname, sSignature).ContinueWith(task1 =>
                   {
                       try
                       {
                           if (task1.IsFaulted)
                           {
                               Trace.WriteLine($"There was an error calling send: {task1.Exception.GetBaseException()}");
                               Console.WriteLine("There was an error calling send: {0}", task1.Exception.GetBaseException());
                           }
                           else
                           {
                               try
                               {
                                   Trace.WriteLine(DateTime.Now.ToString() + "\t JoiningGroup...");
                                   connection.InvokeAsync("JoinGroupCert", Properties.Settings.Default.AgentSignature).ContinueWith(task2 =>
                                   {
                                   });

                                   Program.MinimizeFootprint();
                               }
                               catch { }
                           }
                       }
                       catch { }


                   });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("There was an error: {0}", ex.Message);
            }
        }

        public void RZInst(string s1)
        {
            try
            {
                Random rnd = new Random();
                RZRestAPIv2.sURL = ""; //Enforce reloading RZ URL
                RZUpdater oRZSW = new RZUpdater();
                oRZSW.SoftwareUpdate = new SWUpdate(s1);
                
                if (string.IsNullOrEmpty(oRZSW.SoftwareUpdate.SW.ProductName))
                {
                    sScriptResult = "'" + s1 + "' is NOT available in RuckZuck...!";
                    rnd = new Random();
                    tReInit.Interval = rnd.Next(200, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                }

                if(string.IsNullOrEmpty(oRZSW.SoftwareUpdate.SW.PSInstall))
                {
                    oRZSW.SoftwareUpdate.GetInstallType();
                }

                foreach (string sPreReq in oRZSW.SoftwareUpdate.SW.PreRequisites)
                {
                    if (!string.IsNullOrEmpty(sPreReq))
                    {
                        RZUpdater oRZSWPreReq = new RZUpdater();
                        oRZSWPreReq.SoftwareUpdate = new SWUpdate(sPreReq);

                        sScriptResult = "..downloading dependencies (" + oRZSWPreReq.SoftwareUpdate.SW.ShortName + ")";
                        rnd = new Random();
                        tReInit.Interval = rnd.Next(2000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit

                        if (oRZSWPreReq.SoftwareUpdate.Download().Result)
                        {
                            sScriptResult = "..installing dependencies (" + oRZSWPreReq.SoftwareUpdate.SW.ShortName + ")";
                            rnd = new Random();
                            tReInit.Interval = rnd.Next(2000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit

                            if (oRZSWPreReq.SoftwareUpdate.Install(false, true).Result)
                            {

                            }
                            else
                            {
                                sScriptResult = oRZSWPreReq.SoftwareUpdate.SW.ShortName + " failed.";
                                rnd = new Random();
                                tReInit.Interval = rnd.Next(2000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                            }
                        }
                    }

                }

                sScriptResult = "..downloading " + oRZSW.SoftwareUpdate.SW.ShortName;
                rnd = new Random();
                tReInit.Interval = rnd.Next(2000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit

                if (oRZSW.SoftwareUpdate.Download().Result)
                {
                    sScriptResult = "..installing " + oRZSW.SoftwareUpdate.SW.ShortName;
                    rnd = new Random();
                    tReInit.Interval = rnd.Next(2000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit

                    if (oRZSW.SoftwareUpdate.Install(false, true).Result)
                    {
                        sScriptResult = "Installed: " + oRZSW.SoftwareUpdate.SW.ShortName;
                        rnd = new Random();
                        tReInit.Interval = rnd.Next(3000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                    }
                    else
                    {
                        sScriptResult = "Failed: " + oRZSW.SoftwareUpdate.SW.ShortName;
                        rnd = new Random();
                        tReInit.Interval = rnd.Next(3000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
                    }
                }
            }
            catch (Exception ex)
            {
                sScriptResult = s1 + " : " + ex.Message;
                Random rnd = new Random();
                tReInit.Interval = rnd.Next(3000, Properties.Settings.Default.StatusDelay); //wait max 5s to ReInit
            }
        }

        public void Start(string[] args)
        {
            OnStart(args);
        }

        protected override void OnStop()
        {
            try
            {
                isstopping = true;
                Trace.WriteLine(DateTime.Now.ToString() + "\t stopping DevCDRAgent...");
                Trace.Flush();
                Trace.Listeners.Clear();

                tReCheck.Enabled = false;
                tReInit.Enabled = false;
                tReCheck.Stop();
                tReInit.Stop();

                connection.StopAsync().Wait(3000);
                connection.DisposeAsync().Wait(1000);
            }
            catch { }
        }

        public void RestartService()
        {
            try
            {
                Trace.WriteLine(DateTime.Now.ToString() + "\t restarting Service..");
                Trace.Flush();
                Trace.Close();

                using (PowerShell PowerShellInstance = PowerShell.Create())
                {
                    try
                    {
                        PowerShellInstance.AddScript("powershell.exe -command stop-service DevCDRAgentCore -Force;sleep 5;start-service DevCDRAgentCore");
                        var PSResult = PowerShellInstance.Invoke();
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private void ConsoleError(object sender, DataAddingEventArgs e)
        {
            if (e.ItemAdded != null)
            {
                sScriptResult = "ERROR: " + e.ItemAdded.ToString();
                Trace.WriteLine("ERROR: " + e.ItemAdded.ToString());
            }
        }

        private void ConsoleOutput(object sender, DataAddingEventArgs e)
        {
            if (e.ItemAdded != null)
            {
                sScriptResult = e.ItemAdded.ToString();
                Trace.WriteLine(e.ItemAdded.ToString());

                if (tReInit.Interval > 5000)
                    tReInit.Interval = 2000;
            }
        }

        public static bool IsConnectedToInternet()
        {
            if (System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                using (WebClient webClient = new WebClient())
                {
                    try
                    {
                        string sResult = webClient.DownloadString("http://www.msftncsi.com/ncsi.txt");
                        if (sResult == "Microsoft NCSI")
                            return true;
                    }
                    catch { }
                }
            }

            return false;
        }
    }
}
